using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace MinimapPlugin.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;

    public ConfigWindow(Configuration config)
        : base("Minimap Settings##MinimapConfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 200),
            MaximumSize = new Vector2(600, 800),
        };
    }

    public override void Draw()
    {
        DrawDisplaySection();
        ImGui.Separator();
        DrawZoomSection();
        ImGui.Separator();
        DrawRotationSection();
        ImGui.Separator();
        DrawMarkersSection();
        ImGui.Separator();
        DrawBehaviourSection();
    }

    // ── Display ──────────────────────────────────────────────────────────────

    private void DrawDisplaySection()
    {
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Display");

        var size = config.WindowSize;
        if (ImGui.SliderFloat("Window Size##winsize", ref size, 80f, 600f))
        {
            config.WindowSize = size;
            config.Save();
        }

        var opacity = config.Opacity;
        if (ImGui.SliderFloat("Opacity##opacity", ref opacity, 0f, 1f))
        {
            config.Opacity = opacity;
            config.Save();
        }

        if (ImGui.Button("Reset Position##resetPos"))
        {
            config.WindowX = 20f;
            config.WindowY = 20f;
            config.Save();
        }
    }

    // ── Zoom ─────────────────────────────────────────────────────────────────

    private void DrawZoomSection()
    {
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Zoom");
        ImGui.TextDisabled("Use the scroll wheel over the minimap to zoom in/out.");

        var zoom = config.ZoomLevel;
        if (ImGui.SliderFloat("Zoom Level##zoom", ref zoom, config.ZoomMin, config.ZoomMax))
        {
            config.ZoomLevel = zoom;
            config.Save();
        }

        var zoomMin = config.ZoomMin;
        if (ImGui.SliderFloat("Min Zoom##zmin", ref zoomMin, 0.1f, 1f))
        {
            config.ZoomMin = zoomMin;
            config.Save();
        }

        var zoomMax = config.ZoomMax;
        if (ImGui.SliderFloat("Max Zoom##zmax", ref zoomMax, 1f, 8f))
        {
            config.ZoomMax = zoomMax;
            config.Save();
        }

        if (ImGui.Button("Reset Zoom##resetZoom"))
        {
            config.ZoomLevel = 1f;
            config.ZoomMin   = 0.5f;
            config.ZoomMax   = 4f;
            config.Save();
        }
    }

    // ── Rotation ─────────────────────────────────────────────────────────────

    private void DrawRotationSection()
    {
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Rotation");

        bool northUp = !config.RotateWithPlayer;
        if (ImGui.RadioButton("North-up##northUp", northUp))
        {
            config.RotateWithPlayer = false;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Player-up##playerUp", config.RotateWithPlayer))
        {
            config.RotateWithPlayer = true;
            config.Save();
        }
    }

    // ── Markers ───────────────────────────────────────────────────────────────

    private void DrawMarkersSection()
    {
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Markers");

        Toggle("Show Party Members (green)",  config.ShowPartyMembers,  v => config.ShowPartyMembers  = v);
        Toggle("Show Other Players (grey)",   config.ShowOtherPlayers,  v => config.ShowOtherPlayers  = v);
        Toggle("Show Quest NPCs (yellow)",    config.ShowQuestMarkers,  v => config.ShowQuestMarkers  = v);
        Toggle("Show FATEs (cyan)",           config.ShowFates,         v => config.ShowFates         = v);
        Toggle("Show Aetherytes (orange)",    config.ShowAetherytes,    v => config.ShowAetherytes    = v);
    }

    // ── Behaviour ─────────────────────────────────────────────────────────────

    private void DrawBehaviourSection()
    {
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "Behaviour");

        Toggle("Hide in Cutscenes", config.HideInCutscenes, v => config.HideInCutscenes = v);
        Toggle("Hide in Duties",    config.HideInDuties,    v => config.HideInDuties    = v);
        Toggle("Lock Position",     config.LockPosition,    v => config.LockPosition    = v);
        Toggle("Click-Through",     config.ClickThrough,    v => config.ClickThrough    = v);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Toggle(string label, bool current, System.Action<bool> setter)
    {
        if (ImGui.Checkbox(label, ref current))
        {
            setter(current);
            config.Save();
        }
    }
}
