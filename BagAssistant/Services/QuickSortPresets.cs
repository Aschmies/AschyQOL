using FFXIVClientStructs.FFXIV.Client.Game;
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
    // ItemUICategory row IDs (Lumina). Common ones:
    private const uint UICategoryMeal = 46;
    private const uint UICategoryMedicine = 44;
    private const uint UICategoryCrystal = 59;
    private const uint UICategoryMateria = 58;

    // Reagents / Ingredients / Cooking ingredients live in many categories — easier to detect via flags below.

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
        if (i.Rarity != 1) return false;
        if (config.ExcludeGearFromJunk && (i.IsEquippable || i.Name.EndsWith("Arms") || i.Name.EndsWith("Coffer") || i.Name.EndsWith("Attire") || i.Name.EndsWith("Weapon") || i.Name.EndsWith("Equipment"))) return false;
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
    /// </summary>
    private static string CategorizeForZone(InventoryItemInfo i, Configuration config)
    {
        if (IsJunk(i, config)) return "Junk";
        if (i.IsEquippable) return "Gear";
        if (i.UICategoryRowId == UICategoryMateria) return "Materia";
        if (i.UICategoryRowId == UICategoryCrystal) return "Crystals";
        if (i.UICategoryRowId == UICategoryMeal || i.UICategoryRowId == UICategoryMedicine) return "Consumables";
        // Gathering categories: seafood/fish (47), gardening/seeds (48), and a few raw mats.
        if (i.UICategoryRowId == 47 || i.UICategoryRowId == 48 || i.UICategoryRowId == 38 || i.UICategoryRowId == 39 || i.UICategoryRowId == 40)
            return "Gathering";
        if (i.IsStackable) return "Crafting";
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

    /// <summary>
    /// Build a full-rebuild Apply-Zones plan. Treats the inventory as a blank canvas:
    /// every item is categorised, sorted within its category, and assigned to the painted
    /// layout slots in order. Overflow items (and items whose category has no zone) are
    /// placed into any remaining "None"-tagged slots. Move operations are then computed
    /// using a virtual-swap simulator so the live inventory is rewritten safely.
    /// </summary>
    public static List<QueuedMove> BuildApplyVisualZonesPlan(
        IReadOnlyList<InventoryItemInfo> items,
        string[] layoutMap,
        bool[] bagFlags,
        Configuration config)
    {
        // 1) Categorise every item and group.
        var byCategory = new Dictionary<string, List<InventoryItemInfo>>();
        foreach (var item in items)
        {
            var cat = CategorizeForZone(item, config);
            if (!byCategory.TryGetValue(cat, out var list))
            {
                list = new List<InventoryItemInfo>();
                byCategory[cat] = list;
            }
            list.Add(item);
        }

        // 2) Sort each category deterministically.
        foreach (var key in byCategory.Keys.ToList())
            byCategory[key] = SortForCategory(key, byCategory[key]).ToList();

        // 3) Walk the layout in slot order to build per-tag slot lists + a free-slot list.
        var zoneSlots = new Dictionary<string, List<(InventoryType bag, int slot)>>();
        var freeSlots = new List<(InventoryType bag, int slot)>();
        for (int g = 0; g < 140; g++)
        {
            int bagIdx = g / 35;
            int slotIdx = g % 35;
            if (bagIdx >= bagFlags.Length || !bagFlags[bagIdx]) continue;
            var bag = InventoryService.PlayerBags[bagIdx];
            var tag = (layoutMap != null && g < layoutMap.Length ? layoutMap[g] : null) ?? "None";
            if (string.IsNullOrEmpty(tag) || tag == "None")
            {
                freeSlots.Add((bag, slotIdx));
            }
            else
            {
                if (!zoneSlots.TryGetValue(tag, out var list))
                {
                    list = new List<(InventoryType, int)>();
                    zoneSlots[tag] = list;
                }
                list.Add((bag, slotIdx));
            }
        }

        // 4) Assign sorted items to their zone slots; overflow goes to free-slot pool.
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

        // 5) Spill overflow into the unzoned ("None") slots in slot-order.
        int freeIdx = 0;
        foreach (var item in overflow)
        {
            while (freeIdx < freeSlots.Count && target.ContainsKey(freeSlots[freeIdx])) freeIdx++;
            if (freeIdx >= freeSlots.Count) break; // no remaining capacity – will be left in place
            target[freeSlots[freeIdx]] = item;
            freeIdx++;
        }

        // 6) Virtual swap simulator: walk target slots in deterministic order and emit moves.
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

            // Apply the swap to our virtual model so later moves see the correct state.
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

