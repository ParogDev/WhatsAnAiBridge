using System;
using System.Numerics;
using ImGuiNET;

namespace WhatsAnAiBridge;

/// <summary>
/// Draws the Render Lab frame (RenderLabTypes.cs) on the ImGui background draw list: a world overlay under every HUD
/// window, no input. Two elements, both built as a glow emulated with layered strokes (ImGui has no additive blend):
/// a wide low-alpha halo, a dark "ink" rim, a narrower mid halo over the rim, and a crisp bright core on top. The rim
/// darkens the busy ground under the light so the core and mid halo keep their contrast on bright or detailed
/// floors; the halos are what reads as light on dark ground.
///
/// Walls: a cool cyan contour hugging the wall base, at full strength within 400 world units of the player (it frames
/// the nearby space) and fading to nothing at the far end of the ray range (far = the farthest hit this frame, at
/// least 300 units past the full zone, smoothed over ~0.2 s so a flickering long ray does not pump the picture). The
/// core whitens with strength, so the near geometry reads as "lit by the player". Each run ends in a small bright
/// post, so a gap between two runs reads as an opening between two lit posts; posts fade with the contour, so a run
/// that merely ran out of range gets none. Where the frame gives a wall height, a vertical face is extruded above the
/// base as a per-vertex gradient quad (20% alpha at the base, 0 at the top) for a hint of volume. Nothing is filled
/// on the ground.
///
/// Path: a warm gold light trail from the player's feet to the target. It fades in over the first 60 world units so it
/// never sits under the character, and stops at the edge of the end marker. Comets travel along it toward the target
/// (a 70-unit faint tail, an 18-unit bright head ending in a small particle with its own halo, every 180 units at
/// 240 units/s, in world units so the speed looks the same at any fps or zoom); they tell direction without arrows.
/// The target gets a breathing ring with the same halo/rim/core build and a centre dot; on a re-plan one ring expands
/// from it. The label sits once above the ring in a small dark pill. A re-plan cross-fades: the previous frame's
/// ribbon is kept as a ghost and fades out over 0.18 s while the new shape fades in, so the path never pops or blinks.
///
/// Palette (fixed, this is a world overlay, not a themed panel): walls in cyan/teal (0.40, 0.88, 0.96 core over a
/// 0.12, 0.62, 0.78 halo), path in gold (1.0, 0.90, 0.62 core over a 1.0, 0.66, 0.22 amber halo). Cold for the
/// static geometry, warm for the one thing the player should follow; they are complementary, so neither hides the
/// other where they cross, and both stay far from the game's red/green/blue life, mana and loot colours.
///
/// Cost: a polyline with varying alpha is split into chunks of equal quantized alpha (12 levels), each one
/// AddPolyline per layer, so the work stays linear in the point count (4 vertices per point per layer in ImGui's AA
/// thick-line path) with at most 4 layers plus the comet strokes. The path is subdivided once (Catmull-Rom midpoints)
/// before stroking, so its planner points, ~12 world units apart, read as a curve. No per-frame allocations: every
/// scratch buffer is grow-only and reused. Game-agnostic: ImGui only, nothing here touches ExileCore*.
/// </summary>
public partial class WhatsAnAiBridge
{
    // ── Tuning ───────────────────────────────────────────────────────
    private const int RlLevels = 12;                       // alpha quantisation steps of a faded polyline
    private const float RlFadeInWorld = 60f;               // path: invisible under the feet, full by here (world units)
    private const float RlReplanSec = 0.18f;               // cross-fade between the old and the new path shape
    private const float RlPulseSpeed = 240f, RlPulseSpacing = 180f, RlPulseTail = 70f, RlPulseHead = 18f;   // world units (/s)
    private const int RlMaxPulses = 16;
    private const float RlRingR = 7f, RlRingSec = 0.6f;    // end marker radius; the one expanding ring on re-plan
    private const double RlBreathSec = 2.4;
    private const float RlWallFull = 400f;                 // walls at full strength within this (world units)...
    private const float RlWallFadeMin = 300f;              // ...then fade to nothing over at least this much further
    private const float RlFaceA = 0.20f, RlFaceMaxPx = 400f;
    private const float RlPad = 20f;                       // off-screen margin for culling (covers the widest halo)

    private static readonly Vector3 RlInk = new(0.02f, 0.04f, 0.05f);
    private static readonly Vector3 RlWallGlow = new(0.12f, 0.62f, 0.78f), RlWallCore = new(0.40f, 0.88f, 0.96f), RlWallNear = new(0.86f, 0.98f, 1.00f);
    private static readonly Vector3 RlPathGlow = new(1.00f, 0.66f, 0.22f), RlPathCore = new(1.00f, 0.90f, 0.62f), RlPulseCol = new(1.00f, 0.98f, 0.90f);
    private static readonly Vector3 RlPillFill = new(0.04f, 0.06f, 0.07f), RlPillText = new(0.95f, 0.92f, 0.84f);

    /// <summary>The four layers of one glowing line: outer halo, mid halo, dark rim, core (px widths, alpha factors).</summary>
    private readonly struct RlStyle
    {
        public readonly float OuterW, OuterA, MidW, MidA, InkA, CoreW, CoreA;
        public readonly Vector3 Glow, Core, Near;   // Near: the core colour at full strength (whitened)

        public RlStyle(float outerW, float outerA, float midW, float midA, float inkA, float coreW, float coreA, Vector3 glow, Vector3 core, Vector3 near)
        {
            OuterW = outerW; OuterA = outerA; MidW = midW; MidA = midA; InkA = inkA; CoreW = coreW; CoreA = coreA;
            Glow = glow; Core = core; Near = near;
        }
    }

    private static readonly RlStyle RlWallStyle = new(13f, 0.12f, 5.5f, 0.24f, 0.28f, 2.0f, 0.90f, RlWallGlow, RlWallCore, RlWallNear);
    private static readonly RlStyle RlPathStyle = new(18f, 0.13f, 8f, 0.26f, 0.35f, 2.6f, 0.95f, RlPathGlow, RlPathCore, RlPathCore);

    /// <summary>One ribbon in screen space: points, world distance along, alpha envelope. Grow-only buffers.</summary>
    private sealed class RlPathBuf
    {
        public Vector2[] Pts = new Vector2[256];
        public float[] Along = new float[256], Alpha = new float[256];
        public int Count;
    }

    /// <summary>Overlay-local state: scratch buffers, the path ghost, the smoothed wall fade reference.</summary>
    private sealed class RenderLabUi
    {
        public Vector2[] Buf = new Vector2[256];          // chunk / comet scratch
        public float[] Alpha = new float[256];            // wall per-point alpha scratch
        public RlPathBuf Cur = new(), Prev = new(), Ghost = new();
        public bool HadPath;
        public float LastChanged;
        public float Far = -1f;
        public double LastTime;
        public string? LabelSrc, LabelText;
        public string? LastError;
    }

    private readonly RenderLabUi _rl = new();

    partial void DrawRenderLabImpl(LabFrame f)
    {
        try
        {
            DrawRenderLabBody(f);
        }
        catch (Exception ex)
        {
            if (_rl.LastError != ex.Message)
            {
                _rl.LastError = ex.Message;
                LogError($"[RenderLab] {ex}");
            }
        }
    }

    // ── Frame ────────────────────────────────────────────────────────

    private void DrawRenderLabBody(LabFrame f)
    {
        var u = _rl;
        var dt = u.LastTime > 0 ? (float)Math.Clamp(f.Time - u.LastTime, 0, 0.1) : 0f;
        u.LastTime = f.Time;
        var dl = ImGui.GetBackgroundDrawList();
        if ((dl.Flags & ImDrawListFlags.AntiAliasedLines) == 0) dl.Flags |= ImDrawListFlags.AntiAliasedLines;
        var disp = ImGui.GetIO().DisplaySize;
        if (f.ShowWalls) RlDrawWalls(dl, f, dt, disp);
        if (f.ShowPath) RlDrawPath(dl, f, disp);
        else RlForgetPath();
    }

    // ── Walls ────────────────────────────────────────────────────────

    private void RlDrawWalls(ImDrawListPtr dl, LabFrame f, float dt, Vector2 disp)
    {
        var u = _rl;
        // Full strength within RlWallFull, then a fade to nothing at the farthest hit this frame (about the ray
        // range), smoothed so one flickering long ray does not pump the whole contour.
        var far = RlWallFull + RlWallFadeMin;
        foreach (var run in f.Walls)
        {
            var d = run.Distance;
            for (var i = 0; i < run.Count; i++) if (d[i] > far && float.IsFinite(d[i])) far = d[i];
        }
        u.Far = u.Far < 0 ? far : u.Far + (far - u.Far) * (1f - MathF.Exp(-dt * 5f));
        var inv = 1f / (u.Far - RlWallFull);
        var uvWhite = ImGui.GetFontTexUvWhitePixel();

        foreach (var run in f.Walls)
        {
            var pts = run.Points;
            var n = run.Count;
            if (n < 2 || run.Distance.Length < n || run.Height.Length < n) continue;
            RlEnsure(ref u.Alpha, n);
            var a = u.Alpha;
            for (var i = 0; i < n; i++) a[i] = 1f - RlSmooth((run.Distance[i] - RlWallFull) * inv);
            RlWallFaces(dl, run, a, n, disp, uvWhite);
            RlStrokeChunks(dl, pts, a, n, RlWallStyle, 1f, disp);
            RlPost(dl, pts[0], a[0]);
            RlPost(dl, pts[n - 1], a[n - 1]);
        }
    }

    /// <summary>
    /// The extruded face of a wall run: one quad per segment from the ground points up to the wall top, with the
    /// colour written per vertex (base alpha at the bottom, transparent at the top). Falls back to a flat quad at half
    /// alpha when the 16-bit index space is nearly full and the backend cannot offset vertices.
    /// </summary>
    private static void RlWallFaces(ImDrawListPtr dl, LabWallRun run, float[] a, int n, Vector2 disp, Vector2 uv)
    {
        var pts = run.Points;
        var h = run.Height;
        var canGrad = (dl.Flags & ImDrawListFlags.AllowVtxOffset) != 0;
        var top = RlCol(RlWallGlow, 0f);
        for (var i = 0; i < n - 1; i++)
        {
            var h0 = h[i];
            var h1 = h[i + 1];
            if (!(h0 > 0.5f) || !(h1 > 0.5f)) continue;      // also rejects NaN
            var p0 = pts[i];
            var p1 = pts[i + 1];
            if (!RlVisible(p0, p1, disp)) continue;
            var ab = RlFaceA * (a[i] + a[i + 1]) * 0.5f;
            if (ab < 0.012f) continue;
            var t0 = new Vector2(p0.X, p0.Y - MathF.Min(h0, RlFaceMaxPx));
            var t1 = new Vector2(p1.X, p1.Y - MathF.Min(h1, RlFaceMaxPx));
            var bottom = RlCol(RlWallGlow, ab);
            if (canGrad || dl._VtxCurrentIdx < 60000)
            {
                dl.PrimReserve(6, 4);
                var idx = dl._VtxCurrentIdx;                  // after the reserve: it may have started a new command
                dl.PrimWriteVtx(p0, uv, bottom);
                dl.PrimWriteVtx(p1, uv, bottom);
                dl.PrimWriteVtx(t1, uv, top);
                dl.PrimWriteVtx(t0, uv, top);
                dl.PrimWriteIdx((ushort)idx); dl.PrimWriteIdx((ushort)(idx + 1)); dl.PrimWriteIdx((ushort)(idx + 2));
                dl.PrimWriteIdx((ushort)idx); dl.PrimWriteIdx((ushort)(idx + 2)); dl.PrimWriteIdx((ushort)(idx + 3));
            }
            else dl.AddQuadFilled(p0, p1, t1, t0, RlCol(RlWallGlow, ab * 0.5f));
        }
    }

    /// <summary>The bright post at a run's end: a soft disc under a small core dot, both fading with the contour.</summary>
    private static void RlPost(ImDrawListPtr dl, Vector2 p, float a)
    {
        if (a < 0.03f || !float.IsFinite(p.X) || !float.IsFinite(p.Y)) return;
        dl.AddCircleFilled(p, 4.5f, RlCol(RlWallGlow, 0.18f * a), 12);
        dl.AddCircleFilled(p, 2f, RlCol(Vector3.Lerp(RlWallCore, RlWallNear, a), 0.9f * a), 10);
    }

    // ── Path ─────────────────────────────────────────────────────────

    private void RlDrawPath(ImDrawListPtr dl, LabFrame f, Vector2 disp)
    {
        var u = _rl;
        var p = f.Path;
        if (p == null || p.Count < 2 || p.Points.Length < p.Count || p.Along.Length < p.Count)
        {
            RlForgetPath();
            return;
        }
        // A re-plan resets Changed: the previous frame's ribbon becomes the ghost that fades out under the new shape.
        if (u.HadPath && p.Changed < u.LastChanged && u.Prev.Count >= 2) (u.Prev, u.Ghost) = (u.Ghost, u.Prev);
        else if (!u.HadPath) u.Ghost.Count = 0;
        u.HadPath = true;
        u.LastChanged = p.Changed;
        var k = RlSmooth(p.Changed / RlReplanSec);

        var target = p.Points[p.Count - 1];
        var tOk = float.IsFinite(target.X) && float.IsFinite(target.Y);
        var cur = u.Cur;
        RlBuildRibbon(p, cur, tOk ? target : null);
        if (u.Ghost.Count >= 2 && k < 1f) RlStrokeChunks(dl, u.Ghost.Pts, u.Ghost.Alpha, u.Ghost.Count, RlPathStyle, 1f - k, disp);
        if (cur.Count >= 2)
        {
            RlStrokeChunks(dl, cur.Pts, cur.Alpha, cur.Count, RlPathStyle, k, disp);
            RlComets(dl, cur, f.Time, k, disp);
        }
        if (tOk && RlOnScreen(target, disp, 40f))
        {
            RlEndMarker(dl, target, f.Time, p.Changed, k);
            RlLabel(dl, p.TargetLabel, target, disp, k);
        }
        (u.Cur, u.Prev) = (u.Prev, u.Cur);   // this frame's ribbon is next frame's "previous"
    }

    private void RlForgetPath()
    {
        var u = _rl;
        u.HadPath = false;
        u.Cur.Count = u.Prev.Count = u.Ghost.Count = 0;
    }

    /// <summary>
    /// Builds the ribbon buffer from the path: one Catmull-Rom midpoint per segment (the planner's points are ~12
    /// world units apart, so this halves the visible corner angle for twice the points), trimmed to stop at the edge
    /// of the end marker, with the feet fade-in as its alpha envelope. Count is 0 when the whole path fits inside
    /// the marker.
    /// </summary>
    private static void RlBuildRibbon(LabPath p, RlPathBuf b, Vector2? target)
    {
        var src = p.Points;
        var along = p.Along;
        var n = p.Count;
        RlEnsure(ref b.Pts, 2 * n + 1);
        RlEnsure(ref b.Along, 2 * n + 1);
        RlEnsure(ref b.Alpha, 2 * n + 1);
        var pts = b.Pts;
        var al = b.Along;
        var count = 0;
        for (var i = 0; i < n - 1; i++)
        {
            pts[count] = src[i];
            al[count++] = along[i];
            var p0 = src[Math.Max(i - 1, 0)];
            var p1 = src[i];
            var p2 = src[i + 1];
            var p3 = src[Math.Min(i + 2, n - 1)];
            // Catmull-Rom at t = 0.5: 9/16 (p1 + p2) - 1/16 (p0 + p3).
            pts[count] = (p1 + p2) * 0.5625f - (p0 + p3) * 0.0625f;
            al[count++] = (along[i] + along[i + 1]) * 0.5f;
        }
        pts[count] = src[n - 1];
        al[count++] = along[n - 1];

        if (target is Vector2 t)
        {
            var stop = RlRingR + 2f;
            var j = count - 2;
            while (j >= 0 && (pts[j] - t).Length() < stop) j--;
            if (j < 0)
            {
                b.Count = 0;
                return;
            }
            var d0 = (pts[j] - t).Length();
            var d1 = (pts[j + 1] - t).Length();
            var tt = d0 - d1 > 1e-3f ? Math.Clamp((d0 - stop) / (d0 - d1), 0f, 1f) : 0f;
            pts[j + 1] = Vector2.Lerp(pts[j], pts[j + 1], tt);
            al[j + 1] = al[j] + (al[j + 1] - al[j]) * tt;
            count = j + 2;
        }
        for (var i = 0; i < count; i++) b.Alpha[i] = RlSmooth(al[i] / RlFadeInWorld);
        b.Count = count;
    }

    /// <summary>
    /// The comets: every RlPulseSpacing world units a faint tail and a bright head slide toward the target at
    /// RlPulseSpeed. Ends are interpolated between path points so the motion is smooth, not stepped. A head past the
    /// end fades with its tail still on the path, so comets leave instead of vanishing.
    /// </summary>
    private void RlComets(ImDrawListPtr dl, RlPathBuf b, double time, float mul, Vector2 disp)
    {
        var end = b.Along[b.Count - 1];
        if (end < RlPulseHead * 2 || mul < 0.03f) return;
        RlEnsure(ref _rl.Buf, b.Count + 2);
        var buf = _rl.Buf;
        var phase = (float)(time * RlPulseSpeed % RlPulseSpacing);
        var hint = 0;
        for (var i = 0; i < RlMaxPulses; i++)
        {
            var s = phase + i * RlPulseSpacing;
            if (s - RlPulseTail > end) break;
            var env = RlSmooth(MathF.Min(s, end) / RlFadeInWorld) * mul;
            if (s > end) env *= MathF.Max(0f, 1f - (s - end) / RlPulseTail);
            if (env < 0.03f) continue;
            var h = hint;
            var n = RlSubPath(b, s - RlPulseTail, s, ref h, buf);
            hint = h;
            if (n >= 2 && RlVisible(buf[0], buf[n - 1], disp)) RlLine(dl, buf, n, RlPulseCol, 0.30f * env, 6.5f);
            n = RlSubPath(b, s - RlPulseHead, s, ref h, buf);
            if (n >= 2 && RlVisible(buf[0], buf[n - 1], disp))
            {
                RlLine(dl, buf, n, RlPathGlow, 0.28f * env, 10f);
                RlLine(dl, buf, n, RlPulseCol, 0.90f * env, 3.4f);
                // The head itself: a small bright particle with its own halo, so the comet reads even in a still.
                var head = buf[n - 1];
                dl.AddCircleFilled(head, 6f, RlCol(RlPathGlow, 0.25f * env), 14);
                dl.AddCircleFilled(head, 2.8f, RlCol(RlPulseCol, 0.95f * env), 12);
            }
        }
    }

    /// <summary>
    /// The piece of a ribbon between two world distances, into <paramref name="buf"/>: an interpolated start, the
    /// path points strictly inside, an interpolated end. <paramref name="hint"/> is the segment to start searching
    /// from and comes back as the start segment, so successive calls with increasing s0 stay linear overall.
    /// </summary>
    private static int RlSubPath(RlPathBuf b, float s0, float s1, ref int hint, Vector2[] buf)
    {
        var along = b.Along;
        var pts = b.Pts;
        var count = b.Count;
        if (s0 < along[0]) s0 = along[0];
        if (s1 > along[count - 1]) s1 = along[count - 1];
        if (s1 - s0 < 0.5f) return 0;
        var i = Math.Clamp(hint, 0, count - 2);
        while (i > 0 && along[i] > s0) i--;
        while (i < count - 2 && along[i + 1] <= s0) i++;
        hint = i;
        var n = 0;
        buf[n++] = RlLerpAt(pts, along, i, s0);
        var j = i + 1;
        while (j < count - 1 && along[j] < s1) buf[n++] = pts[j++];
        buf[n++] = RlLerpAt(pts, along, j - 1, s1);
        return n;
    }

    private static Vector2 RlLerpAt(Vector2[] pts, float[] along, int i, float s)
    {
        var d = along[i + 1] - along[i];
        var t = d > 1e-4f ? Math.Clamp((s - along[i]) / d, 0f, 1f) : 0f;
        return Vector2.Lerp(pts[i], pts[i + 1], t);
    }

    /// <summary>The target: halo ring, dark rim, breathing gold ring, centre dot; one expanding ring on a re-plan.</summary>
    private static void RlEndMarker(ImDrawListPtr dl, Vector2 c, double time, float changed, float mul)
    {
        var breath = 0.5f + 0.5f * MathF.Sin((float)(time * Math.PI * 2 / RlBreathSec));
        var r = RlRingR + 0.6f * breath;
        dl.AddCircle(c, r + 2.5f, RlCol(RlPathGlow, 0.14f * mul), 32, 3.5f);
        dl.AddCircle(c, r, RlCol(RlInk, 0.30f * mul), 32, 3.2f);
        dl.AddCircle(c, r, RlCol(RlPathCore, (0.78f + 0.2f * breath) * mul), 32, 1.6f);
        dl.AddCircleFilled(c, 2f, RlCol(RlPathCore, 0.9f * mul), 12);
        if (changed < RlRingSec)
        {
            var k = changed / RlRingSec;
            var e = 1f - (1f - k) * (1f - k) * (1f - k);
            dl.AddCircle(c, r + 18f * e, RlCol(RlPathCore, 0.5f * (1f - k)), 32, 1.5f);
        }
    }

    /// <summary>The target label once, in a small dark pill above the end marker, kept on screen. Long labels are cut.</summary>
    private void RlLabel(ImDrawListPtr dl, string label, Vector2 c, Vector2 disp, float mul)
    {
        if (string.IsNullOrEmpty(label) || mul < 0.03f) return;
        var u = _rl;
        if (label != u.LabelSrc)
        {
            u.LabelSrc = label;
            u.LabelText = label.Length > 36 ? label.Substring(0, 33) + "..." : label;
        }
        var text = u.LabelText!;
        var fs = ImGui.GetFontSize();
        var small = fs * 0.85f;
        var font = ImGui.GetFont();
        var tw = ImGui.CalcTextSize(text).X * (small / fs);
        var size = new Vector2(tw + 12f, small + 6f);
        var pos = new Vector2(c.X - size.X * 0.5f, c.Y - RlRingR - 10f - size.Y);
        pos = new Vector2(
            MathF.Round(Math.Clamp(pos.X, 2f, MathF.Max(2f, disp.X - size.X - 2f))),
            MathF.Round(Math.Clamp(pos.Y, 2f, MathF.Max(2f, disp.Y - size.Y - 2f))));
        dl.AddRectFilled(pos, pos + size, RlCol(RlPillFill, 0.80f * mul), size.Y * 0.5f);
        dl.AddRect(pos, pos + size, RlCol(RlPathGlow, 0.40f * mul), size.Y * 0.5f);
        dl.AddText(font, small, new Vector2(pos.X + 6f, pos.Y + 3f), RlCol(RlPillText, 0.95f * mul), text);
    }

    // ── Glowing polylines ────────────────────────────────────────────

    /// <summary>
    /// Strokes a polyline whose alpha varies per point: consecutive segments with the same quantized alpha (and on
    /// screen) are drawn as one chunk with all layers, chunks share their joint point so they tile seamlessly.
    /// Linear in the point count; a segment off screen or with a non-finite point is skipped.
    /// </summary>
    private void RlStrokeChunks(ImDrawListPtr dl, Vector2[] pts, float[] alpha, int count, in RlStyle st, float mul, Vector2 disp)
    {
        if (count < 2 || mul < 0.012f) return;
        RlEnsure(ref _rl.Buf, count);
        var buf = _rl.Buf;
        var i = 0;
        while (i < count - 1)
        {
            var lvl = RlLevel(alpha[i], alpha[i + 1], mul);
            if (lvl <= 0 || !RlVisible(pts[i], pts[i + 1], disp))
            {
                i++;
                continue;
            }
            var n = 0;
            buf[n++] = pts[i];
            var j = i;
            do
            {
                buf[n++] = pts[j + 1];
                j++;
            } while (j < count - 1 && RlLevel(alpha[j], alpha[j + 1], mul) == lvl && RlVisible(pts[j], pts[j + 1], disp));
            RlStrokeLayers(dl, buf, n, lvl / (float)RlLevels, st);
            i = j;
        }
    }

    /// <summary>
    /// Outer halo, dark rim, mid halo, core: the rim sits under the mid halo (a bit narrower than it), so it darkens
    /// the busy ground beneath the halo and core instead of dulling the light; the core whitens toward
    /// <see cref="RlStyle.Near"/> with strength.
    /// </summary>
    private static void RlStrokeLayers(ImDrawListPtr dl, Vector2[] buf, int n, float a, in RlStyle st)
    {
        RlLine(dl, buf, n, st.Glow, a * st.OuterA, st.OuterW);
        RlLine(dl, buf, n, RlInk, a * st.InkA, st.MidW * 0.8f);
        RlLine(dl, buf, n, st.Glow, a * st.MidA, st.MidW);
        RlLine(dl, buf, n, Vector3.Lerp(st.Core, st.Near, a), a * st.CoreA, st.CoreW);
    }

    private static void RlLine(ImDrawListPtr dl, Vector2[] buf, int n, Vector3 col, float alpha, float width)
    {
        if (alpha < 0.012f || n < 2) return;
        dl.AddPolyline(ref buf[0], n, RlCol(col, alpha), ImDrawFlags.None, width);
    }

    private static int RlLevel(float a0, float a1, float mul) =>
        Math.Clamp((int)MathF.Round((a0 + a1) * 0.5f * mul * RlLevels), 0, RlLevels);

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>False for a segment with a non-finite end or entirely beyond one screen edge (plus the halo margin).</summary>
    private static bool RlVisible(Vector2 a, Vector2 b, Vector2 disp)
    {
        if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(b.X) || !float.IsFinite(b.Y)) return false;
        if (a.X < -RlPad && b.X < -RlPad) return false;
        if (a.Y < -RlPad && b.Y < -RlPad) return false;
        if (a.X > disp.X + RlPad && b.X > disp.X + RlPad) return false;
        if (a.Y > disp.Y + RlPad && b.Y > disp.Y + RlPad) return false;
        return true;
    }

    private static bool RlOnScreen(Vector2 p, Vector2 disp, float pad) =>
        p.X > -pad && p.Y > -pad && p.X < disp.X + pad && p.Y < disp.Y + pad;

    private static float RlSmooth(float t) => t <= 0f ? 0f : t >= 1f ? 1f : t * t * (3f - 2f * t);

    /// <summary>ABGR packing without going through the style alpha (a panel's PushStyleVar(Alpha) must not dim the world).</summary>
    private static uint RlCol(Vector3 c, float a)
    {
        if (!(a > 0f)) return 0u;
        if (a > 1f) a = 1f;
        return ((uint)(a * 255f + 0.5f) << 24) | ((uint)(c.Z * 255f + 0.5f) << 16) | ((uint)(c.Y * 255f + 0.5f) << 8) | (uint)(c.X * 255f + 0.5f);
    }

    private static void RlEnsure<T>(ref T[] arr, int n)
    {
        if (arr.Length < n) Array.Resize(ref arr, Math.Max(n, arr.Length * 2));
    }
}
