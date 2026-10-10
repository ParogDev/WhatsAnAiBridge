using System;
using System.Collections.Generic;
using ImGuiNET;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

using GuideSnap =(string? title, string? instruction, int? step, int? steps, string status, string? detail,
    DateTime updatedAt, DateTime? instructionSince, int rev, WhatsAnAiBridge.GuideLogEntry[] log, string? who);

/// <summary>
/// What the guide's toast stack shows, and the combat sense that mutes part of it (attention v2 proposals, issue #66).
/// The log (AgentGuide.cs) is unchanged: every line still goes to the log sheet and guide.state. The toast stack is a
/// feed built from it as lines arrive, under the rules GuideAttentionFor hands out:
///   - Repeat merging: a line equal to the newest toast (kind, title and text; lines of kinds that are off do not
///     break a run) bumps that toast's count and restarts its life instead of stacking ("Recorded x3").
///   - Echoes: while highlights are on screen, a line that says exactly what a visible highlight label (or the
///     highlight's title) already says is dropped.
///   - Combat hold (ToastHold): agent and result lines are held while a hostile is near, and toasts of those kinds
///     already up are taken down into the hold. When the hold ends they come back as one toast ("3 UPDATES" with the
///     latest line; a single held line comes back as itself). Warnings, errors and steps are never held, and the
///     per-kind verbosity settings apply before anything is counted.
/// Allocation happens only when a line arrives or the hold changes; drawing reads the feed as is.
/// </summary>
public partial class WhatsAnAiBridge
{
    /// <summary>One toast in the stack: a log line (with its repeat count) or the summary of a combat hold.</summary>
    private sealed class GuideToast
    {
        public string Kind = "agent";
        public string? Title;        // the agent's own title (merge key), null = from the kind
        public string Text = "";     // the line as logged (merge key)
        public string TitleCaps = "";  // drawn: built once when the toast is made
        public string TextCaps = "";
        public int Count = 1;        // repeats merged into this toast
        public string CountText = ""; // "x3" once Count > 1 (built when it changes)
        public DateTime At;          // when the toast's life started (its newest repeat, or the hold's release)
        public DateTime Born;        // when the toast first appeared: its slide-in and the stack's push key off this, so
                                     // a repeat restarts the life (At) without replaying the entry; only the chip bumps
        public bool Summary;         // the "N updates" toast after a combat hold (shown whatever its kind's setting)
    }

    // Repeats merge while the next equal line comes within this long of the last one (a toast lives 3 s, so a merged
    // toast can come back as "x2" a few seconds later rather than as a second plain copy).
    private const double GuideRepeatMergeSec = 10;
    private const int GuideToastKeep = 8;

    private readonly List<GuideToast> _toasts = new();   // oldest first
    private GuideLogEntry? _toastSeen;                    // the last log line fed to the stack
    private DateTime _toastShownAt = DateTime.MinValue;   // the last toast that showed (or repeat that bumped one): the quiet clock
    private bool _toastHolding;
    private int _toastHeld;                               // lines held since the hold started
    private GuideLogEntry? _toastHeldLast;

    // ── Combat sense ─────────────────────────────────────────────────
    // An alive hostile monster within CombatGrid grid cells of the player, sampled at 4 Hz from the HUD's per-type
    // monster list (never all entities), held CombatHoldSec after the last one was seen. Never in town or hideout.
    private const float CombatGrid = 60f;
    private const double CombatSampleSec = 0.25, CombatHoldSec = 3.0;
    private double _combatSampledAt = -1e9, _combatSeenAt = -1e9;
    private string? _combatBroken;   // the link that broke (exception text), reported once; the sense is then off

    private bool CombatNear(double now)
    {
        if (now - _combatSampledAt < CombatSampleSec) return now - _combatSeenAt < CombatHoldSec;
        _combatSampledAt = now;
        if (_combatBroken == null)
        {
            try
            {
                switch (HostileNear())
                {
                    case true: _combatSeenAt = now; break;
                    case null: _combatSeenAt = -1e9; break;   // town, hideout, loading: release at once
                }
            }
            catch (Exception ex)
            {
                _combatBroken = $"combat sense: {ex.GetType().Name}: {ex.Message}";
                LogError($"[GuidePanel] {_combatBroken} (combat muting is off until the plugin reloads)");
                _combatSeenAt = -1e9;
            }
        }
        return now - _combatSeenAt < CombatHoldSec;
    }

    /// <summary>True: an alive hostile is near. False: none. Null: no combat possible here (town, hideout, no player).</summary>
    private bool? HostileNear()
    {
        var area = GameController.Area?.CurrentArea;
        if (area == null || area.IsTown || area.IsHideout) return null;
        var player = GameController.Player;
        if (player == null || !player.IsValid) return null;
        if (!GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.Monster, out var ents)) return false;
        var pp = EntityGridNum(player);
        // Cheap tests first: distance (cached positioned data) before IsHostile / IsAlive (component reads).
        for (var i = 0; i < ents.Count; i++)
        {
            var e = ents[i];
            if (e == null || !e.IsValid) continue;
            var gp = EntityGridNum(e);
            float dx = gp.X - pp.X, dy = gp.Y - pp.Y;
            if (dx * dx + dy * dy > CombatGrid * CombatGrid) continue;
            if (e.IsHostile && e.IsAlive) return true;
        }
        return false;
    }

    // ── Feed ─────────────────────────────────────────────────────────

    /// <summary>
    /// Feed the log lines that arrived since last frame into the toast stack, under this frame's attention
    /// (ToastHold, DropEchoes). Called once per frame from DrawGuidePanel, after GuideAttentionFor.
    /// </summary>
    private void FeedGuideToasts(in GuideSnap g, in GuideAttention at, List<HighlightLayerView> hl, DateTime utc)
    {
        // The hold starts: toasts of the held kinds already up go down into it (they come back in the summary).
        if (at.ToastHold && !_toastHolding)
        {
            _toastHolding = true;
            for (var i = _toasts.Count - 1; i >= 0; i--)
            {
                var t = _toasts[i];
                if (!GuideHeldKind(t.Kind) || (utc - t.At).TotalSeconds >= GuideToastSec) continue;
                _toastHeld += t.Count;
                if (_toastHeldLast == null) _toastHeldLast = new GuideLogEntry { Kind = t.Kind, Text = t.Text, Title = t.Title, At = t.At };
                _toasts.RemoveAt(i);
            }
        }

        // New lines since the last one fed (the log keeps 40: when the last one fed was trimmed away, everything still
        // young enough to be a toast is new).
        var start = -1;
        if (_toastSeen != null)
            for (var i = g.log.Length - 1; i >= 0; i--) if (ReferenceEquals(g.log[i], _toastSeen)) { start = i + 1; break; }
        if (start < 0)
        {
            start = g.log.Length;
            while (start > 0 && (utc - g.log[start - 1].At).TotalSeconds < GuideToastSec) start--;
        }
        for (var i = start; i < g.log.Length; i++) OfferGuideToast(g, g.log[i], at, hl);
        if (g.log.Length > 0) _toastSeen = g.log[^1];

        // The hold ends: everything held comes back as one toast, starting now.
        if (!at.ToastHold && _toastHolding)
        {
            _toastHolding = false;
            if (_toastHeld == 1 && _toastHeldLast != null)
                PushGuideToast(g, _toastHeldLast.Kind, _toastHeldLast.Title, _toastHeldLast.Text, utc, false, 1);
            else if (_toastHeld > 1 && _toastHeldLast != null)
                PushGuideToast(g, "result", $"{_toastHeld} updates", _toastHeldLast.Text, utc, true, _toastHeld);
            _toastHeld = 0;
            _toastHeldLast = null;
        }

        // Forget toasts long gone (the stack draws at most GuideToastMax; a few more are kept for merging).
        while (_toasts.Count > GuideToastKeep || (_toasts.Count > 0 && (utc - _toasts[0].At).TotalSeconds > GuideRepeatMergeSec)) _toasts.RemoveAt(0);
    }

    private static bool GuideHeldKind(string kind) => kind is "agent" or "result";

    private void OfferGuideToast(in GuideSnap g, GuideLogEntry e, in GuideAttention at, List<HighlightLayerView> hl)
    {
        if (!ToastShown(e.Kind)) return;   // verbosity first: a kind that is off is neither shown nor counted
        if (at.ToastHold && GuideHeldKind(e.Kind))
        {
            _toastHeld++;
            _toastHeldLast = e;
            return;
        }
        if (at.DropEchoes && GuideEchoes(e.Text, hl)) return;
        var top = _toasts.Count > 0 ? _toasts[^1] : null;
        if (top is { Summary: false } && top.Kind == e.Kind && top.Title == e.Title && top.Text == e.Text
            && (e.At - top.At).TotalSeconds < GuideRepeatMergeSec)
        {
            top.Count++;
            top.CountText = "x" + top.Count;
            top.At = e.At;
            _toastShownAt = e.At;
            return;
        }
        PushGuideToast(g, e.Kind, e.Title, e.Text, e.At, false, 1);
    }

    private void PushGuideToast(in GuideSnap g, string kind, string? title, string text, DateTime at, bool summary, int count)
    {
        var t = new GuideToast { Kind = kind, Title = title, Text = text, At = at, Born = at, Summary = summary, Count = summary ? count : 1 };
        t.TitleCaps = GuideToastTitle(new GuideLogEntry { Kind = kind, Title = title, Text = text }, g);
        t.TextCaps = GuideCaps(summary ? "Latest: " + text : text);
        _toasts.Add(t);
        if (at > _toastShownAt) _toastShownAt = at;
    }

    /// <summary>The line says what a visible highlight already says: equal to a box's label or the highlight's title
    /// (case, surrounding blanks and a trailing full stop or bang ignored), in any session's highlight layer.</summary>
    private static bool GuideEchoes(string text, List<HighlightLayerView> layers)
    {
        var t = GuideEchoKey(text);
        if (t.IsEmpty) return false;
        foreach (var l in layers)
        {
            if (l.Boxes.Count == 0) continue;   // a layer whose targets are not on screen says nothing yet
            if (l.Title != null && t.Equals(GuideEchoKey(l.Title), StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var b in l.Boxes)
                if (b.Label != null && t.Equals(GuideEchoKey(b.Label), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static ReadOnlySpan<char> GuideEchoKey(string s) => s.AsSpan().Trim().TrimEnd(".!").TrimEnd();

    /// <summary>The toasts the stack shows now, newest first: young enough and of a kind that is on (a summary shows
    /// whatever its kind's setting: what it counts was filtered already).</summary>
    private bool GuideToastLive(GuideToast t, DateTime utc) =>
        (utc - t.At).TotalSeconds < GuideToastSec && (t.Summary || ToastShown(t.Kind));

    // ── What attention decided, for agents (guide.state "attention") ─

    /// <summary>The attention state last frame, published for guide.state (written on Render, read on the TCP thread;
    /// replaced only when it changes).</summary>
    internal sealed record GuideAttentionInfo(bool Combat, int Held, bool Quiet, bool Dot, bool BridgeDown, string? CombatBroken);
    private volatile GuideAttentionInfo _attentionInfo = new(false, 0, false, false, false, null);

    private void PublishAttention(in GuideAttention at)
    {
        var cur = _attentionInfo;
        if (cur.Combat == at.Combat && cur.Held == _toastHeld && cur.Quiet == at.Quiet && cur.Dot == at.ShowDot
            && cur.BridgeDown == at.BridgeDown && cur.CombatBroken == _combatBroken) return;
        _attentionInfo = new GuideAttentionInfo(at.Combat, _toastHeld, at.Quiet, at.ShowDot, at.BridgeDown, _combatBroken);
    }

    private JObject AttentionJson()
    {
        var a = _attentionInfo;
        return new JObject
        {
            ["combat"] = a.Combat, ["heldToasts"] = a.Held, ["quiet"] = a.Quiet, ["dot"] = a.Dot, ["bridgeDown"] = a.BridgeDown,
            ["combatBroken"] = a.CombatBroken,
        };
    }
}
