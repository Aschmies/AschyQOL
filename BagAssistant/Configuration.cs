using Dalamud.Configuration;
using Dalamud.Plugin;
using System.Collections.Generic;

namespace BagAssistant;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

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

    /// <summary>Maximum vendor price for an item to be considered Junk.</summary>
    public int MaxJunkVendorPrice { get; set; } = 10;

    /// <summary>If true, items flagged as stackable/crafting materials are excluded from Junk.</summary>
    public bool ExcludeCraftingFromJunk { get; set; } = true;

    /// <summary>If true, items that are equipment/gear are excluded from Junk.</summary>
    public bool ExcludeGearFromJunk { get; set; } = true;

    /// <summary>If true, items that are food or medicine are excluded from Junk.</summary>
    public bool ExcludeConsumablesFromJunk { get; set; } = true;

    /// <summary>
    /// Hard ceiling on item level for junk deletion. Any item with ItemLevel above this is
    /// NEVER considered junk regardless of vendor price. Default 1 effectively blocks every
    /// piece of gear, tool, weapon, etc. — only true ilvl-0 vendor trash can be deleted.
    /// </summary>
    public int JunkMaxItemLevel { get; set; } = 1;

    /// <summary>If true, show visual zone overlays over the inventory slots.</summary>
    public bool ShowVisualZoneOverlay { get; set; } = false;

    /// <summary>Opacity of the colored zone overlays over the inventory slots (0.0 to 1.0).</summary>
    public float VisualZoneOverlayOpacity { get; set; } = 0.3f;

    /// <summary>If true, show slot numbers on the visual zone overlay.</summary>
    public bool ShowVisualZoneNumbers { get; set; } = false;

    /// <summary>If true, Apply Zones will automatically trigger a Merge Stacks process first.</summary>
    public bool ApplyZonesAutoMerge { get; set; } = true;

    /// <summary>Random per-move delay range in milliseconds, used to keep sorts human-paced.</summary>
    public int MoveDelayMinMs { get; set; } = 40;
    public int MoveDelayMaxMs { get; set; } = 80;

    /// <summary>Show a floating button strip above the inventory addon while it's open.</summary>
    public bool ShowInventoryOverlay { get; set; } = true;

    /// <summary>
    /// If true, dock the floating inventory buttons to the LEFT side of the inventory window.
    /// If false, dock them ABOVE the inventory window.
    /// </summary>
    public bool OverlayDockLeftSide { get; set; } = false;

    /// <summary>If true, Bag Assistant uses a darker FFXIV-inspired UI palette.</summary>
    public bool UseFfxivTheme { get; set; } = true;

    /// <summary>Index into <see cref="Rules"/> for the rule the overlay's rule button runs (-1 = none selected).</summary>
    public int OverlayRuleIndex { get; set; } = -1;

    /// <summary>Array of 140 strings (35 per bag * 4 bags) indicating the visual zone tag assigned to each slot.</summary>
    public string[] VisualZoneLayout { get; set; } = new string[140];

    /// <summary>
    /// Category assignment order used by Apply Zones when a slot allows multiple categories.
    /// Earlier categories claim matching shared slots first.
    /// </summary>
    public string[] ZoneCategoryPriority { get; set; } =
    [
        "Gear",
        "Consumables",
        "Materia",
        "Crafting",
        "Gathering",
        "Crystals",
        "Junk",
        "Misc",
    ];

    /// <summary>If true, log detailed categorization info to the plugin log for debugging.</summary>
    public bool DebugLogCategorization { get; set; } = false;

    private IDalamudPluginInterface? pluginInterface;
    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
        // Migrate v1 configs that were saved with the old slow defaults to the new faster pacing.
        if (Version < 2)
        {
            if (MoveDelayMinMs == 80 && MoveDelayMaxMs == 160)
            {
                MoveDelayMinMs = 40;
                MoveDelayMaxMs = 80;
            }
            Version = 2;
            Save();
        }
    }
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
