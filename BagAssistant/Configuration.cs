using Dalamud.Configuration;
using Dalamud.Plugin;
using System.Collections.Generic;

namespace BagAssistant;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Rules evaluated top-to-bottom; the first rule whose filters match an item wins.</summary>
    public List<SortRule> Rules { get; set; } = new();

    /// <summary>Player bag slots considered as both source and target unless a rule specifies its own target.</summary>
    public bool IncludeBag1 { get; set; } = true;
    public bool IncludeBag2 { get; set; } = true;
    public bool IncludeBag3 { get; set; } = true;
    public bool IncludeBag4 { get; set; } = true;

    /// <summary>If true, items that match no rule are left in place. If false, they are pushed to the lowest-numbered free slot in the included bags.</summary>
    public bool LeaveUnmatchedInPlace { get; set; } = true;

    /// <summary>If true, ask for confirmation before running a sort.</summary>
    public bool RequireConfirmation { get; set; } = true;

    /// <summary>Random per-move delay range in milliseconds, used to keep sorts human-paced.</summary>
    public int MoveDelayMinMs { get; set; } = 80;
    public int MoveDelayMaxMs { get; set; } = 160;

    /// <summary>Show a floating button strip above the inventory addon while it's open.</summary>
    public bool ShowInventoryOverlay { get; set; } = true;

    /// <summary>Index into <see cref="Rules"/> for the rule the overlay's rule button runs (-1 = none selected).</summary>
    public int OverlayRuleIndex { get; set; } = -1;

    private IDalamudPluginInterface? pluginInterface;
    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
