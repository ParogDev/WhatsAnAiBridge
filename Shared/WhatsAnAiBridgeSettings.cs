
namespace WhatsAnAiBridge;

public class WhatsAnAiBridgeSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);

    // Bridge
    public TextNode BridgeDirectory { get; set; } = new("claude-bridge");
    public RangeNode<int> PollIntervalMs { get; set; } = new(250, 50, 2000);

    // TCP Server
    public ToggleNode EnableTcp { get; set; } = new(true);
    public RangeNode<int> TcpPort { get; set; } = new(50900, 49152, 65535);
    public ToggleNode EnableFileIpc { get; set; } = new(true);

    // Status HUD
    public ToggleNode ShowStatusHud { get; set; } = new(true);
    public RangeNode<int> HudX { get; set; } = new(10, 0, 3840);
    public RangeNode<int> HudY { get; set; } = new(200, 0, 2160);

    // Limits
    public RangeNode<int> MaxEntityRange { get; set; } = new(200, 50, 9999);
    public RangeNode<int> MaxDeepStats { get; set; } = new(80, 10, 500);
    public RangeNode<int> MaxUiChildren { get; set; } = new(300, 50, 500);

    // Recording
    public RangeNode<int> RecordingIntervalMs { get; set; } = new(200, 50, 2000);
    public RangeNode<int> RecordingEntityRange { get; set; } = new(200, 50, 9999);
    public ToggleNode AutoDeepScanBosses { get; set; } = new(true);
    public RangeNode<int> RecordingMaxDeepStats { get; set; } = new(200, 10, 500);

    // Dev loop: let bridge clients (agents) recompile a source plugin in place, like the menu's
    // Reload button (hud.reload_plugin). The HUD pauses while the plugin compiles.
    public ToggleNode AllowPluginReload { get; set; } = new(true);

    // Dev loop: let bridge clients run C# scripts inside the HUD (script.run). Off by default:
    // it is arbitrary code in the HUD process. See Shared\ScriptRunner.cs.
    public ToggleNode AllowCSharpScripts { get; set; } = new(false);

    // Render-fidelity research: let pipeline.trace patch the HUD's own unprotected code (Harmony) for a few seconds to
    // time reads, plugin Render and Present. Off by default; never patches protected methods. See Shared\PipelineTrace.cs.
    public ToggleNode AllowHudInstrumentation { get; set; } = new(false);

    // Player-stats view state shared with the MCP App / agents (pins, filter, selection, sort).
    // Persisted here so it survives HUD restarts; Rev keeps increasing across restarts.
    public StatsUiState StatsUi { get; set; } = new();

    // In-HUD stats panel (Shared\StatsPanel.cs): panel-only preferences, not part of the shared view.
    public ToggleNode StatsPanelVitalsOpen { get; set; } = new(true);
    public ToggleNode StatsPanelResistsOpen { get; set; } = new(true);
    public ToggleNode StatsPanelPinnedOpen { get; set; } = new(true);
    public ToggleNode StatsPanelShowKeys { get; set; } = new(false);

    // In-HUD agent guide (Shared\AgentGuide.cs, GuidePanel.cs): the agent's current instruction for the user and its log.
    public ToggleNode ShowAgentGuide { get; set; } = new(true);
    public ToggleNode GuideLogOpen { get; set; } = new(true);   // panel-only: the log section's collapse state
    // Toggles the agent log sheet under the guide card (GuidePanel.cs). None by default: the log then only shows as
    // toasts. HotkeyNodeV2 exists on both HUDs (Shared\Nodes via the per-game global usings) and supports modifiers.
    public HotkeyNodeV2 AgentLogHotkey { get; set; } = new();
    // Toast verbosity, per kind (GuidePanel.cs ToastShown). Lines of a kind that is off still go to the log sheet and
    // guide.state; only the toast is suppressed. Agent lines (every agent tool call) are off by default: noise while playing.
    public ToggleNode ToastAgent { get; set; } = new(false);
    public ToggleNode ToastStep { get; set; } = new(true);
    public ToggleNode ToastResult { get; set; } = new(true);
    public ToggleNode ToastWarn { get; set; } = new(true);
    public ToggleNode ToastError { get; set; } = new(true);
    // HUD restarts asked for by agents (Shared\Sessions.cs): a request that nothing blocks still waits this long on the guide
    // card, with Not now, before it is granted. 0 = at once.
    public RangeNode<int> RestartHoldSec { get; set; } = new(5, 0, 60);
}
