using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BagAssistant.Services;

/// <summary>One destination assignment in a quick-sort plan.</summary>
public sealed class QuickMove
{
    public required InventoryItemInfo Item { get; init; }
    public required InventoryType DestBag { get; init; }
    public int? DestSlot { get; init; }
    public int? SrcSlotOverride { get; init; }
}

/// <summary>A one-click sort preset: a filter + destination bag with a friendly name and description.</summary>
public sealed class QuickPreset
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Func<InventoryItemInfo, bool> Match { get; init; }
    public required int DestBagIndex { get; init; }
}

public static class QuickSortPresets
{
    // Plugin logger and config (set via SetLogger)
    private static IPluginLog? _logger = null;
    private static Configuration? _config = null;
    
    public static void SetLogger(IPluginLog log) => _logger = log;
    public static void SetConfig(Configuration config) => _config = config;
    
    private static void LogCategorization(string itemName, uint uiCategoryRowId, string result, string details = "")
    {
        // Only log if debug flag is enabled
        if (_logger == null || _config == null || !_config.DebugLogCategorization) return;
        var msg = $"[Categorize] '{itemName}' UICategory={uiCategoryRowId} → {result}";
        if (!string.IsNullOrEmpty(details)) msg += $" ({details})";
        _logger.Debug(msg);
    }

    // ItemUICategory row IDs (Lumina). Comprehensive consumable & item categories:
    // CONSUMABLES:
    private const uint UICategoryMedicine = 44;      // Potions, stat boosters, etc.
    private const uint UICategoryMeal = 46;          // Food/dishes
    private const uint UICategoryDrink = 43;         // Drinks (e.g., potions in liquid form)
    private const uint UICategorySpirit = 45;        // Alcoholic beverages
    private const uint UICategoryOther = 48;         // Other consumables (includes gardening items)
    private const uint UICategoryEssence = 58;       // Often shared with Materia
    
    // CRAFTING:
    private const uint UICategoryIngredient = 47;    // Ingredients/cooking materials
    
    // GATHERING:
    private const uint UICategorySeafood = 47;       // Fish and seafood
    private const uint UICategoryGardening = 48;     // Seeds and garden items
    private const uint UICategoryRawMat1 = 38;       // Raw materials (minerals, wood, etc.)
    private const uint UICategoryRawMat2 = 39;       // Raw materials continued
    private const uint UICategoryRawMat3 = 40;       // Raw materials continued
    
    // MATERIALS:
    private const uint UICategoryCrystal = 59;       // Crystals
    private const uint UICategoryMateria = 58;       // Materia
    
    // Comprehensive consumable category check
    private static bool IsConsumableCategory(uint uiCategoryRowId) => uiCategoryRowId switch
    {
        44 => true, // Medicine/Potions
        45 => true, // Spirits/Alcohol  
        46 => true, // Meals
        43 => true, // Drinks (can vary by game version)
        _ => false,
    };

    // Reagents / Ingredients / Cooking ingredients live in many categories — easier to detect via flags below.

    private static readonly string[] DefaultZoneCategoryPriority =
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

    private static IReadOnlyList<string> ResolveZoneCategoryPriority(
        Configuration config,
        IReadOnlyDictionary<string, List<InventoryItemInfo>> byCategory)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();

        var configured = config.ZoneCategoryPriority ?? Array.Empty<string>();
        foreach (var raw in configured)
        {
            var tag = NormalizeZoneTag(raw);
            if (string.IsNullOrWhiteSpace(tag) || tag == "None") continue;
            if (seen.Add(tag)) ordered.Add(tag);
        }

        foreach (var fallback in DefaultZoneCategoryPriority)
        {
            if (seen.Add(fallback)) ordered.Add(fallback);
        }

        foreach (var key in byCategory.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (seen.Add(key)) ordered.Add(key);
        }

        return ordered;
    }

    public static readonly QuickPreset GroupGearToBag1 = new()
    {
        Name = "Gear → Bag 1",
        Description = "Move every equippable item (weapons, armor, accessories) into Bag 1.",
        Match = i => i.IsEquippable || i.Name.EndsWith("Arms") || i.Name.EndsWith("Coffer") || i.Name.EndsWith("Attire") || i.Name.EndsWith("Weapon") || i.Name.EndsWith("Equipment"),
        DestBagIndex = 0,
    };

    public static readonly QuickPreset GroupHQGearToBag1 = new()
    {
        Name = "HQ Gear → Bag 1",
        Description = "Move only HQ equippable gear into Bag 1.",
        Match = i => (i.IsEquippable || i.Name.EndsWith("Arms") || i.Name.EndsWith("Coffer") || i.Name.EndsWith("Attire") || i.Name.EndsWith("Weapon") || i.Name.EndsWith("Equipment")) && i.IsHQ,
        DestBagIndex = 0,
    };

    public static readonly QuickPreset GroupConsumablesToBag2 = new()
    {
        Name = "Food + Medicine → Bag 2",
        Description = "Move meals and medicine (potions, tinctures) into Bag 2.",
        Match = i => i.UICategoryRowId == UICategoryMeal || i.UICategoryRowId == UICategoryMedicine,
        DestBagIndex = 1,
    };

    public static readonly QuickPreset GroupCrystalsToBag4 = new()
    {
        Name = "Crystals + Materia → Bag 4",
        Description = "Move shards / crystals / clusters and materia into Bag 4.",
        Match = i => i.UICategoryRowId == UICategoryCrystal || i.UICategoryRowId == UICategoryMateria,
        DestBagIndex = 3,
    };

    public static readonly QuickPreset GroupMaterialsToBag3 = new()
    {
        Name = "Crafting Materials → Bag 3",
        Description = "Move stackable, non-equippable, non-consumable, non-crystal items (the bulk of crafting mats) into Bag 3.",
        Match = i => i.IsStackable
                     && !i.IsEquippable
                     && i.UICategoryRowId != UICategoryMeal
                     && i.UICategoryRowId != UICategoryMedicine
                     && i.UICategoryRowId != UICategoryCrystal
                     && i.UICategoryRowId != UICategoryMateria,
        DestBagIndex = 2,
    };

    public static readonly QuickPreset GroupCollectablesToBag3 = new()
    {
        Name = "Collectables → Bag 3",
        Description = "Move all collectables (gathering / crafting turn-ins) into Bag 3.",
        Match = i => i.IsCollectable,
        DestBagIndex = 2,
    };

    /// <summary>The full preset list shown in the Quick Sort tab.</summary>
    public static readonly QuickPreset[] All =
    [
        GroupGearToBag1,
        GroupHQGearToBag1,
        GroupConsumablesToBag2,
        GroupMaterialsToBag3,
        GroupCollectablesToBag3,
        GroupCrystalsToBag4,
    ];

    // ─── Advanced Presets (The Gatherer, The Raider, The Hoarder) ─────────────

    /// <summary>
    /// "The Gatherer": Crystals/Materia to Bag 4 (top priority, fast turnover), 
    /// then raw materials (stackables, mostly crafting) to Bag 3, then everything else to Bag 2.
    /// </summary>
    public static List<QuickMove> BuildGathererSort(IReadOnlyList<InventoryItemInfo> items, IReadOnlyList<bool> bagFlags)
    {
        var result = new List<QuickMove>();
        var crystalDestBag = bagFlags.Count > 3 && bagFlags[3] ? InventoryService.PlayerBags[3] : (InventoryType?)null;
        var materialDestBag = bagFlags.Count > 2 && bagFlags[2] ? InventoryService.PlayerBags[2] : (InventoryType?)null;
        var otherDestBag = bagFlags.Count > 1 && bagFlags[1] ? InventoryService.PlayerBags[1] : (InventoryType?)null;

        foreach (var item in items)
        {
            InventoryType? destBag = null;

            // Crystals/Materia first (priority)
            if ((item.UICategoryRowId == UICategoryCrystal || item.UICategoryRowId == UICategoryMateria) && crystalDestBag.HasValue)
                destBag = crystalDestBag;
            // Stackables (materials)
            else if (item.IsStackable && !item.IsEquippable && materialDestBag.HasValue)
                destBag = materialDestBag;
            // Everything else
            else if (otherDestBag.HasValue)
                destBag = otherDestBag;

            if (destBag.HasValue && item.Container != destBag)
                result.Add(new QuickMove { Item = item, DestBag = destBag.Value });
        }
        return result;
    }

    /// <summary>
    /// "The Raider": Food, potions, and combat-focused consumables to Bag 2 (quick access).
    /// All gear (prioritize high ilvl) to Bag 1. Mats/crystals to Bag 4.
    /// </summary>
    public static List<QuickMove> BuildRaiderSort(IReadOnlyList<InventoryItemInfo> items, IReadOnlyList<bool> bagFlags)
    {
        var result = new List<QuickMove>();
        var gearDestBag = bagFlags.Count > 0 && bagFlags[0] ? InventoryService.PlayerBags[0] : (InventoryType?)null;
        var consumableDestBag = bagFlags.Count > 1 && bagFlags[1] ? InventoryService.PlayerBags[1] : (InventoryType?)null;
        var crystalDestBag = bagFlags.Count > 3 && bagFlags[3] ? InventoryService.PlayerBags[3] : (InventoryType?)null;

        // Sort by: high ilvl gear first, then consumables, then crystals
        foreach (var item in items.OrderByDescending(i => i.IsEquippable ? i.ItemLevel : 0))
        {
            InventoryType? destBag = null;

            if (item.IsEquippable && gearDestBag.HasValue)
                destBag = gearDestBag;
            else if ((item.UICategoryRowId == UICategoryMeal || item.UICategoryRowId == UICategoryMedicine) && consumableDestBag.HasValue)
                destBag = consumableDestBag;
            else if ((item.UICategoryRowId == UICategoryCrystal || item.UICategoryRowId == UICategoryMateria) && crystalDestBag.HasValue)
                destBag = crystalDestBag;

            if (destBag.HasValue && item.Container != destBag)
                result.Add(new QuickMove { Item = item, DestBag = destBag.Value });
        }
        return result;
    }

    /// <summary>
    /// "The Hoarder": Group by item rarity. Whites/vendor trash to Bag 4, greens to Bag 3, 
    /// blues/purples to Bags 1-2 (organized by equippable/non). Makes it easy to spot junk.
    /// </summary>
    public static List<QuickMove> BuildHoarderSort(IReadOnlyList<InventoryItemInfo> items, IReadOnlyList<bool> bagFlags)
    {
        var result = new List<QuickMove>();

        foreach (var item in items)
        {
            InventoryType? destBag = null;

            // Vendor trash (white, rarity 1)
            if (item.Rarity == 1 && bagFlags.Count > 3 && bagFlags[3])
                destBag = InventoryService.PlayerBags[3];
            // Green (rarity 2)
            else if (item.Rarity == 2 && bagFlags.Count > 2 && bagFlags[2])
                destBag = InventoryService.PlayerBags[2];
            // Blue/Purple (rarity 3-4): separate by type
            else if (item.Rarity == 3 || item.Rarity == 4)
            {
                if (item.IsEquippable && bagFlags.Count > 0 && bagFlags[0])
                    destBag = InventoryService.PlayerBags[0];
                else if (!item.IsEquippable && bagFlags.Count > 1 && bagFlags[1])
                    destBag = InventoryService.PlayerBags[1];
            }

            if (destBag.HasValue && item.Container != destBag)
                result.Add(new QuickMove { Item = item, DestBag = destBag.Value });
        }
        return result;
    }

    /// <summary>
    /// Builds a list of moves for the "Smart Sort Everything" preset:
    /// Gear → Bag1, Consumables → Bag2, Crystals/Materia → Bag4, everything stackable else → Bag3.
    /// First-match-wins ordering.
    /// </summary>
    public static List<QuickMove> BuildSmartSortPlan(IReadOnlyList<InventoryItemInfo> items, IReadOnlyList<bool> bagFlags)
    {
        var presetsInOrder = new[]
        {
            GroupHQGearToBag1,    // HQ gear takes precedence over plain gear (still goes to Bag 1)
            GroupGearToBag1,
            GroupConsumablesToBag2,
            GroupCrystalsToBag4,
            GroupMaterialsToBag3,
        };
        var result = new List<QuickMove>();
        foreach (var item in items)
        {
            foreach (var p in presetsInOrder)
            {
                if (!p.Match(item)) continue;
                var idx = Math.Clamp(p.DestBagIndex, 0, 3);
                if (idx >= bagFlags.Count || !bagFlags[idx]) break;
                var destBag = InventoryService.PlayerBags[idx];
                if (item.Container == destBag) break; // already there
                result.Add(new QuickMove { Item = item, DestBag = destBag });
                break;
            }
        }
        return result;
    }

    public static List<QuickMove> BuildPresetPlan(QuickPreset preset, IReadOnlyList<InventoryItemInfo> items, IReadOnlyList<bool> bagFlags)
    {
        var idx = Math.Clamp(preset.DestBagIndex, 0, 3);
        if (idx >= bagFlags.Count || !bagFlags[idx]) return new List<QuickMove>();
        var destBag = InventoryService.PlayerBags[idx];
        return items
            .Where(i => preset.Match(i) && i.Container != destBag)
            .Select(i => new QuickMove { Item = i, DestBag = destBag })
            .ToList();
    }

    // ─── Advanced Operations ──────────────────────────────────────────────────

    /// <summary>
    /// Extract Materia: Move all gear at 100% spiritbond to the top of Bag 1
    /// (ready for materia extraction from spiritbond process).
    /// </summary>
    public static List<QuickMove> BuildExtractMateria(IReadOnlyList<InventoryItemInfo> items, IReadOnlyList<bool> bagFlags)
    {
        if (!bagFlags[0]) return new List<QuickMove>();
        var destBag = InventoryService.PlayerBags[0];
        
        var extractables = items
            .Where(i => i.IsEquippable && i.SpiritbondPercent >= 100)
            .OrderByDescending(i => i.ItemLevel)
            .ToList();

        var moves = new List<QuickMove>();
        for (int i = 0; i < extractables.Count && i < 35; i++)
        {
            var item = extractables[i];
            if (item.Container != destBag || item.Slot != i)
            {
                moves.Add(new QuickMove { Item = item, DestBag = destBag, DestSlot = i });
            }
        }
        return moves;
    }

    /// <summary>
    /// Merge Stacks: Find duplicate items by ItemId and consolidate into one stack.
    /// Moves all duplicates into the first occurrence's bag, allowing natural consolidation.
    /// </summary>
    public static List<QuickMove> BuildMergeStacks(IReadOnlyList<InventoryItemInfo> items, IReadOnlyList<bool> bagFlags)
    {
        var result = new List<QuickMove>();
        // Important: we group by ItemId and HQ flag to only merge exact matches
        var grouped = items.GroupBy(i => new { i.ItemId, i.IsHQ }).Where(g => g.Count() > 1);

        foreach (var group in grouped)
        {
            var stacks = group.ToList();
            var target = stacks.First(); // Pick the first stack to merge into

            // Move all other stacks directly onto the primary stack's slot to trigger a merge
            foreach (var item in stacks.Skip(1))
            {
                 result.Add(new QuickMove { Item = item, DestBag = target.Container, DestSlot = target.Slot });
            }
        }

        return result;
    }

    /// <summary>
    /// Vendor Trash: Group all grey-rarity items (white) into Bag 4 for easy discard.
    /// </summary>
    public static List<QuickMove> BuildVendorTrashGroup(IReadOnlyList<InventoryItemInfo> items, IReadOnlyList<bool> bagFlags, Configuration config)
    {
        if (!bagFlags[3]) return new List<QuickMove>();
        var destBag = InventoryService.PlayerBags[3];
        
        var trash = items
            .Where(i => IsJunk(i, config))
            .OrderBy(i => i.ItemId)
            .ToList();

        var moves = new List<QuickMove>();
        for (int i = 0; i < trash.Count && i < 35; i++)
        {
            var item = trash[i];
            if (item.Container != destBag || item.Slot != i)
            {
                moves.Add(new QuickMove { Item = item, DestBag = destBag, DestSlot = i });
            }
        }
        return moves;
    }

    // ─── Natural Language Search Helpers ───────────────────────────────────────

    /// <summary>
    /// Simple natural language keyword matching for item categories.
    /// Maps combat-related keywords to item properties.
    /// </summary>
        public static bool IsJunk(InventoryItemInfo i, Configuration config)
    {
        // ─── HARD SAFETY LOCKS — these cannot be disabled by user toggles ───
        // 1) Only white-rarity items can EVER be junk. Green / Blue / Purple / Pink gear and
        //    HQ / collectable items are categorically excluded.
        if (i.Rarity != 1) return false;
        if (i.IsHQ || i.IsCollectable) return false;
        // 2) Item level ceiling — anything with an ilvl above the configured floor is locked out.
        //    Default = 1, which blocks every piece of equippable gear, every tool, etc.
        if (i.ItemLevel > (uint)Math.Max(0, config.JunkMaxItemLevel)) return false;
        // 3) Equippable items are always excluded as a redundant safety net even if the user
        //    accidentally raises JunkMaxItemLevel above the gear they own.
        if (i.IsEquippable) return false;

        // ─── User-configurable filters ───
        if (config.ExcludeGearFromJunk && (i.Name.EndsWith("Arms") || i.Name.EndsWith("Coffer") || i.Name.EndsWith("Attire") || i.Name.EndsWith("Weapon") || i.Name.EndsWith("Equipment"))) return false;
        if (i.UICategoryRowId == 59 || i.UICategoryRowId == 58) return false;
        if (config.ExcludeConsumablesFromJunk && (i.UICategoryRowId == 44 || i.UICategoryRowId == 46)) return false;
        if (config.ExcludeCraftingFromJunk && i.IsStackable) return false;
        if (i.VendorPrice > config.MaxJunkVendorPrice) return false;
        return true;
    }

    public static List<InventoryItemInfo> SearchByKeyword(IReadOnlyList<InventoryItemInfo> items, string keyword)
    {
        var lower = keyword.ToLowerInvariant().Trim();
        if (string.IsNullOrEmpty(lower)) return items.ToList();

        // Map keywords to filter logic
        return lower switch
        {
            "combat" or "fight" or "battle" => items.Where(i => 
                i.IsEquippable || (i.UICategoryRowId == UICategoryMeal || i.UICategoryRowId == UICategoryMedicine)).ToList(),
            
            "heal" or "healer" or "medicine" => items.Where(i =>
                i.ClassJobs.Any(j => new[] { "WHM", "SCH", "AST", "SGE" }.Contains(j)) || i.UICategoryRowId == UICategoryMedicine).ToList(),
            
            "magic" or "caster" or "spell" => items.Where(i =>
                i.ClassJobs.Any(j => new[] { "BLM", "SMN", "RDM", "BLU", "PCT" }.Contains(j))).ToList(),
            
            "physical" or "melee" or "strength" => items.Where(i =>
                i.ClassJobs.Any(j => new[] { "PLD", "MNK", "DRG", "NIN", "SAM", "RPR", "VPR" }.Contains(j))).ToList(),
            
            "tank" => items.Where(i =>
                i.ClassJobs.Any(j => new[] { "PLD", "WAR", "DRK", "GNB" }.Contains(j))).ToList(),
            
            "craft" or "crafter" => items.Where(i =>
                i.ClassJobs.Any(j => new[] { "CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL" }.Contains(j))).ToList(),
            
            "gather" or "gatherer" => items.Where(i =>
                i.ClassJobs.Any(j => new[] { "MIN", "BTN", "FSH" }.Contains(j))).ToList(),
            
            "hq" or "highquality" => items.Where(i => i.IsHQ).ToList(),
            
            "junk" or "trash" or "vendor" => items.Where(i => i.Rarity == 1).ToList(),
            
            "consumable" or "food" or "pot" => items.Where(i =>
                i.UICategoryRowId == UICategoryMeal || i.UICategoryRowId == UICategoryMedicine).ToList(),
            
            "materia" => items.Where(i => i.UICategoryRowId == UICategoryMateria).ToList(),
            
            "crystal" => items.Where(i => i.UICategoryRowId == UICategoryCrystal).ToList(),
            
            "spiritbond" or "extractable" => items.Where(i => i.SpiritbondPercent >= 100).ToList(),
            
            _ => items.ToList()
        };
    }

    // ─── Layout Snapshot & Copy System ────────────────────────────────────────

    /// <summary>
    /// Snapshot a bag's current layout: item IDs in slot order.
    /// Used as a template for "Mirror" operations.
    /// </summary>
    public static List<uint?> SnapshotBagLayout(IReadOnlyList<InventoryItemInfo> items, InventoryType bagType)
    {
        var result = new List<uint?>();
        var bagItems = items.Where(i => i.Container == bagType).OrderBy(i => i.Slot).ToList();
        
        // Create a map of slot -> itemId for this bag
        var maxSlot = bagItems.Any() ? bagItems.Max(i => i.Slot) : 0;
        for (var s = 0; s <= maxSlot; s++)
        {
            var item = bagItems.FirstOrDefault(i => i.Slot == s);
            result.Add(item?.ItemId);
        }
        
        return result;
    }

    /// <summary>
    /// Categorize an item into one of the layout-zone tags. The result matches the tags
    /// painted in the Layout Zones tab: Gear / Materia / Consumables / Crafting / Gathering /
    /// Crystals / Junk / Misc. Junk takes precedence over its underlying category so that
    /// painted "Junk" zones receive the right items even when the item is also stackable.
    /// 
    /// Priority order:
    /// 1. Junk (white-rarity, low vendor price)
    /// 2. Gear (equippable items)
    /// 3. Materia (UICategory 58)
    /// 4. Crystals (UICategory 59)
    /// 5. Consumables (UICategory 44, 45, 46, 43)
    /// 6. Gathering (UICategory 47, 48, 38, 39, 40)
    /// 7. Crafting (stackable items)
    /// 8. Misc (everything else)
    /// </summary>
    public static string CategorizeForZone(InventoryItemInfo i, Configuration config)
    {
        // Priority 1: Junk
        if (IsJunk(i, config))
        {
            LogCategorization(i.Name, i.UICategoryRowId, "Junk", "Matches junk rules");
            return "Junk";
        }
        
        // Priority 2: Gear
        if (i.IsEquippable)
        {
            LogCategorization(i.Name, i.UICategoryRowId, "Gear", "IsEquippable=true");
            return "Gear";
        }
        
        // Priority 3: Materia
        if (i.UICategoryRowId == UICategoryMateria)
        {
            LogCategorization(i.Name, i.UICategoryRowId, "Materia", "UICategory=58");
            return "Materia";
        }
        
        // Priority 4: Crystals
        if (i.UICategoryRowId == UICategoryCrystal)
        {
            LogCategorization(i.Name, i.UICategoryRowId, "Crystals", "UICategory=59");
            return "Crystals";
        }
        
        // Priority 5: Consumables (expanded check)
        if (IsConsumableCategory(i.UICategoryRowId))
        {
            LogCategorization(i.Name, i.UICategoryRowId, "Consumables", $"UICategory={i.UICategoryRowId}");
            return "Consumables";
        }
        
        // Priority 6: Gathering (raw materials, fish, gardening)
        if (i.UICategoryRowId == UICategorySeafood || i.UICategoryRowId == UICategoryGardening ||
            i.UICategoryRowId == UICategoryRawMat1 || i.UICategoryRowId == UICategoryRawMat2 || i.UICategoryRowId == UICategoryRawMat3)
        {
            LogCategorization(i.Name, i.UICategoryRowId, "Gathering", $"UICategory={i.UICategoryRowId}");
            return "Gathering";
        }
        
        // Priority 7: Crafting (stackable non-consumables)
        if (i.IsStackable)
        {
            LogCategorization(i.Name, i.UICategoryRowId, "Crafting", "IsStackable=true");
            return "Crafting";
        }
        
        // Priority 8: Miscellaneous (default fallback)
        LogCategorization(i.Name, i.UICategoryRowId, "Misc", "No specific category matched");
        return "Misc";
    }

    private static IOrderedEnumerable<InventoryItemInfo> SortForCategory(string cat, IEnumerable<InventoryItemInfo> items) => cat switch
    {
        "Gear" => items.OrderByDescending(i => i.ItemLevel)
                       .ThenByDescending(i => i.Rarity)
                       .ThenByDescending(i => i.IsHQ)
                       .ThenBy(i => i.Name),
        "Materia" => items.OrderByDescending(i => i.ItemLevel).ThenBy(i => i.ItemId),
        "Consumables" => items.OrderBy(i => i.ItemId).ThenByDescending(i => i.IsHQ),
        "Crystals" => items.OrderBy(i => i.ItemId),
        "Crafting" => items.OrderBy(i => i.ItemId),
        "Gathering" => items.OrderBy(i => i.ItemId),
        "Junk" => items.OrderBy(i => i.VendorPrice).ThenBy(i => i.ItemId),
        _ => items.OrderBy(i => i.ItemId),
    };

    private static string NormalizeZoneTag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "None";
        var tag = raw.Trim();
        return tag switch
        {
            "Crystal" => "Crystals",
            _ => tag,
        };
    }

    private static HashSet<string> ParseZoneTagSet(string? raw)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var parts = raw.Split(['|', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            var tag = NormalizeZoneTag(p);
            if (tag == "None") continue;
            result.Add(tag);
        }

        return result;
    }

    private static List<QueuedMove> BuildVirtualSwapMoves(
        IReadOnlyList<InventoryItemInfo> items,
        IReadOnlyDictionary<(InventoryType bag, int slot), InventoryItemInfo> target)
    {
        var current = new Dictionary<(InventoryType bag, int slot), InventoryItemInfo>();
        var locOf = new Dictionary<InventoryItemInfo, (InventoryType bag, int slot)>();
        foreach (var item in items)
        {
            var k = (item.Container, item.Slot);
            current[k] = item;
            locOf[item] = k;
        }

        var moves = new List<QueuedMove>();
        var orderedTargets = target.Keys
            .OrderBy(k => Array.IndexOf(InventoryService.PlayerBags, k.bag))
            .ThenBy(k => k.slot)
            .ToList();

        foreach (var key in orderedTargets)
        {
            var desired = target[key];
            if (current.TryGetValue(key, out var here) && ReferenceEquals(here, desired)) continue;
            if (!locOf.TryGetValue(desired, out var srcKey)) continue;

            moves.Add(new QueuedMove(srcKey.bag, srcKey.slot, key.bag, key.slot, false, desired.Name));

            current.TryGetValue(key, out var displaced);
            current[key] = desired;
            locOf[desired] = key;
            if (displaced != null)
            {
                current[srcKey] = displaced;
                locOf[displaced] = srcKey;
            }
            else
            {
                current.Remove(srcKey);
            }
        }

        return moves;
    }

    /// <summary>
    /// Generic full-rebuild engine. Used by every Bag Assistant sort.
    ///
    /// Algorithm:
    /// 1. Read every item in the active bags.
    /// 2. Categorise each item via <paramref name="itemCategorizer"/>.
    /// 3. Sort items inside each category via <paramref name="sorter"/> (deterministic).
    /// 4. For every active bag slot, ask <paramref name="slotTagger"/> for a category tag.
    /// 5. Place sorted items into matching tagged slots in slot order.
    /// 6. Spill overflow (items whose category has no zone, or whose zone is full) into any
    ///    remaining unfilled slots in slot order.
    /// 7. Run a virtual swap simulator over (current → target) and emit minimal
    ///    <see cref="QueuedMove"/>s. Each move corresponds to one
    ///    <c>InventoryManager.MoveItemSlot</c> call (atomic swap).
    ///
    /// The simulator guarantees safety: every emitted move sees the post-state of all
    /// previous moves, so items are never overwritten.
    /// </summary>
    public static List<QueuedMove> BuildFullRebuildPlan(
        IReadOnlyList<InventoryItemInfo> items,
        Func<int, string?> slotTagger,
        Func<InventoryItemInfo, string> itemCategorizer,
        bool[] bagFlags,
        Func<string, IEnumerable<InventoryItemInfo>, IOrderedEnumerable<InventoryItemInfo>>? sorter = null)
    {
        sorter ??= SortForCategory;

        // 1) Group items in active bags by their category.
        var byCategory = new Dictionary<string, List<InventoryItemInfo>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var bagIdx = Array.IndexOf(InventoryService.PlayerBags, item.Container);
            if (bagIdx < 0 || bagIdx >= bagFlags.Length || !bagFlags[bagIdx]) continue;
            var cat = itemCategorizer(item);
            if (!byCategory.TryGetValue(cat, out var list))
            {
                list = new List<InventoryItemInfo>();
                byCategory[cat] = list;
            }
            list.Add(item);
        }

        // 2) Sort each category deterministically.
        foreach (var key in byCategory.Keys.ToList())
            byCategory[key] = sorter(key, byCategory[key]).ToList();

        // 3) Walk the layout in slot order; collect per-tag slot lists.
        var zoneSlots = new Dictionary<string, List<(InventoryType bag, int slot)>>(StringComparer.Ordinal);
        for (int g = 0; g < 140; g++)
        {
            int bagIdx = g / 35;
            int slotIdx = g % 35;
            if (bagIdx >= bagFlags.Length || !bagFlags[bagIdx]) continue;
            var tag = slotTagger(g);
            if (string.IsNullOrEmpty(tag) || tag == "None") continue;
            var bag = InventoryService.PlayerBags[bagIdx];
            if (!zoneSlots.TryGetValue(tag, out var list))
            {
                list = new List<(InventoryType, int)>();
                zoneSlots[tag] = list;
            }
            list.Add((bag, slotIdx));
        }

        // 4) Assign sorted items to their zone slots; collect overflow.
        var target = new Dictionary<(InventoryType bag, int slot), InventoryItemInfo>();
        var overflow = new List<InventoryItemInfo>();
        foreach (var (cat, list) in byCategory)
        {
            if (zoneSlots.TryGetValue(cat, out var slots))
            {
                int n = Math.Min(list.Count, slots.Count);
                for (int i = 0; i < n; i++) target[slots[i]] = list[i];
                for (int i = n; i < list.Count; i++) overflow.Add(list[i]);
            }
            else
            {
                overflow.AddRange(list);
            }
        }

        // 5) Spill overflow into ANY remaining unfilled active slot, in slot order.
        for (int g = 0; g < 140 && overflow.Count > 0; g++)
        {
            int bagIdx = g / 35;
            int slotIdx = g % 35;
            if (bagIdx >= bagFlags.Length || !bagFlags[bagIdx]) continue;
            var bag = InventoryService.PlayerBags[bagIdx];
            var key = (bag, slotIdx);
            if (target.ContainsKey(key)) continue;
            target[key] = overflow[0];
            overflow.RemoveAt(0);
        }

        // 6) Virtual swap simulator: emit minimal moves while applying each change to a
        //    shadow model so subsequent moves see the up-to-date state.
        var current = new Dictionary<(InventoryType bag, int slot), InventoryItemInfo>();
        var locOf = new Dictionary<InventoryItemInfo, (InventoryType bag, int slot)>();
        foreach (var item in items)
        {
            var k = (item.Container, item.Slot);
            current[k] = item;
            locOf[item] = k;
        }

        var moves = new List<QueuedMove>();
        var orderedTargets = target.Keys
            .OrderBy(k => Array.IndexOf(InventoryService.PlayerBags, k.bag))
            .ThenBy(k => k.slot)
            .ToList();

        foreach (var key in orderedTargets)
        {
            var desired = target[key];
            if (current.TryGetValue(key, out var here) && ReferenceEquals(here, desired)) continue;
            if (!locOf.TryGetValue(desired, out var srcKey)) continue;

            moves.Add(new QueuedMove(srcKey.bag, srcKey.slot, key.bag, key.slot, false, desired.Name));

            current.TryGetValue(key, out var displaced);
            current[key] = desired;
            locOf[desired] = key;
            if (displaced != null)
            {
                current[srcKey] = displaced;
                locOf[displaced] = srcKey;
            }
            else
            {
                current.Remove(srcKey);
            }
        }

        return moves;
    }

    /// <summary>
    /// Build a full-rebuild Apply-Zones plan. Categorises every item, sorts within each
    /// category, then assigns items to the painted layout slots.
    /// </summary>
    public static List<QueuedMove> BuildApplyVisualZonesPlan(
        IReadOnlyList<InventoryItemInfo> items,
        string[] layoutMap,
        bool[] bagFlags,
        Configuration config)
    {
        var activeSlots = new List<(InventoryType bag, int slot)>();
        var allowedTagsBySlot = new Dictionary<(InventoryType bag, int slot), HashSet<string>>();

        for (int g = 0; g < 140; g++)
        {
            int bagIdx = g / 35;
            int slotIdx = g % 35;
            if (bagIdx >= bagFlags.Length || !bagFlags[bagIdx]) continue;

            var bag = InventoryService.PlayerBags[bagIdx];
            var key = (bag, slotIdx);
            activeSlots.Add(key);

            var raw = (layoutMap != null && g >= 0 && g < layoutMap.Length) ? layoutMap[g] : null;
            allowedTagsBySlot[key] = ParseZoneTagSet(raw);
        }

        var byCategory = new Dictionary<string, List<InventoryItemInfo>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var bagIdx = Array.IndexOf(InventoryService.PlayerBags, item.Container);
            if (bagIdx < 0 || bagIdx >= bagFlags.Length || !bagFlags[bagIdx]) continue;

            var category = CategorizeForZone(item, config);
            if (!byCategory.TryGetValue(category, out var list))
            {
                list = new List<InventoryItemInfo>();
                byCategory[category] = list;
            }
            list.Add(item);
        }

        foreach (var key in byCategory.Keys.ToList())
            byCategory[key] = SortForCategory(key, byCategory[key]).ToList();

        var categoryOrder = ResolveZoneCategoryPriority(config, byCategory);

        var target = new Dictionary<(InventoryType bag, int slot), InventoryItemInfo>();
        var overflow = new List<InventoryItemInfo>();

        foreach (var category in categoryOrder)
        {
            if (!byCategory.TryGetValue(category, out var bucket) || bucket.Count == 0) continue;

            var matchingSlots = activeSlots
                .Where(s => !target.ContainsKey(s)
                            && allowedTagsBySlot.TryGetValue(s, out var tags)
                            && tags.Contains(category))
                .ToList();

            int assignCount = Math.Min(bucket.Count, matchingSlots.Count);
            for (int i = 0; i < assignCount; i++)
                target[matchingSlots[i]] = bucket[i];

            for (int i = assignCount; i < bucket.Count; i++)
                overflow.Add(bucket[i]);
        }

        foreach (var slot in activeSlots)
        {
            if (overflow.Count == 0) break;
            if (target.ContainsKey(slot)) continue;
            target[slot] = overflow[0];
            overflow.RemoveAt(0);
        }

        return BuildVirtualSwapMoves(items, target);
    }

    // ─── Preset planners (all routed through BuildFullRebuildPlan) ────────────

    /// <summary>Tag every slot of a bag (35 slots) with the same tag, leaving others "None".</summary>
    private static string? UniformBagTag(int globalSlotIndex, int bagIndex, string tag)
        => globalSlotIndex / 35 == bagIndex ? tag : "None";

    /// <summary>Tag bags from a (bagIndex → tag) map.</summary>
    private static string? MultiBagTag(int globalSlotIndex, IReadOnlyDictionary<int, string> tagsByBag)
    {
        var bagIdx = globalSlotIndex / 35;
        return tagsByBag.TryGetValue(bagIdx, out var tag) ? tag : "None";
    }

    /// <summary>
    /// Smart Sort: Gear → Bag 1, Consumables → Bag 2, Crafting/Gathering → Bag 3,
    /// Crystals/Materia → Bag 4. Junk and Misc spill into remaining capacity.
    /// </summary>
    public static List<QueuedMove> BuildSmartSortPlanV2(IReadOnlyList<InventoryItemInfo> items, bool[] bagFlags, Configuration config)
    {
        var slotTags = new Dictionary<int, string>();
        if (bagFlags.Length > 0 && bagFlags[0]) slotTags[0] = "Gear";
        if (bagFlags.Length > 1 && bagFlags[1]) slotTags[1] = "Consumables";
        if (bagFlags.Length > 2 && bagFlags[2]) slotTags[2] = "Crafting";
        if (bagFlags.Length > 3 && bagFlags[3]) slotTags[3] = "Crystals";

        return BuildFullRebuildPlan(
            items,
            slotTagger: g => MultiBagTag(g, slotTags),
            itemCategorizer: i => SmartCategorize(i, config),
            bagFlags: bagFlags);
    }

    /// <summary>Smart Sort categoriser: collapses Materia→Crystals and Gathering→Crafting so
    /// they share their respective destination bags.</summary>
    private static string SmartCategorize(InventoryItemInfo i, Configuration config)
    {
        var raw = CategorizeForZone(i, config);
        return raw switch
        {
            "Materia" => "Crystals",
            "Gathering" => "Crafting",
            _ => raw,
        };
    }

    /// <summary>Gatherer: Crystals/Materia → Bag 4, raw materials → Bag 3, everything else → Bag 2.</summary>
    public static List<QueuedMove> BuildGathererSortPlan(IReadOnlyList<InventoryItemInfo> items, bool[] bagFlags, Configuration config)
    {
        var slotTags = new Dictionary<int, string>();
        if (bagFlags.Length > 1 && bagFlags[1]) slotTags[1] = "Other";
        if (bagFlags.Length > 2 && bagFlags[2]) slotTags[2] = "Mats";
        if (bagFlags.Length > 3 && bagFlags[3]) slotTags[3] = "Crystals";

        return BuildFullRebuildPlan(
            items,
            slotTagger: g => MultiBagTag(g, slotTags),
            itemCategorizer: i =>
            {
                if (i.UICategoryRowId == UICategoryCrystal || i.UICategoryRowId == UICategoryMateria) return "Crystals";
                if (i.IsStackable && !i.IsEquippable) return "Mats";
                return "Other";
            },
            bagFlags: bagFlags);
    }

    /// <summary>Raider: Gear → Bag 1, Consumables → Bag 2, Crafting/Mats → Bag 3, Crystals/Materia → Bag 4.</summary>
    public static List<QueuedMove> BuildRaiderSortPlan(IReadOnlyList<InventoryItemInfo> items, bool[] bagFlags, Configuration config)
    {
        var slotTags = new Dictionary<int, string>();
        if (bagFlags.Length > 0 && bagFlags[0]) slotTags[0] = "Gear";
        if (bagFlags.Length > 1 && bagFlags[1]) slotTags[1] = "Cons";
        if (bagFlags.Length > 2 && bagFlags[2]) slotTags[2] = "Mats";
        if (bagFlags.Length > 3 && bagFlags[3]) slotTags[3] = "Crystals";

        return BuildFullRebuildPlan(
            items,
            slotTagger: g => MultiBagTag(g, slotTags),
            itemCategorizer: i =>
            {
                if (i.IsEquippable) return "Gear";
                if (i.UICategoryRowId == UICategoryMeal || i.UICategoryRowId == UICategoryMedicine) return "Cons";
                if (i.UICategoryRowId == UICategoryCrystal || i.UICategoryRowId == UICategoryMateria) return "Crystals";
                return "Mats";
            },
            bagFlags: bagFlags,
            sorter: (cat, list) => cat == "Gear"
                ? list.OrderByDescending(i => i.ItemLevel).ThenByDescending(i => i.IsHQ).ThenBy(i => i.Name)
                : SortForCategory(cat, list));
    }

    /// <summary>Hoarder: Group by rarity. Purple/Blue gear → Bag 1, Purple/Blue non-gear → Bag 2,
    /// Green → Bag 3, White → Bag 4.</summary>
    public static List<QueuedMove> BuildHoarderSortPlan(IReadOnlyList<InventoryItemInfo> items, bool[] bagFlags, Configuration config)
    {
        var slotTags = new Dictionary<int, string>();
        if (bagFlags.Length > 0 && bagFlags[0]) slotTags[0] = "RareGear";
        if (bagFlags.Length > 1 && bagFlags[1]) slotTags[1] = "RareOther";
        if (bagFlags.Length > 2 && bagFlags[2]) slotTags[2] = "Green";
        if (bagFlags.Length > 3 && bagFlags[3]) slotTags[3] = "White";

        return BuildFullRebuildPlan(
            items,
            slotTagger: g => MultiBagTag(g, slotTags),
            itemCategorizer: i =>
            {
                if (i.Rarity == 1) return "White";
                if (i.Rarity == 2) return "Green";
                return i.IsEquippable ? "RareGear" : "RareOther";
            },
            bagFlags: bagFlags,
            sorter: (cat, list) => list.OrderByDescending(i => i.ItemLevel).ThenBy(i => i.ItemId));
    }

    /// <summary>Single-preset rebuild: matched items fill the target bag (sorted), everything
    /// else displaces naturally to the remaining bags.</summary>
    public static List<QueuedMove> BuildPresetRebuildPlan(QuickPreset preset, IReadOnlyList<InventoryItemInfo> items, bool[] bagFlags, Configuration config)
    {
        var idx = Math.Clamp(preset.DestBagIndex, 0, 3);
        if (idx >= bagFlags.Length || !bagFlags[idx]) return new List<QueuedMove>();
        return BuildFullRebuildPlan(
            items,
            slotTagger: g => g / 35 == idx ? "Match" : "None",
            itemCategorizer: i => preset.Match(i) ? "Match" : "Other",
            bagFlags: bagFlags);
    }

    /// <summary>Single-rule rebuild: items matching the rule fill the rule's target bag in sorted order.</summary>
    public static List<QueuedMove> BuildSingleRuleRebuildPlan(SortRule rule, IReadOnlyList<InventoryItemInfo> items, bool[] bagFlags, Configuration config)
    {
        if (rule.Target != SortTarget.SpecificBag) return new List<QueuedMove>();
        var idx = Math.Clamp(rule.TargetBagIndex, 0, 3);
        if (idx >= bagFlags.Length || !bagFlags[idx]) return new List<QueuedMove>();
        return BuildFullRebuildPlan(
            items,
            slotTagger: g => g / 35 == idx ? "Match" : "None",
            itemCategorizer: i => RuleMatcher.Matches(rule, i) ? "Match" : "Other",
            bagFlags: bagFlags);
    }

    /// <summary>All-rules rebuild: each item is bucketed into the first matching rule's target bag.</summary>
    public static List<QueuedMove> BuildAllRulesRebuildPlan(IReadOnlyList<SortRule> rules, IReadOnlyList<InventoryItemInfo> items, bool[] bagFlags, Configuration config)
    {
        var slotTags = new Dictionary<int, string>();
        for (int b = 0; b < 4; b++)
        {
            if (b < bagFlags.Length && bagFlags[b]) slotTags[b] = $"Bag{b}";
        }

        return BuildFullRebuildPlan(
            items,
            slotTagger: g => MultiBagTag(g, slotTags),
            itemCategorizer: i =>
            {
                foreach (var rule in rules)
                {
                    if (!rule.Enabled) continue;
                    if (rule.Target != SortTarget.SpecificBag) continue;
                    if (!RuleMatcher.Matches(rule, i)) continue;
                    var idx = Math.Clamp(rule.TargetBagIndex, 0, 3);
                    return $"Bag{idx}";
                }
                return "Unsorted";
            },
            bagFlags: bagFlags);
    }

    /// <summary>Extract Materia: gear at 100% spiritbond fills Bag 1 (sorted by ilvl desc).</summary>
    public static List<QueuedMove> BuildExtractMateriaPlan(IReadOnlyList<InventoryItemInfo> items, bool[] bagFlags, Configuration config)
    {
        if (bagFlags.Length == 0 || !bagFlags[0]) return new List<QueuedMove>();
        return BuildFullRebuildPlan(
            items,
            slotTagger: g => g / 35 == 0 ? "Spiritbond" : "None",
            itemCategorizer: i => i.IsEquippable && i.SpiritbondPercent >= 100 ? "Spiritbond" : "Other",
            bagFlags: bagFlags,
            sorter: (cat, list) => list.OrderByDescending(i => i.ItemLevel).ThenBy(i => i.Name));
    }

    /// <summary>Vendor Trash: junk fills Bag 4 (sorted).</summary>
    public static List<QueuedMove> BuildVendorTrashPlan(IReadOnlyList<InventoryItemInfo> items, bool[] bagFlags, Configuration config)
    {
        if (bagFlags.Length < 4 || !bagFlags[3]) return new List<QueuedMove>();
        return BuildFullRebuildPlan(
            items,
            slotTagger: g => g / 35 == 3 ? "Junk" : "None",
            itemCategorizer: i => IsJunk(i, config) ? "Junk" : "Other",
            bagFlags: bagFlags);
    }

    /// <summary>
    /// Legacy entry point kept so any external caller still compiles. Internally it now
    /// delegates to the full-rebuild planner. The returned list is empty because the new
    /// planner emits cross-bag <see cref="QueuedMove"/>s rather than QuickMoves; callers
    /// should use <see cref="BuildApplyVisualZonesPlan"/> directly.
    /// </summary>
    public static List<QuickMove> ApplyVisualZones(IReadOnlyList<InventoryItemInfo> items, string[] layoutMap, bool[] bagFlags, Configuration config)
        => new();

    public static List<QuickMove> ApplyLayoutTemplate(
        IReadOnlyList<InventoryItemInfo> items,
        List<uint?> template,
        InventoryType sourceBag,
        InventoryType targetBag)
    {
        var result = new List<QuickMove>();
        var sourceItems = items.Where(i => i.Container == sourceBag).ToList();
        var targetItems = items.Where(i => i.Container == targetBag).ToList();

        // Try to match items from source layout to target bag
        foreach (var templateItemId in template)
        {
            if (!templateItemId.HasValue) continue;
            var sourceItem = sourceItems.FirstOrDefault(i => i.ItemId == templateItemId);
            if (sourceItem == null) continue;

            // Move this item to target bag
            if (sourceItem.Container != targetBag)
                result.Add(new QuickMove { Item = sourceItem, DestBag = targetBag });
        }

        return result;
    }

    public static List<QuickMove> BuildIntraBagSort(IReadOnlyList<InventoryItemInfo> items, IReadOnlyList<bool> bagFlags, Configuration config)
    {
        var moves = new List<QuickMove>();

        for (int b = 0; b < 4; b++)
        {
            if (b >= bagFlags.Count || !bagFlags[b]) continue;
            var bagType = InventoryService.PlayerBags[b];

            var virtualSlots = new InventoryItemInfo[35];
            foreach (var item in items.Where(i => i.Container == bagType))
            {
                virtualSlots[item.Slot] = item;
            }

            var bagItems = items.Where(i => i.Container == bagType).ToList();
            var sorted = bagItems.OrderBy(i => i.IsEquippable ? 0 : 1)
                                 .ThenBy(i => IsJunk(i, config) ? 1 : 0)
                                 .ThenByDescending(i => i.ItemLevel)
                                 .ThenByDescending(i => i.EquipLevel)
                                 .ThenByDescending(i => i.Rarity)
                                 .ThenByDescending(i => i.UICategoryRowId)
                                 .ThenBy(i => i.ItemId)
                                 .ThenByDescending(i => i.IsHQ)
                                 .ToList();

            for (int k = 0; k < sorted.Count; k++)
            {
                var targetItem = sorted[k];
                if (virtualSlots[k] != targetItem)
                {
                    int currentIndex = -1;
                    for (int s = 0; s < 35; s++)
                    {
                        if (virtualSlots[s] == targetItem)
                        {
                            currentIndex = s;
                            break;
                        }
                    }

                    if (currentIndex != -1)
                    {
                        moves.Add(new QuickMove { Item = targetItem, DestBag = bagType, DestSlot = k, SrcSlotOverride = currentIndex });
                        
                        var temp = virtualSlots[k];
                        virtualSlots[k] = virtualSlots[currentIndex];
                        virtualSlots[currentIndex] = temp;
                    }
                }
            }
        }
        return moves;
    }
}

