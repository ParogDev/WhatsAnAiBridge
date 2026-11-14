using System.Numerics;

namespace WhatsAnAiBridge;

/// <summary>
/// PoE1 side of the ImGui API differences: the PoE1 HUD runs Hexa.NET.ImGui (Dear ImGui 1.92, the HUD's own build),
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

    /// <summary>AddRect in the 1.90 argument order (flags before thickness); Hexa takes thickness, then flags.</summary>
    internal static void AddRectEx(this ImDrawListPtr dl, Vector2 min, Vector2 max, uint col, float rounding, ImDrawFlags flags, float thickness) =>
        dl.AddRect(min, max, col, rounding, thickness, flags);

    /// <summary>AddPolyline in the 1.90 argument order (flags before thickness).</summary>
    internal static void AddPolylineEx(this ImDrawListPtr dl, ref Vector2 first, int count, uint col, ImDrawFlags flags, float thickness) =>
        dl.AddPolyline(in first, count, col, thickness, flags);

    /// <summary>PathStroke in the 1.90 argument order (flags before thickness).</summary>
    internal static void PathStrokeEx(this ImDrawListPtr dl, uint col, ImDrawFlags flags, float thickness) =>
        dl.PathStroke(col, thickness, flags);

    /// <summary>Text in a given font and size (Hexa only has the ImFont* overload for a string).</summary>
    internal static void AddFontText(this ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint col, string? text) =>
        dl.AddText((ImFont*)font, size, pos, col, text!);

    /// <summary>Wrapped text in a given font and size.</summary>
    internal static void AddFontText(this ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint col, string? text, float wrapWidth) =>
        dl.AddText((ImFont*)font, size, pos, col, text!, wrapWidth);

    /// <summary>Advance of one glyph at <paramref name="size"/>: 1.92 bakes per size, so ask the baked font at that size.</summary>
    /// The baked size can differ from the one asked for (font loader limits), so scale by what was baked.
    internal static float GlyphAdvance(ImFontPtr font, float size, char ch)
    {
        var baked = ImGui.GetFontBaked(font, size);
        if (baked.IsNull) return 0f;
        return baked.GetCharAdvance(ch) * (baked.Size > 0 ? size / baked.Size : 1f);
    }

    /// <summary>Screen X of the current window's content-region right edge (GetWindowContentRegionMax is gone in 1.92).</summary>
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
