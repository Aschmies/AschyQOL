using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MinimapPlugin.Services;
using MinimapPlugin.Windows;

namespace MinimapPlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private const string CommandName   = "/minimap";
    private const string CommandConfig = "/minimapconfig";

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("MinimapPlugin");
    private readonly MapDataService mapDataService;
    private readonly EntityService entityService;
    private readonly MinimapWindow minimapWindow;
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        mapDataService = new MapDataService(DataManager, TextureProvider, Log);
        entityService  = new EntityService(ObjectTable, PartyList, ClientState, Log);
        configWindow   = new ConfigWindow(Configuration);
        minimapWindow  = new MinimapWindow(Configuration, mapDataService, entityService, ClientState, Condition, GameGui);

        windowSystem.AddWindow(minimapWindow);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the minimap overlay. /minimap config — open settings. /minimap zoom <value> — set zoom level.",
        });
        CommandManager.AddHandler(CommandConfig, new CommandInfo(OnConfigCommand)
        {
            HelpMessage = "Open minimap settings.",
        });

        PluginInterface.UiBuilder.Draw        += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleMainUi;

        ClientState.MapIdChanged += OnMapIdChanged;

        // Prime the map for the current zone using MapId directly (updated every frame by Dalamud from AgentMap)
        if (ClientState.MapId != 0)
            mapDataService.LoadMapForMapId(ClientState.MapId, ClientState.TerritoryType);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnMapIdChanged(uint mapId)
        => mapDataService.LoadMapForMapId(mapId, ClientState.TerritoryType);

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim();

        if (arg.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            configWindow.IsOpen = true;
            return;
        }

        if (arg.StartsWith("zoom ", StringComparison.OrdinalIgnoreCase))
        {
            var zoomStr = arg[5..].Trim();
            if (float.TryParse(zoomStr, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var zoom))
            {
                Configuration.ZoomLevel = Math.Clamp(zoom, Configuration.ZoomMin, Configuration.ZoomMax);
                Configuration.Save();
            }
            return;
        }

        // Toggle visibility
        Configuration.IsVisible = !Configuration.IsVisible;
        minimapWindow.IsOpen    = Configuration.IsVisible;
        Configuration.Save();
    }

    private void OnConfigCommand(string command, string args)
        => configWindow.IsOpen = true;

    private void DrawUI()  => windowSystem.Draw();
    private void OpenConfigUi() => configWindow.IsOpen = true;

    private void ToggleMainUi()
    {
        Configuration.IsVisible = !Configuration.IsVisible;
        minimapWindow.IsOpen    = Configuration.IsVisible;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        ClientState.MapIdChanged -= OnMapIdChanged;

        PluginInterface.UiBuilder.Draw        -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleMainUi;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandConfig);

        mapDataService.Dispose();
        windowSystem.RemoveAllWindows();
        minimapWindow.Dispose();
    }
}
