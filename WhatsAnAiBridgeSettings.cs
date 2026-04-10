using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

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
}
