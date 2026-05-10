using BagAssistant.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.Numerics;

namespace BagAssistant.Windows;

/// <summary>
/// Floating button strip that appears next to the player's inventory addon while it is open.
/// Provides a one-click "Smart Sort" and a configurable single-rule trigger.
/// </summary>
public sealed class InventoryOverlayWindow : Window, IDisposable
{
    private static readonly string[] InventoryAddons =
    [
        "Inventory",
        "InventoryLarge",
        "InventoryExpansion",
    ];

    private readonly BagAssistantPlugin plugin;
    private readonly IGameGui gameGui;
    private Configuration Config => plugin.Configuration;

    private Vector2 anchorPos;
    private Vector2 anchorSize;

    public InventoryOverlayWindow(BagAssistantPlugin plugin, IGameGui gameGui)
        : base("##BagAssistantInventoryOverlay",
               ImGuiWindowFlags.NoTitleBar
               | ImGuiWindowFlags.NoResize
               | ImGuiWindowFlags.NoMove
               | ImGuiWindowFlags.NoScrollbar
               | ImGuiWindowFlags.NoScrollWithMouse
               | ImGuiWindowFlags.NoCollapse
               | ImGuiWindowFlags.NoSavedSettings
               | ImGuiWindowFlags.NoFocusOnAppearing
               | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        this.gameGui = gameGui;
        IsOpen = false;
        DisableWindowSounds = true;
        RespectCloseHotkey = false;
    }

    public void Dispose() { }

    public override bool DrawConditions()
    {
        if (!Config.ShowInventoryOverlay) return false;
        if (!TryGetInventoryRect(out anchorPos, out anchorSize)) return false;
        return true;
    }

    public override void PreDraw()
    {
        // Pin position above the inventory addon. Width is auto-resize.
        var pos = new Vector2(anchorPos.X, MathF.Max(0, anchorPos.Y - 36f));
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        // Slight transparency so it doesn't fight visually with the addon.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        if (plugin.IsSortQueueBusy)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"Sorting... {plugin.SortQueueRemaining}/{plugin.SortQueueTotal}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Stop##ovstop"))
                plugin.StopSort();
            return;
        }

        // Smart Sort
        if (ImGui.Button("Smart Sort##ov_smart"))
        {
            plugin.RunSmartSort();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Sort everything: Gear -> Bag1, Food/Medicine -> Bag2, Materials -> Bag3, Crystals/Materia -> Bag4.");
        }

        // Rule button (only if a rule is selected and exists)
        var rule = ResolveOverlayRule();
        ImGui.SameLine();
        if (rule != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, rule.GetColor() * new Vector4(1, 1, 1, 0.5f));
            if (ImGui.Button($"{rule.Name}##ov_rule"))
            {
                plugin.RunSingleRule(rule);
            }
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Run only this rule: {rule.Name}");
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button("(no rule)##ov_rule_none");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Pick a rule for the overlay button in Settings tab.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("BA##ov_open"))
            plugin.ToggleMainUi();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open Bag Assistant");
    }

    private SortRule? ResolveOverlayRule()
    {
        if (Config.Rules.Count == 0) return null;
        var idx = Config.OverlayRuleIndex;
        if (idx < 0 || idx >= Config.Rules.Count) return null;
        var rule = Config.Rules[idx];
        return rule.Enabled ? rule : null;
    }

    private bool TryGetInventoryRect(out Vector2 position, out Vector2 size)
    {
        position = Vector2.Zero;
        size = Vector2.Zero;

        foreach (var name in InventoryAddons)
        {
            AtkUnitBasePtr addon = gameGui.GetAddonByName(name, 1);
            if (addon.IsNull) continue;
            if (!addon.IsVisible) continue;

            var w = MathF.Max(40f, addon.ScaledWidth);
            var h = MathF.Max(40f, addon.ScaledHeight);
            position = addon.Position;
            size = new Vector2(w, h);
            return true;
        }
        return false;
    }
}
