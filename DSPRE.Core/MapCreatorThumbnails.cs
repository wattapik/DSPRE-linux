namespace DSPRE.Core;

/// <summary>Draws a tile as seen from straight above, with its textures, for the tile palette and the grid.</summary>
public static class MapCreatorThumbnails
{
    /// <returns>RGBA pixels of a (Width x cellPixels) by (Height x cellPixels) image, north at the top; pixels
    /// the tile does not cover are transparent.</returns>
    public static TextureImage Render(MapCreatorTileset tileset, MapTile tile, int cellPixels)
    {
        int width = Math.Max(tile.Width, 1) * cellPixels, height = Math.Max(tile.Height, 1) * cellPixels;
        var pixels = new byte[width * height * 4];
        var depth = new float[width * height];
        Array.Fill(depth, float.NegativeInfinity);

        var images = new Dictionary<int, TextureImage?>();
        foreach (TilePolygon polygon in tile.Polygons)
        {
            if (!images.TryGetValue(polygon.Material, out TextureImage? image)) images[polygon.Material] = image = tileset.GetImage(polygon.Material);
            TileVertex[] v = polygon.Vertices;
            Triangle(pixels, depth, width, height, cellPixels, image, v[0], v[1], v[2]);
            if (polygon.IsQuad) Triangle(pixels, depth, width, height, cellPixels, image, v[0], v[2], v[3]);
        }

        return new TextureImage(width, height, pixels);
    }

    private static void Triangle(byte[] pixels, float[] depth, int width, int height, int cellPixels, TextureImage? image, TileVertex a, TileVertex b, TileVertex c)
    {
        // Screen space: x grows right, y grows down; one tile unit is cellPixels pixels.
        (float X, float Y) Screen(TileVertex v) => (v.X * cellPixels, height - v.Y * cellPixels);
        (float ax, float ay) = Screen(a);
        (float bx, float by) = Screen(b);
        (float cx, float cy) = Screen(c);
        float area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
        if (MathF.Abs(area) < 1e-6f) return;

        int minX = Math.Max((int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))), 0), maxX = Math.Min((int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))), width - 1);
        int minY = Math.Max((int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))), 0), maxY = Math.Min((int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))), height - 1);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = ((bx - px) * (cy - py) - (cx - px) * (by - py)) / area;
                float w1 = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) / area;
                float w2 = 1f - w0 - w1;
                if (w0 < -0.001f || w1 < -0.001f || w2 < -0.001f) continue;

                float z = a.Z * w0 + b.Z * w1 + c.Z * w2;
                int index = y * width + x;
                if (z < depth[index]) continue;

                float r = 0.6f, g = 0.6f, bl = 0.6f, alpha = 1f;
                if (image is not null)
                {
                    float u = a.U * w0 + b.U * w1 + c.U * w2, v = a.V * w0 + b.V * w1 + c.V * w2;
                    int tx = (int)MathF.Floor((u - MathF.Floor(u)) * image.Width) % image.Width;
                    int ty = (int)MathF.Floor((1f - (v - MathF.Floor(v))) * image.Height) % image.Height;
                    int texel = (Math.Clamp(ty, 0, image.Height - 1) * image.Width + Math.Clamp(tx, 0, image.Width - 1)) * 4;
                    r = image.Rgba[texel] / 255f;
                    g = image.Rgba[texel + 1] / 255f;
                    bl = image.Rgba[texel + 2] / 255f;
                    alpha = image.Rgba[texel + 3] / 255f;
                }

                if (alpha < 0.5f) continue;
                depth[index] = z;
                pixels[index * 4] = (byte)(r * 255f);
                pixels[index * 4 + 1] = (byte)(g * 255f);
                pixels[index * 4 + 2] = (byte)(bl * 255f);
                pixels[index * 4 + 3] = 255;
            }
        }
    }
}
