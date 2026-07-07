using System;
using System.Collections.Generic;
using System.Numerics;

namespace Combobulate.Caching;

/// <summary>
/// Greedy preprocess that fuses pairs of triangle <see cref="CachedQuad"/>s
/// back into single quad <see cref="CachedQuad"/>s where it is safe to do so.
/// This is the Phase 2 optimisation in the triangle-support design: many OBJ
/// exporters triangulate authored quads, and recovering them lets those faces
/// hit the existing 1-sprite-per-quad fast path instead of the per-triangle
/// clip path.
///
/// <para>Two triangles fuse to a quad iff:
/// <list type="number">
///   <item>They share an edge (exactly two distinct positions in common, within
///         <see cref="PositionEpsilon"/>).</item>
///   <item>They are coplanar — both normals are unit-length and within
///         <see cref="CoplanarCosineEpsilon"/> of each other.</item>
///   <item>They share the same material name.</item>
///   <item>The UVs reported at the shared positions match
///         (within <see cref="UvEpsilon"/>).</item>
///   <item>Their windings are opposite across the shared edge (consistent
///         CCW orientation for the combined quad).</item>
///   <item>The resulting quad is convex (no reflex corner) and non-degenerate.</item>
/// </list>
/// </para>
///
/// <para>Each triangle is paired with at most one neighbour; pairing order is
/// deterministic (lowest source index first, lowest partner index first) so
/// the same input always produces the same output.</para>
/// </summary>
internal static class QuadRecovery
{
    /// <summary>Position-equality tolerance for shared-edge detection. The
    /// CachedQuads passed in are already centered; this tolerance is in the
    /// same model-space units as the source positions.</summary>
    public const float PositionEpsilon = 1e-5f;

    /// <summary>UV-equality tolerance at the shared edge.</summary>
    public const float UvEpsilon = 1e-5f;

    /// <summary>Cosine-of-angle tolerance for "coplanar". With unit normals,
    /// dot >= 1 - this means the angle between normals is &lt; ~0.025° (when
    /// epsilon = 1e-7), tightening to a strict coplanarity test. Set loosely
    /// at 1e-4 to absorb float quantisation across exporters.</summary>
    public const float CoplanarCosineEpsilon = 1e-4f;

    /// <summary>Relative tolerance (as a fraction of the longer spanning edge)
    /// for the parallelogram test in <see cref="TryFuse"/>. A recovered quad is
    /// only accepted when its fourth corner V2 lies within this fraction of the
    /// ideal parallelogram corner V1 + V3 - V0, because the quad sprite path
    /// renders quads as parallelograms and ignores the stored V2.</summary>
    public const float ParallelogramEpsilon = 1e-3f;

    /// <summary>
    /// Recover quads from <paramref name="triangles"/>. Returns the list of
    /// fused quads and the list of unmatched triangles. Each output
    /// CachedQuad uses <paramref name="nextSourceIndex"/> incremented to
    /// produce stable, unique <see cref="CachedQuad.SourceIndex"/> values.
    /// </summary>
    /// <param name="triangles">Triangle CachedQuads to pair. Each must have
    /// <see cref="CachedQuad.IsTriangle"/> == true.</param>
    /// <param name="recoveredQuads">Output: fused quad CachedQuads.</param>
    /// <param name="leftoverTriangles">Output: triangles that could not be
    /// paired with any neighbour.</param>
    public static void Recover(
        IReadOnlyList<CachedQuad> triangles,
        out List<CachedQuad> recoveredQuads,
        out List<CachedQuad> leftoverTriangles)
    {
        recoveredQuads = new List<CachedQuad>();
        leftoverTriangles = new List<CachedQuad>();
        int n = triangles.Count;
        if (n == 0) return;

        var paired = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (paired[i]) continue;
            var ti = triangles[i];

            int bestJ = -1;
            CachedQuad bestQuad = default;
            for (int j = i + 1; j < n; j++)
            {
                if (paired[j]) continue;
                var tj = triangles[j];
                if (!TryFuse(ti, tj, out var fused)) continue;
                bestJ = j;
                bestQuad = fused;
                break; // deterministic: first valid partner wins.
            }

            if (bestJ >= 0)
            {
                paired[i] = true;
                paired[bestJ] = true;
                recoveredQuads.Add(bestQuad);
            }
            else
            {
                leftoverTriangles.Add(ti);
            }
        }
    }

    /// <summary>
    /// Try to fuse two triangle CachedQuads into a single quad CachedQuad.
    /// Returns true and sets <paramref name="result"/> on success; returns
    /// false otherwise.
    /// </summary>
    internal static bool TryFuse(CachedQuad a, CachedQuad b, out CachedQuad result)
    {
        result = default;
        if (!a.IsTriangle || !b.IsTriangle) return false;

        // 1. Material must match (null == null).
        if (!string.Equals(a.MaterialName, b.MaterialName, System.StringComparison.Ordinal))
            return false;

        // 2. Coplanar (normals nearly identical). Normals on triangle CachedQuads
        //    are unit-length per ObjGeometry.Build.
        if (Vector3.Dot(a.Normal, b.Normal) < 1f - CoplanarCosineEpsilon)
            return false;

        // 3. Find the shared edge: exactly two positions of `a` must match
        //    two positions of `b` (within ε). Record which a-vertices are
        //    shared and which b-vertex is unique.
        var aPos = new[] { a.V0, a.V1, a.V2 };
        var aUv = new[] { a.Uv0, a.Uv1, a.Uv2 };
        var bPos = new[] { b.V0, b.V1, b.V2 };
        var bUv = new[] { b.Uv0, b.Uv1, b.Uv2 };

        // matchOfA[i] = index in b whose position matches a[i], or -1.
        var matchOfA = new[] { -1, -1, -1 };
        int sharedCount = 0;
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (PositionsEqual(aPos[i], bPos[j]))
                {
                    matchOfA[i] = j;
                    sharedCount++;
                    break;
                }
            }
        }
        if (sharedCount != 2) return false;

        // 4. UV continuity at the shared vertices. Only enforced when BOTH
        //    triangles had explicit UVs in the source OBJ; otherwise the
        //    UVs are parser-assigned defaults (independent per triangle)
        //    and there's no semantic continuity to preserve.
        bool checkUv = a.HasExplicitUv && b.HasExplicitUv;
        if (checkUv)
        {
            for (int i = 0; i < 3; i++)
            {
                int j = matchOfA[i];
                if (j < 0) continue;
                if (!UvsEqual(aUv[i], bUv[j])) return false;
            }
        }

        // 5. Identify the unique vertices: one in a (uniqueA), one in b (uniqueB).
        int uniqueA = -1;
        int uniqueB = -1;
        var bUsedAsShared = new[] { false, false, false };
        for (int i = 0; i < 3; i++)
        {
            if (matchOfA[i] < 0) uniqueA = i;
            else bUsedAsShared[matchOfA[i]] = true;
        }
        for (int j = 0; j < 3; j++)
        {
            if (!bUsedAsShared[j]) { uniqueB = j; break; }
        }
        if (uniqueA < 0 || uniqueB < 0) return false;

        // 6. Determine the shared edge in `a`'s winding order. The two shared
        //    vertices of `a` are at indices (uniqueA+1)%3 and (uniqueA+2)%3.
        int aS1 = (uniqueA + 1) % 3;
        int aS2 = (uniqueA + 2) % 3;
        // Edge direction in a is aS1 -> aS2. For consistent CCW winding across
        // the fused quad, the same edge in b must run aS2 -> aS1 (i.e. b
        // traverses the shared edge in the opposite direction).
        int bMatchOfAS1 = matchOfA[aS1];
        int bMatchOfAS2 = matchOfA[aS2];

        // In b's order, after uniqueB comes (uniqueB+1)%3 then (uniqueB+2)%3.
        // We need the shared edge to read (uniqueB+1)%3 -> (uniqueB+2)%3 in b,
        // and that should equal aS2 -> aS1 in a's positions.
        int bAfter1 = (uniqueB + 1) % 3;
        int bAfter2 = (uniqueB + 2) % 3;
        if (bAfter1 != bMatchOfAS2 || bAfter2 != bMatchOfAS1)
            return false;

        // 7. Build the quad. The triangulator splits a quad along V0→V2, so
        //    we want the two output triangles (V0,V1,V2) and (V0,V2,V3) to be
        //    exactly the two input triangles. Choose:
        //       V0 = uniqueA   (apex of triangle a on its side of the shared edge)
        //       V1 = aS1       (first shared vertex in a's CCW order)
        //       V2 = uniqueB   (apex of triangle b on the opposite side)
        //       V3 = aS2       (second shared vertex in a's CCW order)
        //    Then:
        //       (V0,V1,V2) = (A, p, B) which matches a's CCW with B substituted
        //         for q across the shared edge — same outward normal as a.
        //       (V0,V2,V3) = (A, B, q) which has the same winding handedness as
        //         b's CCW (B, q, p) since (A,B,q) shares edge B→q with b.
        //    The diagonal V0→V2 is A→B, which is the seam that the original
        //    triangulation cut. Re-triangulating the recovered quad along that
        //    same diagonal reproduces the input triangle pair losslessly.
        var v0 = aPos[uniqueA];
        var v1 = aPos[aS1];
        var v2 = bPos[uniqueB];
        var v3 = aPos[aS2];
        var uv0 = aUv[uniqueA];
        var uv1 = aUv[aS1];
        var uv2 = bUv[uniqueB];
        var uv3 = aUv[aS2];

        // Sanity: the recovered quad's normal must match the two source normals.
        var quadCross = Vector3.Cross(v1 - v0, v3 - v0);
        if (quadCross.LengthSquared() <= 1e-14f) return false;
        var quadNormal = Vector3.Normalize(quadCross);
        if (Vector3.Dot(quadNormal, a.Normal) < 1f - CoplanarCosineEpsilon)
            return false;

        // Convexity check: walk the four corners, ensure all interior cross
        // products have the same sign as the face normal.
        if (!IsConvexCcw(v0, v1, v2, v3, quadNormal)) return false;

        // Parallelogram check: the quad sprite fast-path renders a quad as a
        // parallelogram spanned by V0, the edge V0->V1 (xAxis) and the edge
        // V0->V3 (yAxis); it never references V2 and implicitly assumes
        // V2 == V1 + V3 - V0. Fusing a *non*-parallelogram convex quad (e.g. a
        // pair of fan triangles from an n-gon, which is a trapezoid/kite, not a
        // parallelogram) would therefore render the wrong silhouette — the true
        // V2 corner is dropped, leaving a background gap where the quad should
        // be and an overhang on the opposite side. Only fuse when the recovered
        // quad is a genuine parallelogram; otherwise leave the two triangles
        // un-fused so they render exactly via the per-triangle clip path.
        var expectedV2 = v1 + v3 - v0;
        float edgeScale = MathF.Max(
            MathF.Max((v1 - v0).Length(), (v3 - v0).Length()),
            1e-6f);
        if ((v2 - expectedV2).Length() > ParallelogramEpsilon * edgeScale)
            return false;

        var centroid = (v0 + v1 + v2 + v3) * 0.25f;

        // When both source triangles had implicit (parser-default) UVs, the
        // recovered quad gets the canonical unit-square UV mapping that the
        // quad path expects: (0,0), (1,0), (1,1), (0,1). When at least one
        // had explicit UVs, we propagate the (potentially incomplete) UVs
        // and mark the result as carrying explicit UVs.
        bool resultHasExplicitUv = a.HasExplicitUv || b.HasExplicitUv;
        if (!resultHasExplicitUv)
        {
            uv0 = new Vector2(0, 0);
            uv1 = new Vector2(1, 0);
            uv2 = new Vector2(1, 1);
            uv3 = new Vector2(0, 1);
        }

        result = new CachedQuad(
            sourceIndex: a.SourceIndex,
            v0: v0, v1: v1, v2: v2, v3: v3,
            centroid: centroid,
            normal: quadNormal,
            fallbackColor: a.FallbackColor,
            materialName: a.MaterialName,
            uv0: uv0, uv1: uv1, uv2: uv2, uv3: uv3,
            isTriangle: false,
            hasExplicitUv: resultHasExplicitUv);
        return true;
    }

    private static bool PositionsEqual(Vector3 p, Vector3 q)
    {
        return (p - q).LengthSquared() <= PositionEpsilon * PositionEpsilon;
    }

    private static bool UvsEqual(Vector2 p, Vector2 q)
    {
        return (p - q).LengthSquared() <= UvEpsilon * UvEpsilon;
    }

    /// <summary>Returns true when the quad <c>(v0, v1, v2, v3)</c> is convex
    /// and wound CCW about <paramref name="planeNormal"/>.</summary>
    private static bool IsConvexCcw(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 planeNormal)
    {
        // For a convex CCW polygon, every cross product of consecutive edges
        // (e_i × e_{i+1}) must point in the same direction as the plane normal.
        var verts = new[] { v0, v1, v2, v3 };
        for (int i = 0; i < 4; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % 4];
            var c = verts[(i + 2) % 4];
            var cross = Vector3.Cross(b - a, c - b);
            if (Vector3.Dot(cross, planeNormal) <= 0f) return false;
        }
        return true;
    }
}
