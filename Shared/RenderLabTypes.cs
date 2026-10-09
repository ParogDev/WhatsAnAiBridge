using System.Collections.Generic;
using System.Numerics;

namespace WhatsAnAiBridge;

// The Render Lab's contract between the data side (RenderLab.cs: fresh reads, time alignment, raycast walls, path
// planning, projection) and the drawing side (RenderLabDraw.cs). Everything here is already in screen pixels for the
// current frame; the drawer never reads game memory and never projects.

/// <summary>One contiguous run of wall contour found by the rays, in screen space, ordered around the player.</summary>
/// <param name="Points">Screen positions of consecutive ray hits on the same wall (at least 2).</param>
/// <param name="Distance">Per point: distance from the player in world units (for fading with distance).</param>
/// <param name="Height">Per point: how far above the ground the wall top is in screen pixels (0 = flat edge); lets the
/// drawer extrude a vertical face. The ground point is Points[i], the top is Points[i] - (0, Height[i]).</param>
internal sealed record LabWallRun(Vector2[] Points, float[] Distance, float[] Height);

/// <summary>The planned path from the player to the target, smoothed, in screen space.</summary>
/// <param name="Points">Screen positions from the player's feet to the target (at least 2), dense enough to draw as a
/// smooth curve (a point every ~8 px on screen).</param>
/// <param name="Along">Per point: world distance from the player along the path (0 at the player).</param>
/// <param name="Total">Total path length in world units.</param>
/// <param name="TargetLabel">What the path leads to ("Waypoint", "Area transition: X", an entity name).</param>
/// <param name="Changed">Seconds since the path last changed shape (a re-plan); 0 on the frame it changed.</param>
internal sealed record LabPath(Vector2[] Points, float[] Along, float Total, string TargetLabel, float Changed);

/// <summary>Everything the drawer needs for one frame.</summary>
internal sealed class LabFrame
{
    public readonly List<LabWallRun> Walls = new();
    public LabPath? Path;
    public Vector2 PlayerScreen;      // the player's feet
    public float PxPerWorld;          // screen pixels per world unit near the player (for world-sized widths)
    public double Time;               // seconds, monotonic (animation clock)
    public bool ShowWalls, ShowPath;
}
