using System.Numerics;

namespace WhatsAnAiBridge;

/// <summary>
/// PoE1 side of the ImGui API differences: the PoE1 HUD runs Hexa.NET.ImGui (Dear ImGui 1.93, the HUD's own build),
/// the PoE2 HUD still ImGui.NET 1.90. Shared code calls these instead of the differing members, so it has one path.
/// The PoE2 twin is Poe2/ImGuiShim.cs - keep the two member lists and signatures identical.
/// </summary>
internal static unsafe class ImGuiShim
{
    // ImDrawFlags_RoundCorners* are ImDrawFlags.Round* in this build.
    internal const ImDrawFlags RoundTopLeft = ImDrawFlags.RoundTopLeft;
    internal const ImDrawFlags RoundTopRight = ImDrawFlags.RoundTopRight;
    internal const ImDrawFlags RoundLeft = ImDrawFlags.RoundLeft;
    internal const ImDrawFlags RoundRight = ImDrawFlags.RoundRight;

    /// <summary>ImGuiChildFlags_Border was renamed ImGuiChildFlags_Borders in 1.91.</summary>
    internal const ImGuiChildFlags ChildBorders = ImGuiChildFlags.Borders;

    /// <summary>
    /// The 1.90 stroke: 1.93 draws closed shapes (AddRect, AddCircle, AddTriangle) inside their outline by default, so a
    /// thick frame shrinks into its rect, and strokes get round joins and anti-aliased ends. StrokeLegacy is the 1.90
    /// geometry exactly (centred on the half-pixel-inset outline, miter joins, no AA line ends: imgui_draw.cpp AddRect /
    /// AddCircle / _AddPolyline), which StrokeCenter is not (no half-pixel inset, AA ends). Every stroke helper below
    /// ORs it in unless the caller picked a stroke position itself. None on PoE2.
    /// </summary>
    internal const ImDrawFlags StrokeLegacy = ImDrawFlags.StrokeLegacy;

    private static ImDrawFlags Stroke(ImDrawFlags flags) => (flags & ImDrawFlags.StrokeMask) != 0 ? flags : flags | StrokeLegacy;

    /// <summary>AddRect in the 1.90 argument order (flags before thickness) and 1.90 stroke; Hexa takes thickness, then flags.</summary>
    internal static void AddRectEx(this ImDrawListPtr dl, Vector2 min, Vector2 max, uint col, float rounding, ImDrawFlags flags, float thickness) =>
        dl.AddRect(min, max, col, rounding, thickness, Stroke(flags));

    /// <summary>One-pixel AddRect with the 1.90 stroke.</summary>
    internal static void AddRectEx(this ImDrawListPtr dl, Vector2 min, Vector2 max, uint col, float rounding) =>
        dl.AddRect(min, max, col, rounding, 1f, StrokeLegacy);

    /// <summary>Circle outline with the 1.90 stroke (centred on radius - 0.5).</summary>
    internal static void AddCircleEx(this ImDrawListPtr dl, Vector2 center, float radius, uint col, int segments, float thickness) =>
        dl.AddCircle(center, radius, col, segments, thickness, StrokeLegacy);

    /// <summary>Triangle outline with the 1.90 stroke.</summary>
    internal static void AddTriangleEx(this ImDrawListPtr dl, Vector2 p1, Vector2 p2, Vector2 p3, uint col, float thickness) =>
        dl.AddTriangle(p1, p2, p3, col, thickness, StrokeLegacy);

    /// <summary>AddPolyline in the 1.90 argument order (flags before thickness) and 1.90 stroke.</summary>
    internal static void AddPolylineEx(this ImDrawListPtr dl, ref Vector2 first, int count, uint col, ImDrawFlags flags, float thickness) =>
        dl.AddPolyline(in first, count, col, thickness, Stroke(flags));

    /// <summary>PathStroke in the 1.90 argument order (flags before thickness) and 1.90 stroke.</summary>
    internal static void PathStrokeEx(this ImDrawListPtr dl, uint col, ImDrawFlags flags, float thickness) =>
        dl.PathStroke(col, thickness, Stroke(flags));

    /// <summary>Text in a given font and size (Hexa only has the ImFont* overload for a string).</summary>
    internal static void AddFontText(this ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint col, string? text) =>
        dl.AddText((ImFont*)font, size, pos, col, text!);

    /// <summary>Wrapped text in a given font and size.</summary>
    internal static void AddFontText(this ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint col, string? text, float wrapWidth) =>
        dl.AddText((ImFont*)font, size, pos, col, text!, wrapWidth);

    /// <summary>Advance of one glyph at <paramref name="size"/>: 1.92+ bakes per size, so ask the baked font at that size.</summary>
    /// The baked size can differ from the one asked for (font loader limits), so scale by what was baked.
    internal static float GlyphAdvance(ImFontPtr font, float size, char ch)
    {
        var baked = ImGui.GetFontBaked(font, size);
        if (baked.IsNull) return 0f;
        return baked.GetCharAdvance(ch) * (baked.Size > 0 ? size / baked.Size : 1f);
    }

    /// <summary>Screen X of the current window's content-region right edge (GetWindowContentRegionMax is gone since 1.92).</summary>
    internal static float ContentRegionMaxX() =>
        ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;

    /// <summary>Turns anti-aliased lines on for this draw list (ImDrawListFlags folded into ImDrawFlags in this build).</summary>
    internal static void EnsureAntiAliasedLines(ImDrawListPtr dl)
    {
        if ((dl.Flags & ImDrawFlags.AaLines) == 0) dl.Flags |= ImDrawFlags.AaLines;
    }

    /// <summary>Whether the backend lets this draw list offset vertices (more than 64k vertices per list).</summary>
    internal static bool CanOffsetVertices(ImDrawListPtr dl) => (dl.Flags & ImDrawFlags.UseVtxOffset) != 0;

    /// <summary>The draw list's next vertex index (ImDrawList._VtxCurrentIdx).</summary>
    internal static uint VtxCurrentIdx(ImDrawListPtr dl) => dl.VtxCurrentIdx;
}
