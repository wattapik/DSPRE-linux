using DSPRE.Core;

namespace DSPRE.Tests;

/// <summary>A map filled with one tile merges into big polygons; their repeating texture coordinates must stay within what the DS can hold,
/// or the converter squeezes them and the texture looks stretched.</summary>
public sealed class MapCreatorMergerTests
{
    private static void WriteTexture(string path, int size)
    {
        // A TGA named .png: the decoder goes by the content.
        var file = new byte[18 + size * size * 4];
        file[2] = 2;
        file[12] = (byte)size;
        file[14] = (byte)size;
        file[16] = 32;
        file[17] = 0x28;
        for (int i = 18; i < file.Length; i += 4) (file[i], file[i + 1], file[i + 2], file[i + 3]) = (200, 100, 50, 255);
        File.WriteAllBytes(path, file);
    }

    private static MapCreatorTileset TilesetWith(int textureSize, float repeatsPerCell, out string folder)
    {
        folder = Path.Combine(Path.GetTempPath(), "mc-merger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        WriteTexture(Path.Combine(folder, "tex.png"), textureSize);
        var tileset = new MapCreatorTileset(folder);
        int material = tileset.MaterialForImage("tex.png");
        var tile = new MapTile { Name = "flat", Width = 1, Height = 1 };
        tile.Polygons.Add(new TilePolygon(material,
        [
            new TileVertex(0, 0, 0, 0, 0), new TileVertex(1, 0, 0, repeatsPerCell, 0),
            new TileVertex(1, 1, 0, repeatsPerCell, repeatsPerCell), new TileVertex(0, 1, 0, 0, repeatsPerCell)
        ]));
        tileset.Tiles.Add(tile);
        return tileset;
    }

    private static MapCreatorMap Filled()
    {
        var map = new MapCreatorMap();
        for (int c = 0; c < 32; c++) for (int r = 0; r < 32; r++) map.Tiles[0][c, r] = 0;
        return map;
    }

    private static float Area(TilePolygon polygon)
    {
        float sum = 0;
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            TileVertex a = polygon.Vertices[i], b = polygon.Vertices[(i + 1) % polygon.Vertices.Length];
            sum += a.X * b.Y - b.X * a.Y;
        }

        return MathF.Abs(sum) / 2;
    }

    [Fact]
    public void ATileRepeatingItsTextureManyTimesIsNotFusedPastTheLimit()
    {
        MapCreatorTileset tileset = TilesetWith(32, 8f, out string folder);
        try
        {
            MapCreatorComposer.Composition composition = MapCreatorComposer.Compose(tileset, Filled());
            Assert.True(composition.Polygons.Count > 1, "one polygon would repeat the texture 256 times");
            foreach (TilePolygon polygon in composition.Polygons)
            {
                float spanU = polygon.Vertices.Max(v => v.U) - polygon.Vertices.Min(v => v.U), spanV = polygon.Vertices.Max(v => v.V) - polygon.Vertices.Min(v => v.V);
                Assert.True((spanU + 1) * 32 <= 4060 && (spanV + 1) * 32 <= 4060, $"a polygon spans {spanU} x {spanV} repeats of a 32 pixel texture");
            }

            // All the ground is still covered: 32 x 32 cells.
            Assert.Equal(1024f, composition.Polygons.Sum(Area), 1);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AFillIsFusedButNeverRepeatsATextureMoreThanEightTimesOverOnePolygon()
    {
        MapCreatorTileset tileset = TilesetWith(16, 1f, out string folder);
        try
        {
            MapCreatorComposer.Composition composition = MapCreatorComposer.Compose(tileset, Filled());
            Assert.Equal(1024f, composition.Polygons.Sum(Area), 1);
            Assert.True(composition.Polygons.Count < 1024);
            Assert.All(composition.Polygons, p =>
            {
                Assert.True(p.Vertices.Max(v => v.U) - p.Vertices.Min(v => v.U) <= 8.001f);
                Assert.True(p.Vertices.Max(v => v.V) - p.Vertices.Min(v => v.V) <= 8.001f);
            });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WithoutTextureSizesTheMergerPutsNoLimitOnThem()
    {
        var polygons = new List<TilePolygon>();
        for (int c = 0; c < 32; c++)
        {
            polygons.Add(new TilePolygon(0,
            [
                new TileVertex(c, 0, 0, c * 8, 0), new TileVertex(c + 1, 0, 0, c * 8 + 8, 0), new TileVertex(c + 1, 1, 0, c * 8 + 8, 8), new TileVertex(c, 1, 0, c * 8, 8)
            ]));
        }

        Assert.Single(MapCreatorPolygonMerger.Merge(polygons));
        List<TilePolygon> limited = MapCreatorPolygonMerger.Merge(polygons, _ => (32, 32));
        Assert.True(limited.Count > 1);
        Assert.Equal(32f, limited.Sum(Area), 3);
    }

    [Fact]
    public void ARunOfATileableTileIsCutShortAtTheLimitToo()
    {
        MapCreatorTileset tileset = TilesetWith(64, 8f, out string folder);
        try
        {
            MapTile tile = tileset.Tiles[0];
            (tile.XTileable, tile.YTileable, tile.UTileable, tile.VTileable) = (true, true, true, true);
            MapCreatorComposer.Composition composition = MapCreatorComposer.Compose(tileset, Filled());
            foreach (TilePolygon polygon in composition.Polygons)
            {
                float spanU = polygon.Vertices.Max(v => v.U) - polygon.Vertices.Min(v => v.U), spanV = polygon.Vertices.Max(v => v.V) - polygon.Vertices.Min(v => v.V);
                Assert.True((spanU + 1) * 64 <= 4060 && (spanV + 1) * 64 <= 4060, $"a polygon spans {spanU} x {spanV} repeats of a 64 pixel texture");
            }

            Assert.Equal(1024f, composition.Polygons.Sum(Area), 1);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
