namespace DSPRE.Core;

/// <summary>Joins the polygons of a composed map back into as few as it takes to draw the same thing. Cutting a map into cells
/// splits every large polygon at the cell borders; the pieces lie in one plane, share their texture mapping and meet edge to
/// edge, so they can be fused again, without changing what is drawn, until the original polygon is back. The game's models only
/// hold triangles and quads, so a fused polygon with more corners is split again.</summary>
public static class MapCreatorPolygonMerger
{
    private const float PositionTolerance = 2e-3f;
    private const float UvTolerance = 2e-3f;
    private const float KeyStep = 512f;

    /// <summary>The converter keeps a polygon's texture coordinates within this many texels of their middle (the DS stores them in 16 bits),
    /// squeezing any that reach further, which shows as a stretched texture. So a polygon may not be fused so large that its coordinates
    /// run further than that: one more repeat of the texture is allowed for for how the middle is chosen.</summary>
    private const float UvLimitTexels = 4060f;

    // The DS interpolates texture coordinates coarsely across big polygons, so a texture repeated many times over one polygon swims; Studio stops at 8.
    private const float MaxRepeats = 8f;

    /// <summary>Whether coordinates spanning this many repeats of a texture of this size stay within what the converter keeps.</summary>
    public static bool FitsRepeatLimit(int textureWidth, int textureHeight, float spanU, float spanV) =>
        textureWidth <= 0 || textureHeight <= 0 || (spanU <= MaxRepeats && spanV <= MaxRepeats && (spanU + 1f) * textureWidth <= UvLimitTexels && (spanV + 1f) * textureHeight <= UvLimitTexels);

    [ThreadStatic] private static Func<int, (int Width, int Height)>? textureSize;

    private static bool FitsUvLimit(int material, float spanU, float spanV)
    {
        (int width, int height) = textureSize?.Invoke(material) ?? (0, 0);
        return FitsRepeatLimit(width, height, spanU, spanV);
    }

    private static bool FitsUvLimit(int material, IEnumerable<TileVertex> vertices)
    {
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        foreach (TileVertex vertex in vertices)
        {
            minU = MathF.Min(minU, vertex.U);
            maxU = MathF.Max(maxU, vertex.U);
            minV = MathF.Min(minV, vertex.V);
            maxV = MathF.Max(maxV, vertex.V);
        }

        return FitsUvLimit(material, maxU - minU, maxV - minV);
    }

    private readonly record struct Key(long X, long Y, long Z);

    private sealed class Piece(int material, List<TileVertex> vertices)
    {
        public int Material = material;
        public List<TileVertex> Vertices = vertices;
        public bool Alive = true;
    }

    /// <param name="textureSize">The size each material's texture will have, to keep fused polygons within what the DS can hold of its
    /// repeating coordinates; null puts no limit on them.</param>
    public static List<TilePolygon> Merge(List<TilePolygon> polygons, Func<int, (int Width, int Height)>? textureSize = null)
    {
        MapCreatorPolygonMerger.textureSize = textureSize;
        try
        {
            return MergeLimited(polygons);
        }
        finally
        {
            MapCreatorPolygonMerger.textureSize = null;
        }
    }

    private static List<TilePolygon> MergeLimited(List<TilePolygon> polygons)
    {
        polygons = FuseConvexRegions(polygons);
        // Which pieces are fused first decides which can be fused at all, so a few orders are tried and the best kept.
        List<TilePolygon>? best = null;
        int bestCount = int.MaxValue;
        foreach (int order in new[] { 0, 1, 2, 3 })
        {
            List<TilePolygon> attempt = MergeOnce(polygons, order);
            int count = attempt.Sum(polygon => polygon.IsQuad ? 2 : 1);
            if (count < bestCount)
            {
                best = attempt;
                bestCount = count;
            }
        }

        return best!;
    }

    private static List<TilePolygon> MergeOnce(List<TilePolygon> polygons, int order)
    {
        var pieces = polygons.Select(polygon => new Piece(polygon.Material, [.. polygon.Vertices])).ToList();
        var edges = new Dictionary<(Key, Key), int>();
        for (int i = 0; i < pieces.Count; i++) AddEdges(edges, pieces[i], i);

        IEnumerable<int> sequence = order switch
        {
            0 => Enumerable.Range(0, pieces.Count),
            1 => Enumerable.Range(0, pieces.Count).Reverse(),
            2 => Enumerable.Range(0, pieces.Count).OrderBy(i => pieces[i].Vertices.Count).ThenBy(i => i),
            _ => Enumerable.Range(0, pieces.Count).OrderBy(i => (i * 2654435761L) % 1000003)
        };
        var pending = new Stack<int>(sequence.Reverse());
        while (pending.Count > 0)
        {
            int id = pending.Pop();
            Piece piece = pieces[id];
            if (!piece.Alive) continue;

            for (int i = 0; i < piece.Vertices.Count; i++)
            {
                TileVertex a = piece.Vertices[i], b = piece.Vertices[(i + 1) % piece.Vertices.Count];
                if (!edges.TryGetValue((KeyOf(b), KeyOf(a)), out int otherId) || otherId == id) continue;
                Piece other = pieces[otherId];
                if (!other.Alive || other.Material != piece.Material) continue;
                if (TryJoin(piece, other, i) is not { } joined) continue;

                RemoveEdges(edges, piece, id);
                RemoveEdges(edges, other, otherId);
                other.Alive = false;
                piece.Vertices = joined;
                AddEdges(edges, piece, id);
                pending.Push(id);
                break;
            }
        }

        var result = new List<TilePolygon>();
        foreach (Piece piece in pieces.Where(piece => piece.Alive))
        {
            List<TileVertex> v = piece.Vertices;
            RemoveStraightCorners(v);
            if (v.Count <= 4 && IsConvex(v))
            {
                result.Add(new TilePolygon(piece.Material, [.. v]));
                continue;
            }

            foreach ((int x, int y, int z) in Triangulate(v)) result.Add(new TilePolygon(piece.Material, [v[x], v[y], v[z]]));
        }

        return result;
    }

    private static (float X, float Y) Flatten(TileVertex vertex, int dropped) => dropped switch
    {
        0 => (vertex.Y, vertex.Z),
        1 => (vertex.X, vertex.Z),
        _ => (vertex.X, vertex.Y)
    };

    private static (int Dropped, float Sign) Projection(List<TileVertex> v)
    {
        float nx = 0f, ny = 0f, nz = 0f;
        for (int i = 0; i < v.Count; i++)
        {
            TileVertex c = v[i], d = v[(i + 1) % v.Count];
            nx += (c.Y - d.Y) * (c.Z + d.Z);
            ny += (c.Z - d.Z) * (c.X + d.X);
            nz += (c.X - d.X) * (c.Y + d.Y);
        }

        int dropped = MathF.Abs(nx) >= MathF.Abs(ny) && MathF.Abs(nx) >= MathF.Abs(nz) ? 0 : MathF.Abs(ny) >= MathF.Abs(nz) ? 1 : 2;
        // Dropping y flips the handedness of the projected polygon relative to the others.
        float component = dropped == 0 ? nx : dropped == 1 ? -ny : nz;
        return (dropped, component >= 0f ? 1f : -1f);
    }

    private static bool IsConvex(List<TileVertex> v)
    {
        (int dropped, float sign) = Projection(v);
        for (int i = 0; i < v.Count; i++)
        {
            (float ax, float ay) = Flatten(v[(i + v.Count - 1) % v.Count], dropped);
            (float bx, float by) = Flatten(v[i], dropped);
            (float cx, float cy) = Flatten(v[(i + 1) % v.Count], dropped);
            if (((bx - ax) * (cy - by) - (by - ay) * (cx - bx)) * sign < -1e-7f) return false;
        }

        return true;
    }

    /// <summary>Ear clipping, in the plane the polygon lies in; a convex polygon comes out as a fan.</summary>
    private static List<(int, int, int)> Triangulate(List<TileVertex> v)
    {
        var triangles = new List<(int, int, int)>();
        (int dropped, float sign) = Projection(v);
        var points = v.Select(vertex => Flatten(vertex, dropped)).ToList();
        var remaining = Enumerable.Range(0, v.Count).ToList();
        float Cross((float X, float Y) a, (float X, float Y) b, (float X, float Y) c) => ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X)) * sign;

        int guard = 0;
        while (remaining.Count > 3 && guard++ < v.Count * v.Count)
        {
            bool clipped = false;
            for (int k = 0; k < remaining.Count; k++)
            {
                int ia = remaining[(k + remaining.Count - 1) % remaining.Count], ib = remaining[k], ic = remaining[(k + 1) % remaining.Count];
                if (Cross(points[ia], points[ib], points[ic]) <= 1e-9f) continue;
                bool blocked = false;
                foreach (int other in remaining)
                {
                    if (other == ia || other == ib || other == ic) continue;
                    if (Cross(points[ia], points[ib], points[other]) >= -1e-9f && Cross(points[ib], points[ic], points[other]) >= -1e-9f && Cross(points[ic], points[ia], points[other]) >= -1e-9f)
                    {
                        blocked = true;
                        break;
                    }
                }

                if (blocked) continue;
                triangles.Add((ia, ib, ic));
                remaining.RemoveAt(k);
                clipped = true;
                break;
            }

            if (!clipped) break;
        }

        // A polygon too degenerate to clip further is closed off as a fan rather than lost.
        for (int k = 1; k < remaining.Count - 1; k++) triangles.Add((remaining[0], remaining[k], remaining[k + 1]));
        return triangles;
    }

    /// <summary>Finds each group of pieces that meet edge to edge, lie in one plane and share one texture mapping, and whose
    /// union is convex (a large polygon that was cut up): such a group is replaced by that one polygon, whatever order its
    /// pieces would have been fused in.</summary>
    private static List<TilePolygon> FuseConvexRegions(List<TilePolygon> polygons)
    {
        var edges = new Dictionary<(Key, Key), List<int>>();
        for (int i = 0; i < polygons.Count; i++)
        {
            TileVertex[] v = polygons[i].Vertices;
            for (int k = 0; k < v.Length; k++)
            {
                var edge = (KeyOf(v[k]), KeyOf(v[(k + 1) % v.Length]));
                if (!edges.TryGetValue(edge, out List<int>? list)) edges[edge] = list = [];
                list.Add(i);
            }
        }

        // Texture offsets, in whole repeats, that bring each piece into its component's frame.
        var shift = new (float U, float V)[polygons.Count];
        var component = Enumerable.Repeat(-1, polygons.Count).ToArray();
        var members = new List<List<int>>();
        for (int start = 0; start < polygons.Count; start++)
        {
            if (component[start] >= 0) continue;
            var group = new List<int> { start };
            component[start] = members.Count;
            float groupMinU = polygons[start].Vertices.Min(corner => corner.U), groupMaxU = polygons[start].Vertices.Max(corner => corner.U);
            float groupMinV = polygons[start].Vertices.Min(corner => corner.V), groupMaxV = polygons[start].Vertices.Max(corner => corner.V);
            for (int g = 0; g < group.Count; g++)
            {
                int i = group[g];
                TileVertex[] v = polygons[i].Vertices;
                for (int k = 0; k < v.Length; k++)
                {
                    TileVertex a = v[k], b = v[(k + 1) % v.Length];
                    if (!edges.TryGetValue((KeyOf(b), KeyOf(a)), out List<int>? neighbours)) continue;
                    foreach (int j in neighbours)
                    {
                        if (component[j] >= 0 || polygons[j].Material != polygons[i].Material) continue;
                        TileVertex[] w = polygons[j].Vertices;
                        int at = Array.FindIndex(w, corner => KeyOf(corner) == KeyOf(b));
                        if (at < 0) continue;
                        TileVertex nb = w[at], na = w[(at + 1) % w.Length];
                        float du = MathF.Round(b.U + shift[i].U - nb.U), dv = MathF.Round(b.V + shift[i].V - nb.V);
                        if (Math.Abs(nb.U + du - b.U - shift[i].U) > UvTolerance || Math.Abs(nb.V + dv - b.V - shift[i].V) > UvTolerance
                            || Math.Abs(na.U + du - a.U - shift[i].U) > UvTolerance || Math.Abs(na.V + dv - a.V - shift[i].V) > UvTolerance) continue;

                        // Joining must keep one plane and one texture mapping.
                        List<TileVertex> both = [.. v.Select(c => c with { U = c.U + shift[i].U, V = c.V + shift[i].V }), .. w.Select(c => c with { U = c.U + du, V = c.V + dv })];
                        if (!IsFlatAndEvenlyTextured(both)) continue;

                        // The fused polygon's texture coordinates must stay within what the DS holds, or the texture is squeezed.
                        float newMinU = MathF.Min(groupMinU, w.Min(corner => corner.U) + du), newMaxU = MathF.Max(groupMaxU, w.Max(corner => corner.U) + du);
                        float newMinV = MathF.Min(groupMinV, w.Min(corner => corner.V) + dv), newMaxV = MathF.Max(groupMaxV, w.Max(corner => corner.V) + dv);
                        if (!FitsUvLimit(polygons[i].Material, newMaxU - newMinU, newMaxV - newMinV)) continue;
                        (groupMinU, groupMaxU, groupMinV, groupMaxV) = (newMinU, newMaxU, newMinV, newMaxV);
                        shift[j] = (du, dv);
                        component[j] = members.Count;
                        group.Add(j);
                    }
                }
            }

            members.Add(group);
        }

        var result = new List<TilePolygon>(polygons.Count);
        foreach (List<int> group in members)
        {
            if (group.Count > 1 && ConvexUnion(polygons, group, shift) is { } fused)
            {
                result.Add(fused);
                continue;
            }

            foreach (int i in group) result.Add(polygons[i]);
        }

        return result;
    }

    private static TilePolygon? ConvexUnion(List<TilePolygon> polygons, List<int> group, (float U, float V)[] shift)
    {
        var corners = new Dictionary<Key, TileVertex>();
        foreach (int i in group)
        {
            foreach (TileVertex corner in polygons[i].Vertices) corners.TryAdd(KeyOf(corner), corner with { U = corner.U + shift[i].U, V = corner.V + shift[i].V });
        }

        List<TileVertex> all = [.. corners.Values];
        if (all.Count < 3) return null;
        (int dropped, _) = Projection(all.Count >= 3 ? polygons[group[0]].Vertices.ToList() : all);
        var points = all.Select(vertex => (Vertex: vertex, Flat: Flatten(vertex, dropped))).OrderBy(point => point.Flat.X).ThenBy(point => point.Flat.Y).ToList();

        static float Cross((float X, float Y) o, (float X, float Y) a, (float X, float Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(TileVertex Vertex, (float X, float Y) Flat)>();
        foreach (var point in points)
        {
            while (hull.Count >= 2 && Cross(hull[^2].Flat, hull[^1].Flat, point.Flat) <= 1e-7f) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }

        int lower = hull.Count + 1;
        for (int i = points.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[^2].Flat, hull[^1].Flat, points[i].Flat) <= 1e-7f) hull.RemoveAt(hull.Count - 1);
            hull.Add(points[i]);
        }

        hull.RemoveAt(hull.Count - 1);
        if (hull.Count < 3) return null;

        float hullArea = 0f;
        for (int i = 0; i < hull.Count; i++)
        {
            (float ax, float ay) = hull[i].Flat;
            (float bx, float by) = hull[(i + 1) % hull.Count].Flat;
            hullArea += ax * by - bx * ay;
        }

        float piecesArea = 0f;
        foreach (int i in group)
        {
            var flat = polygons[i].Vertices.Select(vertex => Flatten(vertex, dropped)).ToArray();
            for (int k = 0; k < flat.Length; k++) piecesArea += flat[k].X * flat[(k + 1) % flat.Length].Y - flat[(k + 1) % flat.Length].X * flat[k].Y;
        }

        // Same area means the pieces tile the hull without gaps or overlaps.
        if (MathF.Abs(MathF.Abs(hullArea) - MathF.Abs(piecesArea)) > 1e-3f * MathF.Max(MathF.Abs(hullArea), 1e-3f)) return null;
        List<TileVertex> loop = hull.Select(point => point.Vertex).ToList();
        // The hull runs counter-clockwise; the pieces may face the other way.
        if (hullArea * piecesArea < 0f) loop.Reverse();
        if (!IsFlatAndEvenlyTextured(loop)) return null;
        int material = polygons[group[0]].Material;
        // A hull of more than four corners is split by the caller's output stage; keep it whole for now as one piece.
        return new TilePolygon(material, [.. loop]);
    }

    private static Key KeyOf(TileVertex v) => new((long)MathF.Round(v.X * KeyStep), (long)MathF.Round(v.Y * KeyStep), (long)MathF.Round(v.Z * KeyStep));

    private static void AddEdges(Dictionary<(Key, Key), int> edges, Piece piece, int id)
    {
        for (int i = 0; i < piece.Vertices.Count; i++) edges[(KeyOf(piece.Vertices[i]), KeyOf(piece.Vertices[(i + 1) % piece.Vertices.Count]))] = id;
    }

    private static void RemoveEdges(Dictionary<(Key, Key), int> edges, Piece piece, int id)
    {
        for (int i = 0; i < piece.Vertices.Count; i++)
        {
            var edge = (KeyOf(piece.Vertices[i]), KeyOf(piece.Vertices[(i + 1) % piece.Vertices.Count]));
            if (edges.TryGetValue(edge, out int owner) && owner == id) edges.Remove(edge);
        }
    }

    /// <summary>The polygon made by fusing the two across the first one's edge <paramref name="edge"/> (corner edge to edge+1), or null
    /// when they do not make one flat, convex, evenly textured polygon.</summary>
    private static List<TileVertex>? TryJoin(Piece piece, Piece other, int edge)
    {
        List<TileVertex> p = piece.Vertices, n = other.Vertices;
        TileVertex a = p[edge], b = p[(edge + 1) % p.Count];
        int at = n.FindIndex(v => KeyOf(v) == KeyOf(b));
        if (at < 0 || KeyOf(n[(at + 1) % n.Count]) != KeyOf(a)) return null;

        // The same corners must carry the same texture coordinates on both sides, give or take whole repeats of the texture
        // (cells of a repeating texture are shifted to share tiles), or the join would hide a seam.
        TileVertex nb = n[at], na = n[(at + 1) % n.Count];
        float du = MathF.Round(b.U - nb.U), dv = MathF.Round(b.V - nb.V);
        if (Math.Abs(nb.U + du - b.U) > UvTolerance || Math.Abs(nb.V + dv - b.V) > UvTolerance || Math.Abs(na.U + du - a.U) > UvTolerance || Math.Abs(na.V + dv - a.V) > UvTolerance) return null;

        // b ... a around the first polygon, then the second's corners between a and b.
        var joined = new List<TileVertex>(p.Count + n.Count);
        for (int i = 0; i < p.Count; i++) joined.Add(p[(edge + 1 + i) % p.Count]);
        for (int i = 2; i < n.Count; i++)
        {
            TileVertex corner = n[(at + i) % n.Count];
            joined.Add(corner with { U = corner.U + du, V = corner.V + dv });
        }

        RemoveStraightCorners(joined);
        if (joined.Count < 3 || !IsConvex(joined)) return null;
        if (!IsFlatAndEvenlyTextured(joined)) return null;
        if (!FitsUvLimit(piece.Material, joined)) return null;
        return joined;
    }

    private static void RemoveStraightCorners(List<TileVertex> v)
    {
        for (int i = 0; i < v.Count && v.Count > 3;)
        {
            TileVertex previous = v[(i + v.Count - 1) % v.Count], current = v[i], next = v[(i + 1) % v.Count];
            (float ax, float ay, float az) = (current.X - previous.X, current.Y - previous.Y, current.Z - previous.Z);
            (float bx, float by, float bz) = (next.X - current.X, next.Y - current.Y, next.Z - current.Z);
            float cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
            float cross = MathF.Sqrt(cx * cx + cy * cy + cz * cz);
            float scale = MathF.Sqrt(ax * ax + ay * ay + az * az) * MathF.Sqrt(bx * bx + by * by + bz * bz);
            bool straight = scale < 1e-9f || (cross <= PositionTolerance * MathF.Sqrt(ax * ax + ay * ay + az * az) + 1e-7f && ax * bx + ay * by + az * bz > 0f);
            if (straight) v.RemoveAt(i);
            else i++;
        }
    }

    private static bool IsFlatAndEvenlyTextured(List<TileVertex> v)
    {
        // Normal by Newell's method.
        float nx = 0f, ny = 0f, nz = 0f;
        for (int i = 0; i < v.Count; i++)
        {
            TileVertex c = v[i], d = v[(i + 1) % v.Count];
            nx += (c.Y - d.Y) * (c.Z + d.Z);
            ny += (c.Z - d.Z) * (c.X + d.X);
            nz += (c.X - d.X) * (c.Y + d.Y);
        }

        float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length < 1e-9f) return false;
        (nx, ny, nz) = (nx / length, ny / length, nz / length);

        foreach (TileVertex vertex in v)
        {
            if (MathF.Abs((vertex.X - v[0].X) * nx + (vertex.Y - v[0].Y) * ny + (vertex.Z - v[0].Z) * nz) > PositionTolerance) return false;
        }

        // One affine map from position to texture coordinates must fit every corner.
        int i1 = 1, i2 = -1;
        float best = 0f;
        (float ex, float ey, float ez) = (v[i1].X - v[0].X, v[i1].Y - v[0].Y, v[i1].Z - v[0].Z);
        for (int i = 2; i < v.Count; i++)
        {
            (float fx, float fy, float fz) = (v[i].X - v[0].X, v[i].Y - v[0].Y, v[i].Z - v[0].Z);
            float cx = ey * fz - ez * fy, cy = ez * fx - ex * fz, cz = ex * fy - ey * fx;
            float area = cx * cx + cy * cy + cz * cz;
            if (area > best)
            {
                best = area;
                i2 = i;
            }
        }

        if (i2 < 0) return false;
        (float gx, float gy, float gz) = (v[i2].X - v[0].X, v[i2].Y - v[0].Y, v[i2].Z - v[0].Z);
        float ee = ex * ex + ey * ey + ez * ez, eg = ex * gx + ey * gy + ez * gz, gg = gx * gx + gy * gy + gz * gz;
        float determinant = ee * gg - eg * eg;
        if (MathF.Abs(determinant) < 1e-12f) return false;
        for (int i = 2; i < v.Count; i++)
        {
            if (i == i2) continue;
            (float hx, float hy, float hz) = (v[i].X - v[0].X, v[i].Y - v[0].Y, v[i].Z - v[0].Z);
            float he = hx * ex + hy * ey + hz * ez, hg = hx * gx + hy * gy + hz * gz;
            float s = (he * gg - hg * eg) / determinant, t = (hg * ee - he * eg) / determinant;
            float u = v[0].U + s * (v[i1].U - v[0].U) + t * (v[i2].U - v[0].U);
            float w = v[0].V + s * (v[i1].V - v[0].V) + t * (v[i2].V - v[0].V);
            if (MathF.Abs(u - v[i].U) > UvTolerance || MathF.Abs(w - v[i].V) > UvTolerance) return false;
        }

        return true;
    }
}
