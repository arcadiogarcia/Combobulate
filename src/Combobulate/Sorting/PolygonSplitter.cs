using System.Collections.Generic;
using System.Numerics;

namespace Combobulate.Sorting;

/// <summary>
/// Where a polygon (or vertex) lies relative to a partitioning plane.
/// </summary>
public enum PlaneSide
{
    /// <summary>Within ±epsilon of the plane.</summary>
    On = 0,
    /// <summary>Strictly on the +Normal side of the plane.</summary>
    Front = 1,
    /// <summary>Strictly on the −Normal side.</summary>
    Back = 2,
    /// <summary>Some vertices Front, others Back (only valid for a polygon, not a single vertex).</summary>
    Spanning = 3,
}

/// <summary>
/// Splits triangles against a partitioning plane. The split preserves
/// winding (CCW triangles produce CCW fragments) and interpolates UVs
/// along the cut edge so split fragments map back to the correct portion
/// of their source quad's texture.
///
/// <para>Used at <see cref="BspSorter"/> build time to produce a
/// cycle-free triangle set, and at <see cref="NewellSorter"/> run time
/// to break length-2+ cycles when they're actually exposed by the
/// camera.</para>
///
/// <para><b>Epsilon discipline.</b> Vertices within ±<see cref="Epsilon"/>
/// of the plane are classified as <see cref="PlaneSide.On"/> and emitted
/// to whichever side the triangle's other vertices favour. This avoids
/// generating sliver triangles narrower than the float-precision noise.
/// </para>
/// </summary>
public static class PolygonSplitter
{
    /// <summary>
    /// Distance threshold used to classify a vertex as lying ON the splitting
    /// plane. Re-exports <see cref="GeometryPredicates.DistanceEpsilon"/> for
    /// readability at call sites that compare raw signed distances directly.
    /// </summary>
    public const float Epsilon = GeometryPredicates.DistanceEpsilon;

    /// <summary>Outcome of <see cref="ClassifyTriangle"/>.</summary>
    public readonly struct Classification
    {
        public Classification(PlaneSide side, float dA, float dB, float dC)
        {
            Side = side; DA = dA; DB = dB; DC = dC;
        }
        /// <summary>Aggregate side for the whole triangle.</summary>
        public PlaneSide Side { get; }
        /// <summary>Signed distance of vertex A from the plane.</summary>
        public float DA { get; }
        /// <summary>Signed distance of vertex B.</summary>
        public float DB { get; }
        /// <summary>Signed distance of vertex C.</summary>
        public float DC { get; }
    }

    /// <summary>Classify a single vertex against a plane (epsilon-aware).</summary>
    public static PlaneSide ClassifyVertex(float signedDistance)
    {
        // Routed through the central predicate so the distance-scale epsilon
        // is owned in one place. Mapping: +1 → Front, -1 → Back, 0 → On.
        return GeometryPredicates.SignedDistanceSide(signedDistance) switch
        {
            +1 => PlaneSide.Front,
            -1 => PlaneSide.Back,
            _  => PlaneSide.On,
        };
    }

    /// <summary>Classify a whole triangle against a plane.</summary>
    public static Classification ClassifyTriangle(in RenderTriangle tri, in Plane3 plane)
    {
        var dA = plane.SignedDistance(tri.A);
        var dB = plane.SignedDistance(tri.B);
        var dC = plane.SignedDistance(tri.C);
        var sA = ClassifyVertex(dA);
        var sB = ClassifyVertex(dB);
        var sC = ClassifyVertex(dC);

        bool anyFront = sA == PlaneSide.Front || sB == PlaneSide.Front || sC == PlaneSide.Front;
        bool anyBack  = sA == PlaneSide.Back  || sB == PlaneSide.Back  || sC == PlaneSide.Back;

        if (anyFront && anyBack) return new Classification(PlaneSide.Spanning, dA, dB, dC);
        if (anyFront)             return new Classification(PlaneSide.Front,    dA, dB, dC);
        if (anyBack)              return new Classification(PlaneSide.Back,     dA, dB, dC);
        return new Classification(PlaneSide.On, dA, dB, dC);
    }

    /// <summary>
    /// Splits <paramref name="tri"/> against <paramref name="plane"/>.
    /// Triangles entirely on one side (including the On case, which is
    /// arbitrarily routed to the Front side) are appended unmodified to
    /// the matching output list. Triangles that span the plane are cut
    /// into 1+2 or 2+1 sub-triangles depending on which side has the
    /// lone vertex; both output sub-meshes preserve CCW winding.
    /// </summary>
    /// <param name="tri">Triangle to split.</param>
    /// <param name="plane">Partitioning plane.</param>
    /// <param name="frontOut">Receives sub-triangles on the +Normal side.</param>
    /// <param name="backOut">Receives sub-triangles on the −Normal side.</param>
    /// <param name="coplanarFrontOut">
    /// Optional. If non-null and the triangle is coplanar (all three
    /// vertices On) AND its normal points in the same hemisphere as
    /// <paramref name="plane"/>.Normal, the triangle is appended here
    /// instead of <paramref name="frontOut"/>. BSP uses this to bundle
    /// coplanar-with-splitter triangles at the splitter's tree node.
    /// </param>
    /// <param name="coplanarBackOut">As <paramref name="coplanarFrontOut"/> but for opposite-facing coplanar triangles.</param>
    public static void Split(
        in RenderTriangle tri,
        in Plane3 plane,
        List<RenderTriangle> frontOut,
        List<RenderTriangle> backOut,
        List<RenderTriangle>? coplanarFrontOut = null,
        List<RenderTriangle>? coplanarBackOut = null)
    {
        var c = ClassifyTriangle(tri, plane);

        switch (c.Side)
        {
            case PlaneSide.Front:
                frontOut.Add(tri);
                return;

            case PlaneSide.Back:
                backOut.Add(tri);
                return;

            case PlaneSide.On:
                // Coplanar-with-splitter: route by relative facing.
                if (coplanarFrontOut != null && Vector3.Dot(tri.Plane.Normal, plane.Normal) >= 0)
                    coplanarFrontOut.Add(tri);
                else if (coplanarBackOut != null)
                    coplanarBackOut.Add(tri);
                else
                    frontOut.Add(tri);
                return;

            case PlaneSide.Spanning:
                SplitSpanning(tri, plane, c, frontOut, backOut);
                return;
        }
    }

    private static void SplitSpanning(
        in RenderTriangle tri,
        in Plane3 plane,
        in Classification c,
        List<RenderTriangle> frontOut,
        List<RenderTriangle> backOut)
    {
        // Walk the triangle's edges A→B, B→C, C→A. For each edge whose
        // endpoints lie on opposite sides of the plane, compute the
        // intersection point + interpolated UV. Then re-emit triangles
        // by walking around the polygon and routing each edge to the
        // correct output list, splitting where intersections occur.
        //
        // We use a small fan-walker that handles all 6 patterns
        // (3 vertices × 2 sides each, minus all-front and all-back which
        // never reach this code path) with a single structure.
        Span<Vector3> verts = stackalloc Vector3[3] { tri.A, tri.B, tri.C };
        Span<Vector2> uvs   = stackalloc Vector2[3] { tri.UvA, tri.UvB, tri.UvC };
        Span<float>   dists = stackalloc float[3]   { c.DA, c.DB, c.DC };

        // Build per-side vertex/uv ring buffers (max 4 vertices per side
        // for a triangle: original + up to 2 intersections + the wrap).
        Span<Vector3> frontV = stackalloc Vector3[4];
        Span<Vector2> frontU = stackalloc Vector2[4];
        Span<Vector3> backV  = stackalloc Vector3[4];
        Span<Vector2> backU  = stackalloc Vector2[4];
        int fc = 0, bc = 0;

        for (int i = 0; i < 3; i++)
        {
            int j = (i + 1) % 3;
            var di = dists[i];
            var dj = dists[j];
            var si = ClassifyVertex(di);
            var sj = ClassifyVertex(dj);

            // Always start by emitting the i-th vertex to its own side(s).
            if (si == PlaneSide.Front || si == PlaneSide.On)
            {
                frontV[fc] = verts[i]; frontU[fc] = uvs[i]; fc++;
            }
            if (si == PlaneSide.Back || si == PlaneSide.On)
            {
                backV[bc] = verts[i]; backU[bc] = uvs[i]; bc++;
            }

            // If this edge crosses the plane (one strict front, the other
            // strict back), emit the interpolation point to BOTH rings.
            if ((si == PlaneSide.Front && sj == PlaneSide.Back) ||
                (si == PlaneSide.Back  && sj == PlaneSide.Front))
            {
                var t = di / (di - dj); // di and dj have opposite signs → denom != 0
                if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                var p = verts[i] + (verts[j] - verts[i]) * t;
                var u = uvs[i] + (uvs[j] - uvs[i]) * t;
                frontV[fc] = p; frontU[fc] = u; fc++;
                backV[bc]  = p; backU[bc]  = u; bc++;
            }
        }

        EmitFan(frontV, frontU, fc, tri, frontOut);
        EmitFan(backV,  backU,  bc, tri, backOut);
    }

    private static void EmitFan(
        Span<Vector3> verts, Span<Vector2> uvs, int n,
        in RenderTriangle source,
        List<RenderTriangle> dest)
    {
        if (n < 3) return; // Degenerate side (everything was On).
        // Triangle fan around verts[0]: (0, k, k+1) for k = 1..n-2.
        for (int k = 1; k < n - 1; k++)
        {
            var a = verts[0];
            var b = verts[k];
            var c = verts[k + 1];
            // Skip degenerate fragments produced by collinear interpolations.
            // Scale: squared length of an edge cross product (area² × 4).
            var cross = Vector3.Cross(b - a, c - a);
            if (GeometryPredicates.IsDegenerateCross3D(cross)) continue;
            // Source triangle's plane is preserved (cuts are in-plane subsets).
            var t = new RenderTriangle(a, b, c, uvs[0], uvs[k], uvs[k + 1], source.SourceQuadIndex, source.Plane);
            dest.Add(t);
        }
    }
}
