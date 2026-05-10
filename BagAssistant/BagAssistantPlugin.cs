using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using BagAssistant.Services;
using BagAssistant.Windows;
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

    public void StopSort() => SortQueue.Stop();

    private bool[] GetBagFlags() => new[]
    {
        Configuration.IncludeBag1,
        Configuration.IncludeBag2,
        Configuration.IncludeBag3,
        Configuration.IncludeBag4,
    };

    public void RunSmartSort()
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildSmartSortPlan(items, bagFlags);
        SortQueue.Enqueue(moves.Select(m => (m.Item, m.DestBag)), "Smart Sort");
    }

    public void RunPreset(QuickPreset preset)
    {
        if (SortQueue.IsBusy) return;
        var bagFlags = GetBagFlags();
        var items = InventoryService.ScanBags(bagFlags);
        var moves = QuickSortPresets.BuildPresetPlan(preset, items, bagFlags);
        SortQueue.Enqueue(moves.Select(m => (m.Item, m.DestBag)), preset.Name);
    }

    public void RunAllRules()
    {
        if (SortQueue.IsBusy) return;
        var plan = SortRunner.BuildPlan(Configuration);
        SortQueue.Enqueue(plan.Select(p => (p.Item, p.DestBag)), "Custom rules");
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
        SortQueue.Enqueue(moves, $"Rule: {rule.Name}");
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
