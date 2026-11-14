using System.Numerics;

namespace WhatsAnAiBridge;

/// <summary>
/// PoE2 side of the ImGui API differences: the PoE2 HUD (ExileCore2) runs ImGui.NET 1.90, the PoE1 HUD Hexa.NET.ImGui
/// (Dear ImGui 1.93). Shared code calls these instead of the differing members, so it has one path. These are the
/// 1.90 calls exactly as Shared code made them before the PoE1 migration.
/// The PoE1 twin is Poe1/ImGuiShim.cs - keep the two member lists and signatures identical.
/// </summary>
internal static class ImGuiShim
{
    internal const ImDrawFlags RoundTopLeft = ImDrawFlags.RoundCornersTopLeft;
    internal const ImDrawFlags RoundTopRight = ImDrawFlags.RoundCornersTopRight;
    internal const ImDrawFlags RoundLeft = ImDrawFlags.RoundCornersLeft;
    internal const ImDrawFlags RoundRight = ImDrawFlags.RoundCornersRight;

    internal const ImGuiChildFlags ChildBorders = ImGuiChildFlags.Border;

    /// <summary>1.90 strokes are already centred (the PoE1 shim asks 1.93 for this look); no flag here.</summary>
    internal const ImDrawFlags StrokeLegacy = ImDrawFlags.None;

    internal static void AddRectEx(this ImDrawListPtr dl, Vector2 min, Vector2 max, uint col, float rounding, ImDrawFlags flags, float thickness) =>
        dl.AddRect(min, max, col, rounding, flags, thickness);

    internal static void AddRectEx(this ImDrawListPtr dl, Vector2 min, Vector2 max, uint col, float rounding) =>
        dl.AddRect(min, max, col, rounding);

    internal static void AddCircleEx(this ImDrawListPtr dl, Vector2 center, float radius, uint col, int segments, float thickness) =>
        dl.AddCircle(center, radius, col, segments, thickness);

    internal static void AddTriangleEx(this ImDrawListPtr dl, Vector2 p1, Vector2 p2, Vector2 p3, uint col, float thickness) =>
        dl.AddTriangle(p1, p2, p3, col, thickness);

    internal static void AddPolylineEx(this ImDrawListPtr dl, ref Vector2 first, int count, uint col, ImDrawFlags flags, float thickness) =>
        dl.AddPolyline(ref first, count, col, flags, thickness);

    internal static void PathStrokeEx(this ImDrawListPtr dl, uint col, ImDrawFlags flags, float thickness) =>
        dl.PathStroke(col, flags, thickness);

    internal static void AddFontText(this ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint col, string? text) =>
        dl.AddText(font, size, pos, col, text!);

    /// <summary>Wrapped text in a given font and size.</summary>
    internal static void AddFontText(this ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint col, string? text, float wrapWidth) =>
        dl.AddText(font, size, pos, col, text!, wrapWidth);

    /// <summary>Advance of one glyph at <paramref name="size"/>: 1.90 has one atlas size, scaled linearly.</summary>
    internal static float GlyphAdvance(ImFontPtr font, float size, char ch) =>
        font.GetCharAdvance(ch) * (size / font.FontSize);

    internal static float ContentRegionMaxX() =>
        ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;

    internal static void EnsureAntiAliasedLines(ImDrawListPtr dl)
    {
        if ((dl.Flags & ImDrawListFlags.AntiAliasedLines) == 0) dl.Flags |= ImDrawListFlags.AntiAliasedLines;
    }

    internal static bool CanOffsetVertices(ImDrawListPtr dl) => (dl.Flags & ImDrawListFlags.AllowVtxOffset) != 0;

    internal static uint VtxCurrentIdx(ImDrawListPtr dl) => dl._VtxCurrentIdx;
}
