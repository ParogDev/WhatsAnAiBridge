namespace WhatsAnAiBridge;

/// <summary>
/// Every string the player reads on the outage card (GuidePanelOutage.cs) and its toasts (Outage.cs), in one place. From
/// the design (fable-outage-card.md in the hud-not-now work), adapted to the runner: the HUD stops only on the player's
/// Restart now, then after a short countdown (phase down + stopAt) that Cancel can still stop. ASCII only (the HUD font has
/// no other glyphs). {0}-style placeholders are filled with string.Format; counts of agents go through Agents / AgentsWait
/// / AgentsResume so "1 agent" reads right.
/// </summary>
internal static class OutageCardText
{
    // ── Card header (caps) ───────────────────────────────────────────
    public const string TitleWaits = "GAME + HUD RESTART WAITS";   // busy agents hold it (warn tone)
    public const string Title = "GAME + HUD RESTART";              // ready, answered, stopping (accent)

    // ── Lead line (font f) ───────────────────────────────────────────
    public const string LeadWaitsOne = "Waits for 1 agent to finish:";
    public const string LeadWaits = "Waits for {0} of {1} agents to finish:";            // {0} busy, {1} all
    public const string LeadReadyAll = "All {0} ready - press Restart now to stop the HUD.";   // {0} = Agents(T), T > 1: "All 3 agents ready"
    public const string LeadReadyOne = "The agent is ready - press Restart now to stop the HUD.";
    public const string LeadReadyNone = "No agents are working - press Restart now to stop the HUD.";
    public const string LeadStoppingTold = "Stopping the HUD - {0} told.";             // {0} = Agents(B), busy agents interrupted
    public const string LeadStopping = "Stopping the HUD.";
    public const string LeadStopsIn = "Stops the HUD in {0} s - then:";                 // phase down, stopAt ahead
    public const string LeadStopsNow = "Stopping the HUD now - then:";

    // ── Busy bullets ─────────────────────────────────────────────────
    public const string BusyLine = "{0}: {1}";          // name, what it is doing
    public const string BusyLeft = " ({0} left)";       // its work ends then (a lease's TTL)
    public const string BusyFor = " (for {0})";         // busy this long
    public const string MoreBusy = "+{0} more";

    // ── Explain line (small) ─────────────────────────────────────────
    public const string ExplainSomeReady = "{0} ready. Then: HUD stops - you restart game + HUD - agents resume.";   // {0} = R
    public const string ExplainNoneReady = "Then: the HUD stops - you restart game + HUD - agents resume.";
    public const string ExplainGo = "Then restart game + HUD by hand; agents resume when you are back.";

    // ── Meta line (small): who asked, when, why ──────────────────────
    public const string Meta = "{0} asked {1} ago - {2}";
    public const string MetaNoReason = "{0} asked {1} ago";

    // ── The stopping countdown (phase down): what the player does next ─
    public const string Step1 = "1. Restart the game";
    public const string Step2 = "2. Start the HUD";
    public const string Step3 = "3. Log in - agents resume when you are in game";
    // HUD startup: ~15 s when nothing stalls, ~35 s usually, up to ~100 s when ExileCore2's "Preload stats and mods" stalls.
    public const string StartupTypical = "HUD startup here: typically ~{0} s, up to ~100 s.";   // {0} = typicalStartupSec
    public const string StartupUsual = "HUD startup: usually ~35 s, up to ~100 s.";

    // ── Buttons and their tooltips ───────────────────────────────────
    public const string ButtonNow = "Restart now";
    public const string ButtonNowTipBusy = "Stop the HUD now anyway. It interrupts {0}; they are told why and resume after your restart.";   // {0} = "Name's doing, ..."
    public const string ButtonNowTipReady = "Stop the HUD now. You then restart the game and the HUD by hand.";
    public const string Interrupts = "{0}'s {1}";       // name, doing (joined with ", ", first 3, then "+N")
    public const string InterruptsMore = ", +{0} more";
    public const string ButtonNotNow = "Not now";
    public const string ButtonNotNowTip = "Cancel this restart. Agents carry on as before; ask again when it suits you.";
    public const string ButtonCancel = "Cancel";
    public const string ButtonCancelTip = "Keep the HUD running: cancels this restart. Agents carry on.";

    // ── Hover over the card's text ───────────────────────────────────
    public const string HoverTitle = "GAME + HUD RESTART";
    public const string HoverBody = "{0}. Asked by {1} {2} ago. The HUD stops when you press Restart now (after a short countdown you can still cancel). You then restart the game and the HUD by hand; agents wait and resume when you are back in game.";
    public const string HoverBodyNoReason = "Asked by {0} {1} ago. The HUD stops when you press Restart now (after a short countdown you can still cancel). You then restart the game and the HUD by hand; agents wait and resume when you are back in game.";
    public const string HoverBusy = "Busy: {0}";        // "Name: doing (time); Name: ..."
    public const string HoverReady = "Ready: {0}";      // names, up to 6
    public const string HoverReadyMany = "Ready: {0} agents";

    // ── Toasts (posted by the bridge only: N agents never make N toasts) ─
    public const string ToastCancelledTitle = "RESTART CANCELLED";
    public const string ToastCancelledText = "Agents carry on. Ask again when it suits you.";
    public const double ToastCancelledHold = 4;
    public const string ToastNowTitle = "RESTART NOW";   // Restart now pressed while agents were busy
    public const string ToastNowText = "Stopping the HUD - {0} told why; they resume after your restart.";   // {0} = Agents(B)
    public const double ToastNowHold = 4;
    public const string ToastDownTitle = "HUD STOPS - YOUR TURN";   // phase down without a countdown on the card
    public const string ToastDownText = "Restart the game, then the HUD. {0}.";       // {0} = AgentsWait(T)
    public const string ToastDownTextNone = "Restart the game, then the HUD.";
    public const double ToastDownHold = 30;
    public const string ToastUpTitle = "HUD IS UP";       // the new bridge, not in game yet
    public const string ToastUpText = "Log in - {0} when you are in game.";            // {0} = AgentsResume(T)
    public const string ToastUpTextNone = "Log in - agents resume when you are in game.";
    public const double ToastUpHold = 10;
    public const string ToastUpTag = "outage:waitingGame";   // retired on the first in-game frame
    public const string ToastBackTitle = "BACK";
    public const string ToastBackText = "{0} - {1} from the HUD stop to here.";       // AgentsResume(T), elapsed
    public const string ToastBackTextNone = "HUD and game back - {0} from the HUD stop to here.";
    public const string ToastBackTextNoTime = "{0}.";
    public const string ToastBackTextNoneNoTime = "HUD and game back.";
    public const double ToastBackHold = 5;

    public static string Agents(int n) => n == 1 ? "1 agent" : $"{n} agents";
    public static string AgentsWait(int n) => n == 1 ? "1 agent waits" : $"{n} agents wait";
    public static string AgentsResume(int n) => n == 1 ? "1 agent resumes" : $"{n} agents resume";
}
