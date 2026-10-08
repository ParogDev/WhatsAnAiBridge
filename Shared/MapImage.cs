using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// map.image: the current area's terrain as a PNG, from the Radar plugin's PluginBridge method
/// "Radar.GetMapImage" (Radar must be loaded), with the player marked - so an AI agent can look at the map.
///
/// Radar's image is the area grid at 1 pixel per grid cell, same orientation as GridPos (pixel x,y = grid
/// x,y; verified on PoE2 against IngameData.RawPathfindingData: 70% of painted pixels lie on walkable
/// boundaries with this mapping vs 5% flipped). Radar paints walls/edges, not walkable floor.
/// Optional crop around the player (in grid cells) and a maximum output size keep it small.
/// Uses System.Drawing explicitly (the per-game "Color" alias differs between PoE1 and PoE2).
/// </summary>
public partial class WhatsAnAiBridge
{
    private string? ProcessMapMethod(string method, JToken? p)
    {
        if (method != "map.image") return null;
        return MapImage(
            p?["includeRoutes"]?.Value<bool>() ?? false,
            p?["markPlayer"]?.Value<bool>() ?? true,
            Math.Clamp(p?["cropRadius"]?.Value<int>() ?? 0, 0, 2000),
            Math.Clamp(p?["maxSize"]?.Value<int>() ?? 1024, 64, 2048)).ToString(Newtonsoft.Json.Formatting.None);
    }

    private JObject MapImage(bool includeRoutes, bool markPlayer, int cropRadius, int maxSize)
    {
        var getImage = GameController.PluginBridge.GetMethod<Func<bool, byte[]>>("Radar.GetMapImage");
        if (getImage == null)
            return new JObject { ["error"] = "radar_unavailable", ["message"] = "The Radar plugin isn't loaded (no PluginBridge method Radar.GetMapImage)." };
        byte[]? png;
        try { png = getImage(includeRoutes); }
        catch (Exception ex) { return new JObject { ["error"] = "radar_failed", ["message"] = ex.Message }; }
        if (png == null || png.Length == 0)
            return new JObject { ["error"] = "no_map", ["message"] = "Radar has no map for this area yet." };

        using var input = new MemoryStream(png);
        using var source = new System.Drawing.Bitmap(input);
        var player = GameController.Player?.GridPos;
        int px = player != null ? (int)player.Value.X : -1, py = player != null ? (int)player.Value.Y : -1;

        // Crop (grid cells around the player), then scale down to maxSize.
        // Default: trim to the painted terrain (areas are mostly empty margin), keeping the player inside.
        var crop = ContentBounds(source, px, py, margin: 24);
        if (cropRadius > 0 && px >= 0)
        {
            crop = System.Drawing.Rectangle.Intersect(crop,
                new System.Drawing.Rectangle(px - cropRadius, py - cropRadius, cropRadius * 2, cropRadius * 2));
            if (crop.Width <= 0 || crop.Height <= 0) crop = new System.Drawing.Rectangle(0, 0, source.Width, source.Height);
        }
        double scale = Math.Min(1.0, (double)maxSize / Math.Max(crop.Width, crop.Height));
        int outW = Math.Max(1, (int)(crop.Width * scale)), outH = Math.Max(1, (int)(crop.Height * scale));

        using var output = new System.Drawing.Bitmap(outW, outH);
        using (var g = System.Drawing.Graphics.FromImage(output))
        {
            // Dark background: Radar's image is transparent outside walls, which reads poorly on its own.
            g.Clear(System.Drawing.Color.FromArgb(255, 24, 24, 28));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(source, new System.Drawing.Rectangle(0, 0, outW, outH), crop, System.Drawing.GraphicsUnit.Pixel);
            if (markPlayer && px >= 0 && crop.Contains(px, py))
            {
                float mx = (float)((px - crop.X) * scale), my = (float)((py - crop.Y) * scale);
                float r = Math.Max(7f, outW / 90f);
                using var ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 255, 215, 0), Math.Max(2f, r / 2.5f));
                using var dot = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 255, 64, 64));
                g.FillEllipse(dot, mx - r / 2, my - r / 2, r, r);
                g.DrawEllipse(ring, mx - r * 1.4f, my - r * 1.4f, r * 2.8f, r * 2.8f);
            }
        }
        using var outStream = new MemoryStream();
        output.Save(outStream, System.Drawing.Imaging.ImageFormat.Png);

        return MapResult(outStream, outW, outH, px, py, crop, scale, source, includeRoutes);
    }

    /// <summary>Bounding box of non-transparent pixels (+margin, always containing the player), via LockBits.</summary>
    private static System.Drawing.Rectangle ContentBounds(System.Drawing.Bitmap bmp, int px, int py, int margin)
    {
        var full = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(full, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        try
        {
            var row = new byte[data.Stride];
            // Skip an 8-pixel frame: Radar draws a border line along the image edge (anti-aliased).
            for (int y = 8; y < bmp.Height - 8; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 8; x < bmp.Width - 8; x++)
                {
                    if (row[x * 4 + 3] == 0) continue; // alpha
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        if (maxX < 0) return full;
        if (px >= 0) { minX = Math.Min(minX, px); maxX = Math.Max(maxX, px); minY = Math.Min(minY, py); maxY = Math.Max(maxY, py); }
        return System.Drawing.Rectangle.Intersect(full,
            System.Drawing.Rectangle.FromLTRB(minX - margin, minY - margin, maxX + margin + 1, maxY + margin + 1));
    }

    private JObject MapResult(MemoryStream outStream, int outW, int outH, int px, int py,
        System.Drawing.Rectangle crop, double scale, System.Drawing.Bitmap source, bool includeRoutes)
    {
        return new JObject
        {
            ["area"] = GameController.Area?.CurrentArea?.Name,
            ["pngBase64"] = Convert.ToBase64String(outStream.ToArray()),
            ["width"] = outW,
            ["height"] = outH,
            ["playerGrid"] = px >= 0 ? new JArray(px, py) : null,
            // pixel = (grid - origin) * scale  <=>  grid = pixel / scale + origin
            ["mapping"] = new JObject { ["originGrid"] = new JArray(crop.X, crop.Y), ["scale"] = Math.Round(scale, 6), ["areaGrid"] = new JArray(source.Width, source.Height) },
            ["legend"] = "Light lines = walls/edges of walkable terrain (Radar). Red dot in a gold ring = player." +
                         (includeRoutes ? " Coloured lines = Radar's routes to its targets." : ""),
        };
    }
}
