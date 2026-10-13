namespace WhatsAnAiBridge;

/// <summary>
/// Every string the player reads on the outage card (GuidePanelOutage.cs) and its toasts (Outage.cs), in one place.
/// Placeholder wording: the final text and layout come from the design (fable-outage-card.md). ASCII only (the HUD font
/// has no other glyphs). {0}-style placeholders are filled with string.Format where noted.
/// </summary>
internal static class OutageCardText
{
    // Header (caps, tracked): before and after an answer.
    public const string Title = "PLANNED RESTART";
    public const string TitleAnswered = "RESTARTING";

    // Header right side: who asked. {0} = the agent's display name.
    public const string AskedBy = "{0}";

    // Lead line. {0} = the reason (outage.set reason); LeadNoReason when none was given.
    public const string Lead = "Restart game + HUD: {0}";
    public const string LeadNoReason = "Restart the game and the HUD";

    // How it goes: stopOnly (we stop the HUD, the user restarts game + HUD by hand) vs a full restart.
    public const string HowStopOnly = "We stop the HUD; then you restart the game and the HUD.";
    public const string HowFull = "We restart the HUD for you.";

    // Who it waits for. {0} = the agent's display name, {1} = what it is doing.
    public const string BusyLine = "{0}: {1}";
    public const string WaitingFor = "Waiting for agents to finish:";
    public const string NobodyBusy = "No agent is busy - ready when you are.";
    // More busy agents than shown. {0} = how many more.
    public const string MoreBusy = "+{0} more";

    // After "Restart now", until the HUD stops.
    public const string AnsweredNow = "Stopping the HUD...";

    // Buttons and their tooltips.
    public const string ButtonNow = "Restart now";
    public const string ButtonNowTip = "Go ahead now, even if an agent is still busy.";
    public const string ButtonNotNow = "Not now";
    public const string ButtonNotNowTip = "Cancel this restart. The agent is told you declined.";

    // Toasts after an answer.
    public const string ToastTitle = "Restart";
    public const string ToastNowText = "Restarting: the HUD stops in a moment";
    public const string ToastNotNowText = "Restart cancelled";
}
