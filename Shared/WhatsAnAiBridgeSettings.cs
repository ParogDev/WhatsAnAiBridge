
namespace WhatsAnAiBridge;

// Labels, descriptions and grouping live here as attributes, read by both the in-game panel (Shared\SettingsMenu.cs)
// and settings.describe (Shared\SettingsControl.cs, the control center and hud_settings): [Menu] gives the label and
// the description, [MenuTab] the tab in game and the group in describe, [SettingPermission] marks the switches that
// give bridge clients power (settings.set refuses them without allowPermission). Property names are the saved JSON
// keys: never rename one.
public class WhatsAnAiBridgeSettings : ISettings
{
    [Menu("Enabled", "Turn the whole bridge on or off.")]
    [MenuTab("Bridge")]
    public ToggleNode Enable { get; set; } = new(true);

    // Bridge
    [Menu("Bridge folder", "Folder (in the HUD folder) for the port and token files, recordings, experiments and the observer journal.")]
    [MenuTab("Bridge")]
    public TextNode BridgeDirectory { get; set; } = new("claude-bridge");

    [Menu("File request poll (ms)", "How often the legacy file IPC checks for a request.")]
    [MenuTab("Bridge"), MenuUnit("ms")]
    public RangeNode<int> PollIntervalMs { get; set; } = new(250, 50, 2000);

    // TCP Server
    [Menu("TCP server", "Serve bridge clients (the MCP server) on 127.0.0.1.")]
    [MenuTab("Bridge")]
    public ToggleNode EnableTcp { get; set; } = new(true);

    [Menu("TCP port", "Port of the TCP server (written to bridge-port.txt).")]
    [MenuTab("Bridge")]
    public RangeNode<int> TcpPort { get; set; } = new(50900, 49152, 65535);

    [Menu("File IPC (legacy)", "Answer requests written as files.")]
    [MenuTab("Bridge")]
    public ToggleNode EnableFileIpc { get; set; } = new(true);

    // Status HUD
    [Menu("Status line", "The small bridge status line on the HUD.")]
    [MenuTab("Bridge")]
    public ToggleNode ShowStatusHud { get; set; } = new(true);

    [Menu("Status line X", "Horizontal position of the status line.")]
    [MenuTab("Bridge"), MenuUnit("px")]
    public RangeNode<int> HudX { get; set; } = new(10, 0, 3840);

    [Menu("Status line Y", "Vertical position of the status line.")]
    [MenuTab("Bridge"), MenuUnit("px")]
    public RangeNode<int> HudY { get; set; } = new(200, 0, 2160);

    // Limits
    [Menu("Entity range", "How far entity queries look (grid units).")]
    [MenuTab("Limits")]
    public RangeNode<int> MaxEntityRange { get; set; } = new(200, 50, 9999);

    [Menu("Stats per deep scan", "Most stat entries per entity in a deep scan.")]
    [MenuTab("Limits")]
    public RangeNode<int> MaxDeepStats { get; set; } = new(80, 10, 500);

    [Menu("UI children per element", "How many children of a UI element 'ui' queries scan.")]
    [MenuTab("Limits")]
    public RangeNode<int> MaxUiChildren { get; set; } = new(300, 50, 500);

    // Recording
    [Menu("Recording interval (ms)", "Time between snapshots while recording.")]
    [MenuTab("Recording"), MenuUnit("ms")]
    public RangeNode<int> RecordingIntervalMs { get; set; } = new(200, 50, 2000);

    [Menu("Recording entity range", "How far recorded snapshots look for entities (grid units).")]
    [MenuTab("Recording")]
    public RangeNode<int> RecordingEntityRange { get; set; } = new(200, 50, 9999);

    [Menu("Deep-scan bosses", "Record bosses' full stats automatically (a deep scan of Unique and Rare entities).")]
    [MenuTab("Recording")]
    public ToggleNode AutoDeepScanBosses { get; set; } = new(true);

    [Menu("Stats per recorded boss", "Most stat entries per deep-scanned entity in a recording.")]
    [MenuTab("Recording")]
    public RangeNode<int> RecordingMaxDeepStats { get; set; } = new(200, 10, 500);

    // Dev loop: let bridge clients (agents) recompile a source plugin in place, like the menu's
    // Reload button (hud.reload_plugin). The HUD pauses while the plugin compiles.
    [Menu("Allow plugin reload", "Lets agents recompile a source plugin in place (the HUD pauses while it compiles).")]
    [MenuTab("Permissions"), SettingPermission]
    public ToggleNode AllowPluginReload { get; set; } = new(true);

    // Dev loop: let bridge clients run C# scripts inside the HUD (script.run). Off by default:
    // it is arbitrary code in the HUD process. See Shared\ScriptRunner.cs.
    [Menu("Allow C# scripts", "Lets agents run arbitrary C# inside the HUD process. Turn it on only while you want it.")]
    [MenuTab("Permissions"), SettingPermission]
    public ToggleNode AllowCSharpScripts { get; set; } = new(false);

    // Render-fidelity research: let pipeline.trace patch the HUD's own unprotected code (Harmony) for a few seconds to
    // time reads, plugin Render and Present. Off by default; never patches protected methods. See Shared\PipelineTrace.cs.
    [Menu("Allow HUD instrumentation", "Lets traces and profiles patch the HUD's own code for a few seconds (Harmony). Never the game.")]
    [MenuTab("Permissions"), SettingPermission]
    public ToggleNode AllowHudInstrumentation { get; set; } = new(false);

    // Player-stats view state shared with the MCP App / agents (pins, filter, selection, sort).
    // Persisted here so it survives HUD restarts; Rev keeps increasing across restarts.
    // In game only its PanelOpen flag is a row (a Custom row in WhatsAnAiBridgeSettingsUi, through the view's mutator:
    // SettingsMenu draws a registered Custom row even under [IgnoreMenu]). [IgnoreMenu] keeps the HUD's own settings
    // parser from logging "StatsUiState is not a supported settings element" at every load.
    [Menu("Player stats panel", "In-game panel of the shared stats view: pins, filter and selection sync with the Claude app.")]
    [MenuTab("Stats panel"), IgnoreMenu]
    public StatsUiState StatsUi { get; set; } = new();

    // In-HUD stats panel (Shared\StatsPanel.cs): panel-only preferences, not part of the shared view.
    // The section collapse states are the panel's own clicks: not drawn in the settings panel.
    [Menu("Stats panel: vitals open", "Whether the panel's vitals section is open.")]
    [MenuTab("Stats panel"), IgnoreMenu]
    public ToggleNode StatsPanelVitalsOpen { get; set; } = new(true);

    [Menu("Stats panel: resistances open", "Whether the panel's resistances section is open.")]
    [MenuTab("Stats panel"), IgnoreMenu]
    public ToggleNode StatsPanelResistsOpen { get; set; } = new(true);

    [Menu("Stats panel: pinned open", "Whether the panel's pinned section is open.")]
    [MenuTab("Stats panel"), IgnoreMenu]
    public ToggleNode StatsPanelPinnedOpen { get; set; } = new(true);

    [Menu("Stats panel: show keys", "Show Stats.dat keys instead of the in-game text in the panel's table.")]
    [MenuTab("Stats panel")]
    public ToggleNode StatsPanelShowKeys { get; set; } = new(false);

    // In-HUD agent guide (Shared\AgentGuide.cs, GuidePanel.cs): the agent's current instruction for the user and its log.
    [Menu("Agent guide card", "The card that shows what the agent asks you to do in game, plus its log. Hides itself when idle.")]
    [MenuTab("Agent guide")]
    public ToggleNode ShowAgentGuide { get; set; } = new(true);

    // Kept for old saves; unused (the log is a toast stack plus the hotkey's sheet now).
    [Menu("Guide log open", "Unused: kept for old saves.")]
    [MenuTab("Agent guide"), IgnoreMenu]
    public ToggleNode GuideLogOpen { get; set; } = new(true);

    // Toggles the agent log sheet under the guide card (GuidePanel.cs). None by default: the log then only shows as
    // toasts. HotkeyNodeV2 exists on both HUDs (Shared\Nodes via the per-game global usings) and supports modifiers.
    [Menu("Agent log hotkey", "Toggles the agent log sheet under the card, to select and copy lines. None: the log only shows as toasts. Set it in game.")]
    [MenuTab("Agent guide")]
    public HotkeyNodeV2 AgentLogHotkey { get; set; } = new();

    // Toast verbosity, per kind (GuidePanel.cs ToastShown). Lines of a kind that is off still go to the log sheet and
    // guide.state; only the toast is suppressed. Agent lines (every agent tool call) are off by default: noise while playing.
    [Menu("Toasts: agent activity", "Show a toast for every agent tool call (the grey ones). Off: they only go to the log.")]
    [MenuTab("Agent guide")]
    public ToggleNode ToastAgent { get; set; } = new(false);

    [Menu("Toasts: steps", "Show a toast when a guided step starts or is queued.")]
    [MenuTab("Agent guide")]
    public ToggleNode ToastStep { get; set; } = new(true);

    [Menu("Toasts: results", "Show a toast for results and done receipts.")]
    [MenuTab("Agent guide")]
    public ToggleNode ToastResult { get; set; } = new(true);

    [Menu("Toasts: warnings", "Show the amber warning toasts.")]
    [MenuTab("Agent guide")]
    public ToggleNode ToastWarn { get; set; } = new(true);

    [Menu("Toasts: errors", "Show the red error toasts.")]
    [MenuTab("Agent guide")]
    public ToggleNode ToastError { get; set; } = new(true);

    // HUD restarts asked for by agents (Shared\Sessions.cs): a request that nothing blocks still waits this long on the guide
    // card, with Not now, before it is granted. 0 = at once.
    [Menu("HUD restart hold (s)", "An agent's HUD restart that nothing blocks still waits this long on the card, with Not now. 0 = at once. Restarts never interrupt a running test unless you press Restart now.")]
    [MenuTab("Agent guide"), MenuUnit("s")]
    public RangeNode<int> RestartHoldSec { get; set; } = new(5, 0, 60);
}
