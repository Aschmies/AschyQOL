using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using BagAssistant.Services;
using BagAssistant.Windows;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Collections.Generic;
using System.Linq;

namespace BagAssistant;

public sealed class BagAssistantPlugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/bagassistant";
    private const string ShortCommand = "/ba";

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("BagAssistant");
    private readonly BagAssistantWindow mainWindow;
    private readonly InventoryOverlayWindow overlayWindow;
    internal readonly InventoryService InventoryService;
    internal readonly SortRunner SortRunner;
    internal readonly SortQueueService SortQueue;

    public BagAssistantPlugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        InventoryService = new InventoryService(DataManager, Log);
        SortRunner = new SortRunner(InventoryService);
        SortQueue = new SortQueueService(InventoryService, Configuration);

        mainWindow = new BagAssistantWindow(this, DataManager);
        overlayWindow = new InventoryOverlayWindow(this, GameGui);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(overlayWindow);
        // Overlay window is always "open"; its DrawConditions decides per-frame visibility.
        overlayWindow.IsOpen = true;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Bag Assistant | /bagassistant",
        });
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Bag Assistant | /ba",
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    private void OnCommand(string command, string args) => ToggleMainUi();

    private void DrawUI()
    {
        // Tick the move queue exactly once per frame, before any window references its state.
        SortQueue.Tick();
        windowSystem.Draw();
    }

    public void ToggleMainUi() => mainWindow.Toggle();

    // ─── Public sort actions (used by main window + inventory overlay) ──────

    public bool IsSortQueueBusy => SortQueue.IsBusy;
    public int SortQueueRemaining => SortQueue.Remaining;
    public int SortQueueTotal => SortQueue.Total;
    public string SortQueueStatus => SortQueue.StatusMessage;
    public bool CanUndo => SortQueue.CanUndo;

    public void StopSort() => SortQueue.Stop();
    public void UndoLastSort() => SortQueue.Undo();

    private bool[] GetBagFlags() => new[]
    {
        Configuration.IncludeBag1,
        Configuration.IncludeBag2,
        Configuration.IncludeBag3,
        Configuration.IncludeBag4,
    };

    private void EnqueueWithIntraBagSort(List<QuickMove> moves, string label, bool[] bagFlags)
    {
        SortQueue.Enqueue(moves.Select(m => (m.Item, m.DestBag, m.DestSlot, m.SrcSlotOverride)), label);
        
        SortQueue.OnComplete = () => {
            var items = InventoryService.ScanBags(bagFlags);
            var intraMoves = QuickSortPresets.BuildIntraBagSort(items, bagFlags, Configuration);
            if (intraMoves.Count > 0)
            {
                SortQueue.Enqueue(intraMoves.Select(m => (m.Item, m.DestBag, m.DestSlot, m.SrcSlotOverride)), $" (In-bag Sort)");
            }
        };
    }

    public void RunSmartSort()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildSmartSortPlan(items, bagFlags);
        EnqueueWithIntraBagSort(moves, "Smart Sort", bagFlags);
    }

    public void RunPreset(QuickPreset preset)
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildPresetPlan(preset, items, bagFlags);
        EnqueueWithIntraBagSort(moves, preset.Name, bagFlags);
    }

    public void RunAllRules()
    {
        if (SortQueue.IsBusy) return;
        var plan = SortRunner.BuildPlan(Configuration);
        SortQueue.Enqueue(plan.Select(p => (p.Item, p.DestBag, (int?)null, (int?)null)), "Custom rules");
    }

    public void RunSingleRule(SortRule rule)
    {
        if (SortQueue.IsBusy) return;
        if (rule.Target != SortTarget.SpecificBag) return;
        var bagFlags = GetBagFlags();
        var destIdx = System.Math.Clamp(rule.TargetBagIndex, 0, 3);
        if (!bagFlags[destIdx]) return;
        var destBag = InventoryService.PlayerBags[destIdx];
        var items = InventoryService.ScanBags(bagFlags);
        var moves = new List<(InventoryItemInfo Item, FFXIVClientStructs.FFXIV.Client.Game.InventoryType DestBag)>();
        foreach (var item in items)
        {
            if (!RuleMatcher.Matches(rule, item)) continue;
            if (item.Container == destBag) continue;
            moves.Add((item, destBag));
        }
        SortQueue.Enqueue(moves.Select(m => (m.Item, m.DestBag, (int?)null, (int?)null)), $"Rule: {rule.Name}");
    }

    // ─── Advanced Sorts ───────────────────────────────────────────────────────

    public void RunGathererSort()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildGathererSort(items, bagFlags);
        EnqueueWithIntraBagSort(moves, "The Gatherer", bagFlags);
    }

    public void RunRaiderSort()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildRaiderSort(items, bagFlags);
        EnqueueWithIntraBagSort(moves, "The Raider", bagFlags);
    }

    public void RunHoarderSort()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildHoarderSort(items, bagFlags);
        EnqueueWithIntraBagSort(moves, "The Hoarder", bagFlags);
    }

        public List<InventoryItemInfo> GetJunkItems()
    {
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        return items.Where(i => QuickSortPresets.IsJunk(i, Configuration)).ToList();
    }

    public void DeleteSpecificJunk(List<InventoryItemInfo> junk)
    {
        if (SortQueue.IsBusy) return;
        var discardCount = 0;
        foreach (var item in junk)
        {
            unsafe
            {
                var mgr = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                if (mgr != null)
                {
                    mgr->DiscardItem(item.Container, (ushort)item.Slot);
                    discardCount++;
                }
            }
        }
        SortQueue.StatusMessage = $"Discarded {discardCount} junk item(s).";
    }

    public void DeleteJunk()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        // Junk = white rarity (vendor trash)
        var junk = items.Where(i => QuickSortPresets.IsJunk(i, Configuration)).ToList();
        if (junk.Count == 0)
        {
            SortQueue.StatusMessage = "No junk (white items) found.";
            return;
        }
        // Discard each junk item via InventoryManager.DiscardItem
        var discardCount = 0;
        foreach (var item in junk)
        {
            unsafe
            {
                var mgr = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                if (mgr != null)
                {
                    mgr->DiscardItem(item.Container, (ushort)item.Slot);
                    discardCount++;
                }
            }
        }
        SortQueue.StatusMessage = $"Discarded {discardCount} junk item(s).";
    }

    // ─── v1.0.4 Operations ────────────────────────────────────────────────────

    public void ExtractMateria()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildExtractMateria(items, bagFlags);
        EnqueueWithIntraBagSort(moves, "Extract Materia (100% spiritbond gear)", bagFlags);
    }

    public void MergeStacks()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildMergeStacks(items, bagFlags);
        EnqueueWithIntraBagSort(moves, "Merge Stacks", bagFlags);
    }

    public void GroupVendorTrash()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildVendorTrashGroup(items, bagFlags, Configuration);
        EnqueueWithIntraBagSort(moves, "Group Vendor Trash", bagFlags);
    }

    public List<InventoryItemInfo> SearchByKeyword(string keyword)
    {
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        return QuickSortPresets.SearchByKeyword(items, keyword);
    }

    public List<uint?> SnapshotBagLayout(InventoryType bagType)
    {
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        return QuickSortPresets.SnapshotBagLayout(items, bagType);
    }

        public void ApplyVisualZones(bool skipAutoMerge = false)
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);

        if (Configuration.ApplyZonesAutoMerge && !skipAutoMerge)
        {
            var mergeMoves = QuickSortPresets.BuildMergeStacks(items, bagFlags);
            if (mergeMoves.Count > 0)
            {
                SortQueue.Enqueue(
                    mergeMoves.Select(m => (m.Item, m.DestBag, m.DestSlot, m.SrcSlotOverride)),
                    "Apply Zones – Merge stacks");
                // Chain the layout arrangement to run immediately after the merge finishes.
                SortQueue.OnComplete = () =>
                {
                    SortQueue.OnComplete = null;
                    ApplyVisualZones(skipAutoMerge: true);
                };
                return;
            }
        }

        // Full rebuild: categorize → sort → assign to painted slots → emit minimal swap moves.
        var plan = QuickSortPresets.BuildApplyVisualZonesPlan(items, Configuration.VisualZoneLayout, bagFlags, Configuration);
        SortQueue.EnqueueDirect(plan, "Apply Visual Zones (full rebuild)");
    }

    public void SyncLayoutToOtherBag(InventoryType sourceBag, InventoryType targetBag, List<uint?> template)
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.ApplyLayoutTemplate(items, template, sourceBag, targetBag);
        EnqueueWithIntraBagSort(moves, $"Sync layout from {sourceBag} to {targetBag}", bagFlags);
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommand);
        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        overlayWindow.Dispose();
    }
}


