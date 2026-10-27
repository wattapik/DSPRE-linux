namespace DSPRE.Core;

/// <summary>Reduces a textured triangle mesh by quadric edge collapse. Vertices are welded by position
/// and texture coordinate, so a texture seam stays two vertices; a collapse keeps one end's position and
/// texture coordinate (never invents new ones), and its cost includes how far the texture coordinate of
/// the removed end moves, so the texture is not smeared for the sake of the shape. The first phase only
/// removes vertices inside a flat patch (not on its border or a texture seam) and charges what the move
/// does to the texture mapping of the triangles around it, so it loses nothing it can avoid; the second
/// phase, only entered when that is not enough, may also move border vertices.</summary>
public static class MeshDecimator
{
    private sealed class Vertex(double x, double y, double z, double u, double v)
    {
        public double X = x, Y = y, Z = z, U = u, V = v;
        public double[] Q = new double[10];
        public List<int> Triangles = [];
        public int Version;
        public bool Dead;
        public bool Locked;
    }

    /// <param name="positions">Three floats per vertex of each triangle, three vertices per triangle.</param>
    /// <param name="uvs">Two floats per vertex of each triangle.</param>
    public static (float[] Positions, float[] Uvs) Reduce(float[] positions, float[] uvs, int targetTriangles)
    {
        int triangleCount = positions.Length / 9;
        if (triangleCount <= targetTriangles || targetTriangles < 1) return (positions, uvs);

        var vertices = new List<Vertex>();
        var lookup = new Dictionary<(float, float, float, float, float), int>();
        int[][] triangles = new int[triangleCount][];
        bool[] alive = new bool[triangleCount];
        for (int t = 0; t < triangleCount; t++)
        {
            triangles[t] = new int[3];
            for (int c = 0; c < 3; c++)
            {
                int o = t * 9 + c * 3, w = t * 6 + c * 2;
                var key = (positions[o], positions[o + 1], positions[o + 2], uvs[w], uvs[w + 1]);
                if (!lookup.TryGetValue(key, out int index))
                {
                    lookup[key] = index = vertices.Count;
                    vertices.Add(new Vertex(key.Item1, key.Item2, key.Item3, key.Item4, key.Item5));
                }

                triangles[t][c] = index;
            }

            alive[t] = triangles[t][0] != triangles[t][1] && triangles[t][1] != triangles[t][2] && triangles[t][0] != triangles[t][2];
            if (!alive[t]) continue;
            double[] plane = Plane(vertices[triangles[t][0]], vertices[triangles[t][1]], vertices[triangles[t][2]], out _);
            foreach (int v in triangles[t])
            {
                vertices[v].Triangles.Add(t);
                Add(vertices[v].Q, plane);
            }
        }

        int liveTriangles = alive.Count(a => a);
        double diagonal = Diagonal(vertices);
        double uvWeight = diagonal * diagonal * 0.5;
        var queue = new PriorityQueue<(int Keep, int Remove, int KeepVersion, int RemoveVersion), double>();
        MarkBorders(vertices, triangles, alive);
        bool strict = true;

        void Push(int keep, int remove)
        {
            Vertex a = vertices[keep], b = vertices[remove];
            double[] q = new double[10];
            Add(q, a.Q);
            Add(q, b.Q);
            double cost = Error(q, a.X, a.Y, a.Z) + MappingError(vertices, triangles, alive, b, a, remove) * uvWeight;
            queue.Enqueue((keep, remove, a.Version, b.Version), cost);
        }

        void PushAll()
        {
            queue.Clear();
            for (int t = 0; t < triangleCount; t++)
            {
                if (!alive[t]) continue;
                for (int c = 0; c < 3; c++)
                {
                    int x = triangles[t][c], y = triangles[t][(c + 1) % 3];
                    Push(x, y);
                    Push(y, x);
                }
            }
        }

        PushAll();
        while (liveTriangles > targetTriangles)
        {
            if (!queue.TryDequeue(out var edge, out _))
            {
                if (!strict) break;
                strict = false;
                foreach (Vertex vertex in vertices) vertex.Locked = false;
                PushAll();
                continue;
            }

            Vertex keep = vertices[edge.Keep], remove = vertices[edge.Remove];
            if (keep.Dead || remove.Dead || keep.Version != edge.KeepVersion || remove.Version != edge.RemoveVersion) continue;
            if (remove.Locked) continue;
            if (Flips(vertices, triangles, alive, remove, edge.Keep, edge.Remove)) continue;

            foreach (int t in remove.Triangles.ToList())
            {
                if (!alive[t]) continue;
                int[] corners = triangles[t];
                for (int c = 0; c < 3; c++) if (corners[c] == edge.Remove) corners[c] = edge.Keep;
                if (corners[0] == corners[1] || corners[1] == corners[2] || corners[0] == corners[2])
                {
                    alive[t] = false;
                    liveTriangles--;
                }
                else keep.Triangles.Add(t);
            }

            remove.Dead = true;
            Add(keep.Q, remove.Q);
            keep.Version++;
            keep.Triangles = keep.Triangles.Where(t => alive[t]).Distinct().ToList();
            foreach (int t in keep.Triangles)
            {
                foreach (int other in triangles[t])
                {
                    if (other == edge.Keep) continue;
                    Push(edge.Keep, other);
                    Push(other, edge.Keep);
                }
            }
        }

        var outPositions = new List<float>();
        var outUvs = new List<float>();
        for (int t = 0; t < triangleCount; t++)
        {
            if (!alive[t]) continue;
            foreach (int v in triangles[t])
            {
                Vertex vertex = vertices[v];
                outPositions.AddRange([(float)vertex.X, (float)vertex.Y, (float)vertex.Z]);
                outUvs.AddRange([(float)vertex.U, (float)vertex.V]);
            }
        }

        return (outPositions.ToArray(), outUvs.ToArray());
    }

    private const double BorderWeight = 1000;

    private static void MarkBorders(List<Vertex> vertices, int[][] triangles, bool[] alive)
    {
        var edges = new Dictionary<(int, int), int>();
        for (int t = 0; t < triangles.Length; t++)
        {
            if (!alive[t]) continue;
            for (int c = 0; c < 3; c++)
            {
                int a = triangles[t][c], b = triangles[t][(c + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        for (int t = 0; t < triangles.Length; t++)
        {
            if (!alive[t]) continue;
            double[] n = Normal(triangles[t].Select(v => vertices[v]).ToArray());
            double nl = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (nl < 1e-12) continue;
            for (int c = 0; c < 3; c++)
            {
                int a = triangles[t][c], b = triangles[t][(c + 1) % 3];
                if (edges[a < b ? (a, b) : (b, a)] > 1) continue;
                Vertex va = vertices[a], vb = vertices[b];
                double ex = vb.X - va.X, ey = vb.Y - va.Y, ez = vb.Z - va.Z;
                double[] side = [n[1] * ez - n[2] * ey, n[2] * ex - n[0] * ez, n[0] * ey - n[1] * ex];
                double sl = Math.Sqrt(side[0] * side[0] + side[1] * side[1] + side[2] * side[2]);
                if (sl < 1e-12) continue;
                double sx = side[0] / sl, sy = side[1] / sl, sz = side[2] / sl, d = -(sx * va.X + sy * va.Y + sz * va.Z);
                double w = BorderWeight * (ex * ex + ey * ey + ez * ez);
                double[] q = [w * sx * sx, w * sx * sy, w * sx * sz, w * sx * d, w * sy * sy, w * sy * sz, w * sy * d, w * sz * sz, w * sz * d, w * d * d];
                Add(va.Q, q);
                Add(vb.Q, q);
                va.Locked = true;
                vb.Locked = true;
            }
        }

        var byPosition = new Dictionary<(double, double, double), int>();
        for (int i = 0; i < vertices.Count; i++)
        {
            Vertex v = vertices[i];
            var key = (v.X, v.Y, v.Z);
            if (byPosition.TryGetValue(key, out int other))
            {
                v.Locked = true;
                vertices[other].Locked = true;
            }
            else byPosition[key] = i;
        }
    }

    // How far the texture coordinate the triangles around `removed` would have at the kept vertex's position,
    // if they went on mapping the texture as they do, is from the kept vertex's own (the most of them, in
    // repeats squared): nothing for a collapse along a flat, evenly mapped patch.
    private static double MappingError(List<Vertex> vertices, int[][] triangles, bool[] alive, Vertex removed, Vertex kept, int removedIndex)
    {
        double worst = 0;
        foreach (int t in removed.Triangles)
        {
            if (!alive[t]) continue;
            int[] corners = triangles[t];
            if (corners.Any(v => vertices[v] == kept)) continue;
            Vertex a = vertices[corners[0]], b = vertices[corners[1]], c = vertices[corners[2]];
            double v0x = b.X - a.X, v0y = b.Y - a.Y, v0z = b.Z - a.Z, v1x = c.X - a.X, v1y = c.Y - a.Y, v1z = c.Z - a.Z;
            double v2x = kept.X - a.X, v2y = kept.Y - a.Y, v2z = kept.Z - a.Z;
            double d00 = v0x * v0x + v0y * v0y + v0z * v0z, d01 = v0x * v1x + v0y * v1y + v0z * v1z, d11 = v1x * v1x + v1y * v1y + v1z * v1z;
            double d20 = v2x * v0x + v2y * v0y + v2z * v0z, d21 = v2x * v1x + v2y * v1y + v2z * v1z;
            double denominator = d00 * d11 - d01 * d01;
            if (Math.Abs(denominator) < 1e-12) continue;
            double beta = (d11 * d20 - d01 * d21) / denominator, gamma = (d00 * d21 - d01 * d20) / denominator, alpha = 1 - beta - gamma;
            double du = alpha * a.U + beta * b.U + gamma * c.U - kept.U, dv = alpha * a.V + beta * b.V + gamma * c.V - kept.V;
            worst = Math.Max(worst, du * du + dv * dv);
        }

        return worst;
    }

    // A collapse is refused when it would turn any surrounding triangle over or squash it flat.
    private static bool Flips(List<Vertex> vertices, int[][] triangles, bool[] alive, Vertex remove, int keep, int removeIndex)
    {
        Vertex target = vertices[keep];
        foreach (int t in remove.Triangles)
        {
            if (!alive[t] || triangles[t].Contains(keep)) continue;
            Vertex[] before = triangles[t].Select(v => vertices[v]).ToArray();
            Vertex[] after = triangles[t].Select(v => v == removeIndex ? target : vertices[v]).ToArray();
            double[] a = Normal(before), b = Normal(after);
            double lengthB = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);
            double lengthA = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
            if (lengthA < 1e-12) continue;
            if (lengthB < 1e-12 || (a[0] * b[0] + a[1] * b[1] + a[2] * b[2]) / (lengthA * lengthB) < 0.2) return true;
        }

        return false;
    }

    private static double[] Normal(Vertex[] t)
    {
        double ax = t[1].X - t[0].X, ay = t[1].Y - t[0].Y, az = t[1].Z - t[0].Z;
        double bx = t[2].X - t[0].X, by = t[2].Y - t[0].Y, bz = t[2].Z - t[0].Z;
        return [ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx];
    }

    private static double[] Plane(Vertex a, Vertex b, Vertex c, out double area)
    {
        double[] n = Normal([a, b, c]);
        double length = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        area = length / 2;
        if (length < 1e-12) return new double[10];
        double nx = n[0] / length, ny = n[1] / length, nz = n[2] / length, d = -(nx * a.X + ny * a.Y + nz * a.Z);
        double w = area;
        return [w * nx * nx, w * nx * ny, w * nx * nz, w * nx * d, w * ny * ny, w * ny * nz, w * ny * d, w * nz * nz, w * nz * d, w * d * d];
    }

    private static void Add(double[] into, double[] other)
    {
        for (int i = 0; i < 10; i++) into[i] += other[i];
    }

    private static double Error(double[] q, double x, double y, double z) =>
        q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x + q[4] * y * y + 2 * q[5] * y * z + 2 * q[6] * y + q[7] * z * z + 2 * q[8] * z + q[9];

    private static double Diagonal(List<Vertex> vertices)
    {
        double dx = vertices.Max(v => v.X) - vertices.Min(v => v.X);
        double dy = vertices.Max(v => v.Y) - vertices.Min(v => v.Y);
        double dz = vertices.Max(v => v.Z) - vertices.Min(v => v.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
