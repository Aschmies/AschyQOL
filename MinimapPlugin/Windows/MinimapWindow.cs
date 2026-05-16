using System;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using MinimapPlugin.Helpers;
using MinimapPlugin.Models;
using MinimapPlugin.Services;

namespace MinimapPlugin.Windows;

public sealed class MinimapWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly MapDataService mapDataService;
    private readonly EntityService entityService;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;

    public MinimapWindow(
        Configuration config,
        MapDataService mapDataService,
        EntityService entityService,
        IClientState clientState,
        ICondition condition,
        IGameGui gameGui)
        : base("##MinimapOverlay",
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize)
    {
        this.config = config;
        this.mapDataService = mapDataService;
        this.entityService = entityService;
        this.condition = condition;
        this.gameGui = gameGui;

        IsOpen = config.IsVisible;
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        // Rebuild flags dynamically so click-through and lock-position are reactive
        var flags =
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize;

        if (config.ClickThrough)
            flags |= ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMove;
        else if (config.LockPosition)
            flags |= ImGuiWindowFlags.NoMove;

        Flags = flags;

        ImGui.SetNextWindowPos(new Vector2(config.WindowX, config.WindowY), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(config.WindowSize, config.WindowSize));
        ImGui.SetNextWindowBgAlpha(0f); // We draw everything via DrawList
    }

    public override void Draw()
    {
        // Suppress during cutscenes / duties if configured
        if (config.HideInCutscenes &&
            (condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene78]))
            return;

        if (config.HideInDuties && condition[ConditionFlag.BoundByDuty])
            return;

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;

        // Track window position changes so they persist
        if (!config.LockPosition && !config.ClickThrough)
        {
            var pos = ImGui.GetWindowPos();
            if (MathF.Abs(pos.X - config.WindowX) > 0.5f || MathF.Abs(pos.Y - config.WindowY) > 0.5f)
            {
                config.WindowX = pos.X;
                config.WindowY = pos.Y;
                config.Save();
            }
        }

        // Scroll-wheel zoom
        if (ImGui.IsWindowHovered())
        {
            float delta = ImGui.GetIO().MouseWheel;
            if (delta != 0f)
            {
                config.ZoomLevel = Math.Clamp(config.ZoomLevel + delta * 0.25f, config.ZoomMin, config.ZoomMax);
                config.Save();
            }
        }

        var windowMin  = ImGui.GetWindowPos();
        float winSize  = config.WindowSize;
        var windowMax  = windowMin + new Vector2(winSize, winSize);
        var center     = windowMin + new Vector2(winSize * 0.5f, winSize * 0.5f);
        var drawList   = ImGui.GetWindowDrawList();

        // Draw a background — fully opaque black at opacity 1, invisible at 0
        uint bgColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, config.Opacity));
        drawList.AddRectFilled(windowMin, windowMax, bgColor);

        if (mapDataService.IsLoading)
        {
            ImGui.SetCursorScreenPos(windowMin + new Vector2(8, winSize * 0.5f - 8));
            ImGui.TextDisabled("Loading map...");
            return;
        }

        var mapInfo     = mapDataService.CurrentMapInfo;
        var textureWrap = mapDataService.GetCurrentTextureWrap();

        // GetCurrentTextureWrap() returns null while the ISharedImmediateTexture placeholder is 1×1.
        // This happens for a frame or two while the game texture loads from disk.
        if (mapInfo == null)
        {
            ImGui.SetCursorScreenPos(windowMin + new Vector2(8, winSize * 0.5f - 8));
            ImGui.TextDisabled("No map data");
            return;
        }

        if (textureWrap == null)
        {
            // Map info loaded but texture still decoding — skip this frame and try next
            ImGui.SetCursorScreenPos(windowMin + new Vector2(8, winSize * 0.5f - 8));
            ImGui.TextDisabled("Loading...");
            return;
        }

        int texW = textureWrap.Width;
        int texH = textureWrap.Height;
        if (texW == 0 || texH == 0) return;

        var playerPos   = localPlayer.Position;
        float playerRot = localPlayer.Rotation;

        // Check if the loaded file texture is a stub (game has no real overhead map for this zone).
        // Dawntrail city zones (Tuliyollal, Solution Nine, etc.) only have 4×4 placeholder files.
        bool fileIsStub = texW <= 16 && texH <= 16;

        // Try to grab the texture from the game's own NaviMap addon when the file is a stub.
        nint naviSrv = 0;
        int  naviW   = 0;
        int  naviH   = 0;
        if (fileIsStub)
            (naviSrv, naviW, naviH) = TryGetNaviMapBgTexture();
        bool usingNaviMap = naviSrv != 0 && naviW > 16;

        // For coordinate calculations use the expected full-res size when the file is a stub.
        int calcW = fileIsStub ? 2048 : texW;
        int calcH = fileIsStub ? 2048 : texH;

        var playerMapPixel = CoordinateHelper.WorldToMapPixel(playerPos.X, playerPos.Z, mapInfo, calcW, calcH);

        // At zoom 1.0 show calcW/4 px in each direction from the player.
        float visibleHalf = (calcW / 4.0f) / config.ZoomLevel;

        // Clip all drawing to the minimap square
        drawList.PushClipRect(windowMin, windowMax, true);

        const uint mapTint = 0xFFFFFFFF;

        // Pre-compute north-up UV range (used for debug overlay and file-texture path).
        var uvMin = new Vector2(
            (playerMapPixel.X - visibleHalf) / calcW,
            (playerMapPixel.Y - visibleHalf) / calcH);
        var uvMax = new Vector2(
            (playerMapPixel.X + visibleHalf) / calcW,
            (playerMapPixel.Y + visibleHalf) / calcH);

        // ── Draw map texture ────────────────────────────────────────────────
        if (usingNaviMap)
        {
            // NaviMap render target is always player-centred.  Map zoom is applied as a UV crop.
            float uvHalf   = 0.5f / config.ZoomLevel;
            var naviUvMin  = new Vector2(0.5f - uvHalf, 0.5f - uvHalf);
            var naviUvMax  = new Vector2(0.5f + uvHalf, 0.5f + uvHalf);
            drawList.AddImage(new ImTextureID(naviSrv), windowMin, windowMin + new Vector2(winSize, winSize),
                              naviUvMin, naviUvMax, mapTint);
        }
        else if (!fileIsStub)
        {
            if (config.RotateWithPlayer)
                DrawRotatedMap(drawList, textureWrap.Handle, texW, texH, playerMapPixel, playerRot, center, winSize, visibleHalf, mapTint);
            else
                DrawNorthUpMap(drawList, textureWrap.Handle, windowMin, winSize, uvMin, uvMax, mapTint);
        }
        // else: stub and no NaviMap fallback → leave black background

        // ── Draw entity markers (only when we have real coordinate context) ─
        if (!usingNaviMap && !fileIsStub)
        {
            var markers = entityService.GetMarkers(config);
            foreach (var marker in markers)
            {
                var markerPixel = CoordinateHelper.WorldToMapPixel(marker.WorldX, marker.WorldZ, mapInfo, calcW, calcH);
                Vector2 screenPos;

                if (config.RotateWithPlayer)
                    screenPos = CoordinateHelper.MapPixelToScreenRotated(markerPixel, playerMapPixel, center, visibleHalf, winSize * 0.5f, playerRot);
                else
                    screenPos = CoordinateHelper.MapPixelToScreen(markerPixel, playerMapPixel, center, visibleHalf, winSize * 0.5f);

                DrawMarker(drawList, screenPos, marker.Type);
                if (marker.AetheryteId != 0)
                {
                    const float hit = 8f;
                    var hitMin = screenPos - new Vector2(hit, hit);
                    var hitMax = screenPos + new Vector2(hit, hit);
                    if (ImGui.IsMouseHoveringRect(hitMin, hitMax))
                    {
                        ImGui.SetTooltip($"Teleport: {marker.Label}");
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            var entry = Plugin.AetheryteList.FirstOrDefault(a => a.AetheryteId == marker.AetheryteId);
                            if (entry != null)
                            {
                                unsafe { Telepo.Instance()->Teleport(entry.AetheryteId, entry.SubIndex); }
                            }
                        }
                    }
                }
            }
        }

        // ── Draw player arrow (always centred) ──────────────────────────────
        float arrowRot = config.RotateWithPlayer ? 0f : playerRot;
        DrawPlayerArrow(drawList, center, arrowRot);

        drawList.PopClipRect();

        // Border
        drawList.AddRect(windowMin, windowMax, 0xAA000000, 0f, ImDrawFlags.None, 1.5f);

        // Debug overlay
        float dbgY = winSize - 52;
        float dbgX = 4;
        ImGui.SetCursorScreenPos(windowMin + new Vector2(dbgX, dbgY));
        ImGui.TextDisabled($"{mapInfo.MapId} #{mapInfo.MapRowId}");
        ImGui.SetCursorScreenPos(windowMin + new Vector2(dbgX, dbgY + 12));
        if (usingNaviMap)
            ImGui.TextDisabled($"NaviMap {naviW}x{naviH}");
        else
            ImGui.TextDisabled($"tex {texW}x{texH}  {System.IO.Path.GetFileName(mapInfo.TexturePath)}");
        ImGui.SetCursorScreenPos(windowMin + new Vector2(dbgX, dbgY + 24));
        ImGui.TextDisabled($"sf={mapInfo.SizeFactor} uv({uvMin.X:F2},{uvMin.Y:F2})-({uvMax.X:F2},{uvMax.Y:F2})");
        ImGui.SetCursorScreenPos(windowMin + new Vector2(dbgX, dbgY + 36));
        ImGui.TextDisabled($"w({playerPos.X:F0},{playerPos.Z:F0}) ox={mapInfo.OffsetX} oy={mapInfo.OffsetY}");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walk the NaviMap addon's node tree to find its largest background image texture.
    /// For zones that have no pre-baked overhead map file (Dawntrail city zones), the game
    /// renders the minimap dynamically and the result lives inside the NaviMap addon tree.
    /// Returns (SRV pointer, width, height) — all zero if not available.
    /// </summary>
    private unsafe (nint Srv, int W, int H) TryGetNaviMapBgTexture()
    {
        var addonPtr = gameGui.GetAddonByName("NaviMap");
        if (addonPtr.IsNull || !addonPtr.IsVisible) return default;

        var addon = (AtkUnitBase*)(nint)addonPtr;
        if (addon->UldManager.NodeListCount == 0) return default;

        nint bestSrv = nint.Zero;
        int  bestW   = 0;
        int  bestH   = 0;

        for (var i = 0u; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Image) continue;

            var imgNode = (AtkImageNode*)node;
            if (imgNode->PartsList == null || imgNode->PartsList->PartCount == 0) continue;

            var partId   = (ushort)Math.Min(imgNode->PartId, (ushort)(imgNode->PartsList->PartCount - 1));
            var uldAsset = imgNode->PartsList->Parts[partId].UldAsset;
            if (uldAsset == null) continue;

            // GetKernelTexture is a MemberFunction — call via pointer to the field
            var atkTexPtr = &uldAsset->AtkTexture;
            var kt = atkTexPtr->GetKernelTexture();
            if (kt == null) continue;

            var srv = (nint)kt->D3D11ShaderResourceView;
            if (srv == nint.Zero) continue;

            if ((int)kt->ActualWidth > bestW)
            {
                bestW   = (int)kt->ActualWidth;
                bestH   = (int)kt->ActualHeight;
                bestSrv = srv;
            }
        }

        return (bestSrv, bestW, bestH);
    }

    private static void DrawNorthUpMap(
        ImDrawListPtr drawList,
        ImTextureID texId,
        Vector2 windowMin,
        float winSize,
        Vector2 uvMin,
        Vector2 uvMax,
        uint tint)
    {
        drawList.AddImage(texId, windowMin, windowMin + new Vector2(winSize, winSize), uvMin, uvMax, tint);
    }

    private static void DrawRotatedMap(
        ImDrawListPtr drawList,
        ImTextureID texId,
        int texW, int texH,
        Vector2 playerMapPixel,
        float rotation,
        Vector2 center,
        float winSize,
        float visibleHalf,
        uint tint)
    {
        float halfWin = winSize * 0.5f;
        float uvHalfX = visibleHalf / texW;
        float uvHalfY = visibleHalf / texH;
        var uvCenter  = new Vector2(playerMapPixel.X / texW, playerMapPixel.Y / texH);

        // Rotate UV offsets by -rotation so the map counter-rotates with the player
        float angle = -rotation;
        float cos   = MathF.Cos(angle);
        float sin   = MathF.Sin(angle);

        // TL, TR, BR, BL offsets in UV space before rotation
        Vector2[] offsets =
        [
            new(-uvHalfX, -uvHalfY),
            new( uvHalfX, -uvHalfY),
            new( uvHalfX,  uvHalfY),
            new(-uvHalfX,  uvHalfY),
        ];

        var uvs = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            var o = offsets[i];
            uvs[i] = uvCenter + new Vector2(o.X * cos - o.Y * sin, o.X * sin + o.Y * cos);
        }

        var p0 = center + new Vector2(-halfWin, -halfWin);
        var p1 = center + new Vector2( halfWin, -halfWin);
        var p2 = center + new Vector2( halfWin,  halfWin);
        var p3 = center + new Vector2(-halfWin,  halfWin);

        drawList.AddImageQuad(texId, p0, p1, p2, p3, uvs[0], uvs[1], uvs[2], uvs[3], tint);
    }

    private static void DrawMarker(ImDrawListPtr drawList, Vector2 pos, MarkerType type)
    {
        const float radius = 5f;

        uint fill = type switch
        {
            MarkerType.PartyMember => 0xFF44FF44,  // bright green
            MarkerType.OtherPlayer => 0xFFAAAAAA,  // grey
            MarkerType.Aetheryte   => 0xFFFF8800,  // orange
            MarkerType.Fate        => 0xFF00FFFF,  // cyan
            MarkerType.QuestNpc    => 0xFFFFFF00,  // yellow
            _                      => 0xFFFFFFFF,
        };

        drawList.AddCircleFilled(pos, radius, fill);
        drawList.AddCircle(pos, radius, 0xFF000000, 0, 1.5f);
    }

    private static void DrawPlayerArrow(ImDrawListPtr drawList, Vector2 center, float rotation)
    {
        const float len  = 10f;
        const float wing = 2.45f; // ~140°

        var tip   = center + new Vector2( MathF.Sin(rotation) * len,        -MathF.Cos(rotation) * len);
        var left  = center + new Vector2( MathF.Sin(rotation + wing) * len * 0.5f, -MathF.Cos(rotation + wing) * len * 0.5f);
        var right = center + new Vector2( MathF.Sin(rotation - wing) * len * 0.5f, -MathF.Cos(rotation - wing) * len * 0.5f);

        drawList.AddTriangleFilled(tip, left, right, 0xFFFFFFFF);
        drawList.AddTriangle(tip, left, right, 0xFF000000, 1.5f);
    }

    public void Dispose() { }
}
