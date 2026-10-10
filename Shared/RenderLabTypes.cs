using System.Collections.Generic;
using System.Numerics;

namespace WhatsAnAiBridge;

// The Render Lab's contract between the data side (RenderLab.cs: fresh reads, time alignment, raycast walls, path
// planning, projection) and the drawing side (RenderLabDraw.cs). Everything here is already in screen pixels for the
// current frame; the drawer never reads game memory and never projects.

/// <summary>One contiguous run of wall contour found by the rays, in screen space, ordered around the player.
/// Reused across frames (no garbage per frame): the arrays only grow, so read the first <see cref="Count"/> entries,
/// never Length.</summary>
internal sealed class LabWallRun
{
    /// <summary>Screen positions of consecutive ray hits on the same wall (at least 2).</summary>
    public Vector2[] Points = [];
    /// <summary>Per point: distance from the player in world units (for fading with distance).</summary>
    public float[] Distance = [];
    /// <summary>Per point: how far above the ground the wall top is in screen pixels (0 = flat edge); lets the drawer
    /// extrude a vertical face. The ground point is Points[i], the top is Points[i] - (0, Height[i]).</summary>
    public float[] Height = [];
    /// <summary>How many entries of the arrays belong to this frame.</summary>
    public int Count;
}

/// <summary>The planned path from the player to the target, smoothed, in screen space. One instance reused every
/// frame: the arrays only grow, so read the first <see cref="Count"/> entries, never Length.</summary>
internal sealed class LabPath
{
    /// <summary>Screen positions from the player's feet to the target (at least 2), dense enough to draw as a smooth
    /// curve (a point every ~8 px on screen).</summary>
    public Vector2[] Points = [];
    /// <summary>Per point: world distance from the player along the path (0 at the player).</summary>
    public float[] Along = [];
    /// <summary>How many entries of Points and Along belong to this frame.</summary>
    public int Count;
    /// <summary>Total path length in world units.</summary>
    public float Total;
    /// <summary>What the path leads to ("Waypoint", "Area transition: X", an entity name).</summary>
    public string TargetLabel = "";
    /// <summary>Seconds since the path last changed shape (a re-plan); 0 on the frame it changed.</summary>
    public float Changed;
}

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
