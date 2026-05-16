using System;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using MinimapPlugin.Models;
using Lumina.Excel.Sheets;

namespace MinimapPlugin.Services;

public sealed class MapDataService : IDisposable
{
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly IPluginLog log;

    private ISharedImmediateTexture? currentTexture;
    private bool texDimensionsLogged;

    public bool IsLoading { get; private set; }
    public ZoneMapInfo? CurrentMapInfo { get; private set; }

    public MapDataService(IDataManager dataManager, ITextureProvider textureProvider, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
        this.log = log;
    }

    /// <summary>Load map texture using a Lumina Map row ID (from IClientState.MapId).</summary>
    public void LoadMapForMapId(uint mapRowId)
    {
        if (mapRowId == 0) return;

        IsLoading = true;
        currentTexture = null;
        CurrentMapInfo = null;
        texDimensionsLogged = false;

        Task.Run(() =>
        {
            try
            {
                var mapSheet = dataManager.GetExcelSheet<Map>();
                if (mapSheet == null)
                {
                    log.Warning("[Minimap] Map sheet not available.");
                    return;
                }

                var mapRow = mapSheet.GetRowOrDefault(mapRowId);
                if (mapRow == null)
                {
                    log.Warning($"[Minimap] No Map row for id {mapRowId}.");
                    return;
                }

                var map = mapRow.Value;
                var mapId = map.Id.ExtractText().TrimEnd('\0');
                var sizeFactor = map.SizeFactor == 0 ? (ushort)100 : map.SizeFactor;
                var offsetX = map.OffsetX;
                var offsetY = map.OffsetY;

                if (string.IsNullOrEmpty(mapId))
                {
                    log.Warning($"[Minimap] Empty map ID for map row {mapRowId}.");
                    return;
                }

                // Try _m.tex → _s.tex → parent zone _m.tex
                string texPath = $"ui/map/{mapId}_m.tex";
                if (!dataManager.FileExists(texPath))
                {
                    var smPath = $"ui/map/{mapId}_s.tex";
                    if (dataManager.FileExists(smPath))
                    {
                        texPath = smPath;
                    }
                    else if (mapId.Contains('/'))
                    {
                        // Sub-area with numeric ID (e.g. "x6e2/00") — try the parent zone texture
                        var folder = mapId.Split('/')[0];
                        var parentPath = $"ui/map/{folder}/{folder}_m.tex";
                        if (dataManager.FileExists(parentPath))
                        {
                            texPath = parentPath;
                            log.Info($"[Minimap] Sub-area '{mapId}' has no standalone tex; falling back to parent '{folder}/{folder}_m.tex'");
                        }
                        else
                        {
                            log.Warning($"[Minimap] No _m/_s tex found for '{mapId}', path '{texPath}' will be attempted anyway.");
                        }
                    }
                    else
                    {
                        log.Warning($"[Minimap] '{texPath}' not found in game files; will attempt anyway.");
                    }
                }

                log.Info($"[Minimap] row={mapRowId} id='{mapId}' sf={sizeFactor} ox={offsetX} oy={offsetY} path='{texPath}'");

                var info = new ZoneMapInfo
                {
                    MapRowId = mapRowId,
                    MapId = mapId,
                    TexturePath = texPath,
                    SizeFactor = sizeFactor,
                    OffsetX = offsetX,
                    OffsetY = offsetY,
                };

                CurrentMapInfo = info;
                currentTexture = textureProvider.GetFromGame(texPath);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"[Minimap] Failed to load map for map row {mapRowId}.");
            }
            finally
            {
                IsLoading = false;
            }
        });
    }

    /// <summary>Returns the current texture wrap for use within an ImGui Draw frame.
    /// Returns null while the texture is still loading (wrap size is 1×1 placeholder).</summary>
    public Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? GetCurrentTextureWrap()
    {
        var wrap = currentTexture?.GetWrapOrEmpty();
        if (wrap == null || wrap.Width <= 1) return null;

        if (!texDimensionsLogged)
        {
            texDimensionsLogged = true;
            log.Info($"[Minimap] Texture loaded: {wrap.Width}×{wrap.Height} for '{CurrentMapInfo?.TexturePath}'");
        }

        return wrap;
    }

    public void Dispose()
    {
        currentTexture = null;
    }
}
