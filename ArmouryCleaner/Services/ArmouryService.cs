using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArmouryCleaner.Services
{
    public record ArmouryItem(
        uint ItemId,
        string Name,
        int Slot,
        InventoryType ContainerType,
        uint Level,
        uint ILvl,
        byte Rarity,
        bool IsHQ,
        bool IsUntradeable,
        string[] ClassJobs
    );

    public sealed unsafe class ArmouryService(IDataManager dataManager, IPluginLog log)
    {
        private static readonly InventoryType[] ArmouryContainers =
        [
            InventoryType.ArmoryMainHand,
            InventoryType.ArmoryOffHand,
            InventoryType.ArmoryHead,
            InventoryType.ArmoryBody,
            InventoryType.ArmoryHands,
            InventoryType.ArmoryLegs,
            InventoryType.ArmoryFeets,
            InventoryType.ArmoryEar,
            InventoryType.ArmoryNeck,
            InventoryType.ArmoryWrist,
            InventoryType.ArmoryRings,
            InventoryType.ArmorySoulCrystal,
        ];

        private static readonly InventoryType[] PlayerBags =
        [
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        ];

        public List<ArmouryItem> ScanCandidates(Configuration config)
        {
            var results = new List<ArmouryItem>();
            var itemSheet = dataManager.GetExcelSheet<Item>();
            if (itemSheet == null)
                return results;

            var mgr = InventoryManager.Instance();
            if (mgr == null)
                return results;

            foreach (var invType in ArmouryContainers)
            {
                var container = mgr->GetInventoryContainer(invType);
                if (container == null) continue;

                for (var slot = 0; slot < container->Size; slot++)
                {
                    var invItem = container->GetInventorySlot(slot);
                    if (invItem == null || invItem->ItemId == 0) continue;

                    var itemData = itemSheet.GetRow(invItem->ItemId);
                    if (itemData.RowId == 0) continue;

                    var isHQ = (invItem->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                    var isUntradeable = itemData.IsUntradable;
                    var equipLevel = itemData.LevelEquip;
                    var ilvl = itemData.LevelItem.RowId;
                    var rarity = itemData.Rarity;
                    var classJobs = GetClassJobs(itemData);

                    if (config.SkipHighQuality && isHQ) continue;
                    if (config.SkipUntradeable && isUntradeable) continue;
                    if (config.SkipWhite  && rarity == 1) continue;
                    if (config.SkipGreen  && rarity == 2) continue;
                    if (config.SkipBlue   && rarity == 3) continue;
                    if (config.SkipPurple && rarity == 4) continue;
                    if (config.SkipPink   && rarity == 7) continue;
                    if (equipLevel < config.MinLevel || equipLevel > config.MaxLevel) continue;
                    if (config.FilterByIlvl && (ilvl < (uint)config.MinIlvl || ilvl > (uint)config.MaxIlvl)) continue;
                    if (config.SelectedJobs.Count > 0 && !classJobs.Any(j => config.SelectedJobs.Contains(j))) continue;

                    results.Add(new ArmouryItem(
                        invItem->ItemId,
                        itemData.Name.ToString(),
                        slot,
                        invType,
                        equipLevel,
                        ilvl,
                        rarity,
                        isHQ,
                        isUntradeable,
                        classJobs
                    ));
                }
            }

            return results;
        }

        public (bool Success, string Message) MoveToInventory(ArmouryItem item)
        {
            var mgr = InventoryManager.Instance();
            if (mgr == null)
                return (false, "InventoryManager unavailable.");

            foreach (var bagType in PlayerBags)
            {
                var bag = mgr->GetInventoryContainer(bagType);
                if (bag == null) continue;

                for (var slot = 0; slot < bag->Size; slot++)
                {
                    var bagSlot = bag->GetInventorySlot(slot);
                    if (bagSlot == null || bagSlot->ItemId != 0) continue;

                    var result = mgr->MoveItemSlot(item.ContainerType, (ushort)item.Slot, bagType, (ushort)slot, true);
                    if (result == 0)
                    {
                        log.Debug($"[ArmouryCleaner] Moved '{item.Name}' from {item.ContainerType}[{item.Slot}] to {bagType}[{slot}].");
                        return (true, $"Moved '{item.Name}' to inventory.");
                    }

                    return (false, $"Move failed (code {result}).");
                }
            }

            return (false, "No free inventory slot found.");
        }

        /// <summary>
        /// Discards directly from the armoury container without staging through the player
        /// inventory. Faster (single game packet) and avoids briefly polluting the bag, but the
        /// game may reject some discards from armoury slots — caller should fall back to
        /// MoveAndDiscard if this returns Success=false with a non-zero discard code.
        /// </summary>
        public (bool Success, string Message) DiscardFromArmoury(ArmouryItem item)
        {
            var mgr = InventoryManager.Instance();
            if (mgr == null)
                return (false, "InventoryManager unavailable.");

            var discardResult = mgr->DiscardItem(item.ContainerType, (ushort)item.Slot);
            if (discardResult == 0)
            {
                log.Debug($"[ArmouryCleaner] Discarded '{item.Name}' directly from {item.ContainerType}[{item.Slot}].");
                return (true, $"Discarded '{item.Name}' from armoury.");
            }

            log.Warning($"[ArmouryCleaner] Direct discard from {item.ContainerType}[{item.Slot}] failed (code {discardResult}) for '{item.Name}'.");
            return (false, $"Direct discard failed (code {discardResult}).");
        }

        public (bool Success, string Message) MoveAndDiscard(ArmouryItem item)
        {
            var mgr = InventoryManager.Instance();
            if (mgr == null)
                return (false, "InventoryManager unavailable.");

            // Find a free bag slot
            foreach (var bagType in PlayerBags)
            {
                var bag = mgr->GetInventoryContainer(bagType);
                if (bag == null) continue;

                for (var slot = 0; slot < bag->Size; slot++)
                {
                    var bagSlot = bag->GetInventorySlot(slot);
                    if (bagSlot == null || bagSlot->ItemId != 0) continue;

                    // Move from armoury to bag
                    var moveResult = mgr->MoveItemSlot(item.ContainerType, (ushort)item.Slot, bagType, (ushort)slot, true);
                    if (moveResult != 0)
                        return (false, $"Move failed (code {moveResult}).");

                    // Immediately discard from the bag slot
                    var discardResult = mgr->DiscardItem(bagType, (ushort)slot);
                    if (discardResult == 0)
                    {
                        log.Debug($"[ArmouryCleaner] Discarded '{item.Name}' via {bagType}[{slot}].");
                        return (true, $"Discarded '{item.Name}'.");
                    }

                    log.Warning($"[ArmouryCleaner] Move succeeded but discard failed (code {discardResult}) for '{item.Name}' — item is now in {bagType}[{slot}].");
                    return (false, $"Moved to inventory but discard failed — remove it manually.");
                }
            }

            return (false, "No free inventory slot found.");
        }

        private static string[] GetClassJobs(Item item)
        {
            var cat = item.ClassJobCategory.Value;
            var jobs = new List<string>();

            if (cat.GLA) jobs.Add("GLA");
            if (cat.PGL) jobs.Add("PGL");
            if (cat.MRD) jobs.Add("MRD");
            if (cat.LNC) jobs.Add("LNC");
            if (cat.ARC) jobs.Add("ARC");
            if (cat.CNJ) jobs.Add("CNJ");
            if (cat.THM) jobs.Add("THM");
            if (cat.ACN) jobs.Add("ACN");
            if (cat.ROG) jobs.Add("ROG");
            if (cat.PLD) jobs.Add("PLD");
            if (cat.MNK) jobs.Add("MNK");
            if (cat.WAR) jobs.Add("WAR");
            if (cat.DRG) jobs.Add("DRG");
            if (cat.BRD) jobs.Add("BRD");
            if (cat.WHM) jobs.Add("WHM");
            if (cat.BLM) jobs.Add("BLM");
            if (cat.SMN) jobs.Add("SMN");
            if (cat.SCH) jobs.Add("SCH");
            if (cat.NIN) jobs.Add("NIN");
            if (cat.MCH) jobs.Add("MCH");
            if (cat.DRK) jobs.Add("DRK");
            if (cat.AST) jobs.Add("AST");
            if (cat.SAM) jobs.Add("SAM");
            if (cat.RDM) jobs.Add("RDM");
            if (cat.BLU) jobs.Add("BLU");
            if (cat.GNB) jobs.Add("GNB");
            if (cat.DNC) jobs.Add("DNC");
            if (cat.RPR) jobs.Add("RPR");
            if (cat.SGE) jobs.Add("SGE");
            if (cat.VPR) jobs.Add("VPR");
            if (cat.PCT) jobs.Add("PCT");

            return [.. jobs];
        }
    }
}
