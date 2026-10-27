using DSPRE.Core;

namespace DSPRE.Tests;

public class MeshDecimatorTests
{
    private static (float[] Positions, float[] Uvs) Grid(int cells)
    {
        var positions = new List<float>();
        var uvs = new List<float>();
        void Vertex(int x, int z)
        {
            positions.AddRange([x, 0, z]);
            uvs.AddRange([x / 4f, z / 4f]);
        }

        for (int x = 0; x < cells; x++)
        {
            for (int z = 0; z < cells; z++)
            {
                Vertex(x, z); Vertex(x + 1, z); Vertex(x + 1, z + 1);
                Vertex(x, z); Vertex(x + 1, z + 1); Vertex(x, z + 1);
            }
        }

        return (positions.ToArray(), uvs.ToArray());
    }

    [Fact]
    public void AFlatEvenlyMappedPatchLosesNoAreaAndKeepsItsTextureMapping()
    {
        (float[] positions, float[] uvs) = Grid(8);
        (float[] reduced, float[] reducedUvs) = MeshDecimator.Reduce(positions, uvs, 20);

        Assert.True(reduced.Length / 9 < 128);
        double area = 0;
        for (int t = 0; t < reduced.Length / 9; t++)
        {
            int o = t * 9;
            area += Math.Abs((reduced[o + 3] - reduced[o]) * (reduced[o + 8] - reduced[o + 2]) - (reduced[o + 6] - reduced[o]) * (reduced[o + 5] - reduced[o + 2])) / 2;
            for (int c = 0; c < 3; c++)
            {
                Assert.Equal(reduced[o + c * 3] / 4f, reducedUvs[t * 6 + c * 2], 4);
                Assert.Equal(reduced[o + c * 3 + 2] / 4f, reducedUvs[t * 6 + c * 2 + 1], 4);
            }
        }

        Assert.Equal(64.0, area, 3);
    }
}
