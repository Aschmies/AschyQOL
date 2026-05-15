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
    private const int MaxRebuildPasses = 3;

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
        
        // Initialize logging for categorization debug output
        QuickSortPresets.SetLogger(Log);
        QuickSortPresets.SetConfig(Configuration);

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

    /// <summary>
    /// All sort presets route through the unified full-rebuild engine. The caller supplies a
    /// label and a planner delegate; the planner returns a <see cref="QueuedMove"/> list which
    /// is enqueued via <see cref="SortQueueService.EnqueueDirect"/>.
    /// </summary>
    private void RunRebuild(string label, System.Func<List<InventoryItemInfo>, bool[], List<Services.QueuedMove>> planner)
    {
        if (SortQueue.IsBusy) return;

        if (!TryQueueRebuildPass(label, planner, 1, reportNoop: true))
            return;

        AttachRebuildCleanupPasses(label, planner, 2);
    }

    private bool TryQueueRebuildPass(
        string label,
        System.Func<List<InventoryItemInfo>, bool[], List<Services.QueuedMove>> planner,
        int pass,
        bool reportNoop)
    {
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = planner(items, bagFlags);
        if (moves.Count == 0)
        {
            if (reportNoop)
                SortQueue.StatusMessage = pass <= 1 ? $"{label}: nothing to move." : $"{label}: fully sorted in {pass - 1} pass(es).";
            return false;
        }

        var passLabel = pass == 1 ? label : $"{label} (cleanup pass {pass})";
        SortQueue.EnqueueDirect(moves, passLabel);
        return true;
    }

    private void AttachRebuildCleanupPasses(
        string label,
        System.Func<List<InventoryItemInfo>, bool[], List<Services.QueuedMove>> planner,
        int nextPass)
    {
        SortQueue.OnComplete = () =>
        {
            SortQueue.OnComplete = null;
            if (nextPass > MaxRebuildPasses) return;

            if (!TryQueueRebuildPass(label, planner, nextPass, reportNoop: true))
                return;

            AttachRebuildCleanupPasses(label, planner, nextPass + 1);
        };
    }

    public void RunSmartSort()
        => RunRebuild("Smart Sort", (items, flags) => QuickSortPresets.BuildSmartSortPlanV2(items, flags, Configuration));

    public void RunPreset(QuickPreset preset)
        => RunRebuild(preset.Name, (items, flags) => QuickSortPresets.BuildPresetRebuildPlan(preset, items, flags, Configuration));

    public void RunAllRules()
        => RunRebuild("Custom rules", (items, flags) => QuickSortPresets.BuildAllRulesRebuildPlan(Configuration.Rules, items, flags, Configuration));

    public void RunSingleRule(SortRule rule)
        => RunRebuild($"Rule: {rule.Name}", (items, flags) => QuickSortPresets.BuildSingleRuleRebuildPlan(rule, items, flags, Configuration));

    // ─── Advanced Sorts ───────────────────────────────────────────────────────

    public void RunGathererSort()
        => RunRebuild("The Gatherer", (items, flags) => QuickSortPresets.BuildGathererSortPlan(items, flags, Configuration));

    public void RunRaiderSort()
        => RunRebuild("The Raider", (items, flags) => QuickSortPresets.BuildRaiderSortPlan(items, flags, Configuration));

    public void RunHoarderSort()
        => RunRebuild("The Hoarder", (items, flags) => QuickSortPresets.BuildHoarderSortPlan(items, flags, Configuration));

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
        => RunRebuild("Extract Materia (100% spiritbond gear)", (items, flags) => QuickSortPresets.BuildExtractMateriaPlan(items, flags, Configuration));

    public void MergeStacks()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildMergeStacks(items, bagFlags);
        // Merge Stacks is a slot-merge operation (not a layout rebuild), so it still uses the
        // legacy enqueue path which emits move-onto-occupied-slot operations to trigger the
        // game's stack merge.
        SortQueue.Enqueue(moves.Select(m => (m.Item, m.DestBag, m.DestSlot, m.SrcSlotOverride)), "Merge Stacks");
    }

    public void GroupVendorTrash()
        => RunRebuild("Group Vendor Trash", (items, flags) => QuickSortPresets.BuildVendorTrashPlan(items, flags, Configuration));

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

        RunRebuild("Apply Visual Zones", (scanItems, flags) =>
            QuickSortPresets.BuildApplyVisualZonesPlan(scanItems, Configuration.VisualZoneLayout, flags, Configuration));
    }

    public void SyncLayoutToOtherBag(InventoryType sourceBag, InventoryType targetBag, List<uint?> template)
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.ApplyLayoutTemplate(items, template, sourceBag, targetBag);
        // SyncLayout is item-id-based template matching; route through the legacy queue path.
        SortQueue.Enqueue(moves.Select(m => (m.Item, m.DestBag, m.DestSlot, m.SrcSlotOverride)), $"Sync layout from {sourceBag} to {targetBag}");
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


