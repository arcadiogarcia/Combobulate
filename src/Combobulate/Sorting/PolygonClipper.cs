using System;
using System.Collections.Generic;
using System.Numerics;

namespace Combobulate.Sorting;

/// <summary>
/// Two-sided polygon-vs-plane clipper. Given a convex polygon and a
/// splitting plane, emits up to two new convex polygons — one for each
/// half-space — preserving as much "quadness" as the geometry allows.
///
/// <para><b>Why two-sided.</b> Classic Sutherland-Hodgman clipping emits
/// only the inside half. The mesh decomposer needs <i>both</i> halves
/// so the working set after a plane cut contains every fragment of every
/// originally-uncut polygon. A two-sided pass walks each edge once and
/// emits a vertex (or an interpolated cut point) to the appropriate
/// side ring(s); the resulting front and back rings are independently
/// usable as fresh <see cref="PolygonFragment"/> inputs to the next
/// plane.</para>
///
/// <para><b>Quad preservation.</b> When the splitter chord enters one
/// edge of the polygon and exits the OPPOSITE edge ("Case A" — the
/// canonical axis-aligned grid case for book-like geometry), a 4-vertex
/// input becomes two 4-vertex outputs. When the chord enters/exits
/// adjacent edges ("Case B"), one side becomes a 3-vertex triangle and
/// the other becomes a 5-vertex pentagon, which may later be split
/// back into quads by further cuts or fan-triangulated at emission. We
/// never introduce Steiner vertices on un-cut edges (those would create
/// T-junctions that cause sub-pixel rendering cracks in the compositor),
/// so the decomposer accepts whatever vertex counts the chord geometry
/// dictates.</para>
///
/// <para><b>Numerical safety.</b> Vertex-vs-plane classification uses
/// <see cref="GeometryPredicates.SignedDistanceSide(float)"/> so a
/// vertex within ±<see cref="GeometryPredicates.DistanceEpsilon"/> of
/// the plane is treated as exactly ON the plane and routed to BOTH
/// rings (where it acts as a shared boundary vertex). This avoids
/// hairline slivers from sub-epsilon disagreements that fp-noise could
/// otherwise turn into degenerate fragments.</para>
///
/// <para>Adjacent identical vertices in the output rings (which arise
/// when an On-plane vertex coincides with a cut point at exactly the
/// same coordinates) are collapsed during emission so output polygons
/// never carry zero-length edges.</para>
/// </summary>
internal static class PolygonClipper
{
    /// <summary>Outcome of a plane-vs-polygon split.</summary>
    public enum SplitOutcome
    {
        /// <summary>All vertices on the +Normal side (within epsilon). The input is unchanged on the front side.</summary>
        AllFront = 0,
        /// <summary>All vertices on the -Normal side. The input is unchanged on the back side.</summary>
        AllBack = 1,
        /// <summary>All vertices on the plane (within epsilon). The input is coplanar with the splitter.</summary>
        OnPlane = 2,
        /// <summary>The polygon straddles the plane. <c>front</c> and <c>back</c> contain the two halves.</summary>
        Split = 3,
        /// <summary>The polygon was degenerate (zero vertices). Both out-params are default.</summary>
        Degenerate = 4,
    }

    /// <summary>
    /// Splits <paramref name="input"/> against <paramref name="plane"/>.
    /// </summary>
    /// <param name="input">Convex polygon to split.</param>
    /// <param name="plane">Splitter plane.</param>
    /// <param name="distanceEpsilon">
    /// Tolerance for the on-plane classification of a vertex. Defaults
    /// to <see cref="GeometryPredicates.DistanceEpsilon"/>. Pass a
    /// larger value to be more aggressive about routing near-plane
    /// vertices to both sides (useful when the source has noisy fp
    /// coordinates).
    /// </param>
    /// <param name="front">On <see cref="SplitOutcome.Split"/>, receives the +Normal-side polygon. Otherwise default.</param>
    /// <param name="back">On <see cref="SplitOutcome.Split"/>, receives the −Normal-side polygon. Otherwise default.</param>
    public static SplitOutcome Split(
        in PolygonFragment input,
        in Plane3 plane,
        float distanceEpsilon,
        out PolygonFragment front,
        out PolygonFragment back)
    {
        front = default;
        back = default;
        int n = input.Count;
        if (n < 3) return SplitOutcome.Degenerate;

        // Pre-classify all vertices relative to the splitter.
        Span<float> dists = n <= 16 ? stackalloc float[n] : new float[n];
        Span<int> sides = n <= 16 ? stackalloc int[n] : new int[n];
        int frontCount = 0, backCount = 0, onCount = 0;
        for (int i = 0; i < n; i++)
        {
            dists[i] = plane.SignedDistance(input.Vertices[i]);
            int side = SignedSide(dists[i], distanceEpsilon);
            sides[i] = side;
            if (side > 0) frontCount++;
            else if (side < 0) backCount++;
            else onCount++;
        }

        // Quick early exits.
        if (frontCount == 0 && backCount == 0) return SplitOutcome.OnPlane;
        if (backCount == 0) return SplitOutcome.AllFront;
        if (frontCount == 0) return SplitOutcome.AllBack;

        // Two-sided walk: for each edge i → (i+1) % n, route the i-th
        // vertex to its side(s), and if the edge strictly straddles
        // (one side strictly +, other strictly −), emit the interpolation
        // point to BOTH rings.
        var frontV = new List<Vector3>(n + 1);
        var frontU = new List<Vector2>(n + 1);
        var backV = new List<Vector3>(n + 1);
        var backU = new List<Vector2>(n + 1);

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            int si = sides[i];
            int sj = sides[j];

            // Emit v_i to whichever side(s) it belongs to.
            if (si >= 0) AppendIfDistinct(frontV, frontU, input.Vertices[i], input.Uvs[i]);
            if (si <= 0) AppendIfDistinct(backV, backU, input.Vertices[i], input.Uvs[i]);

            // Edge crosses strictly: interpolate and emit cut point to both rings.
            if ((si > 0 && sj < 0) || (si < 0 && sj > 0))
            {
                float di = dists[i];
                float dj = dists[j];
                // di, dj have strict opposite signs, so |di − dj| ≥ 2·distanceEpsilon > DivisorEpsilon.
                if (!GeometryPredicates.TryComputeSegmentParam(di, dj, out float t))
                {
                    // Defensive: in this branch the divide should always be safe.
                    // If it isn't, snap to the midpoint and continue (the resulting
                    // fragment is still a valid clip, just slightly biased).
                    t = 0.5f;
                }
                var cutV = input.Vertices[i] + (input.Vertices[j] - input.Vertices[i]) * t;
                var cutU = input.Uvs[i] + (input.Uvs[j] - input.Uvs[i]) * t;
                AppendIfDistinct(frontV, frontU, cutV, cutU);
                AppendIfDistinct(backV, backU, cutV, cutU);
            }
        }

        // Collapse a leading/trailing duplicate pair (closing-edge dedup).
        StripWrapDuplicate(frontV, frontU);
        StripWrapDuplicate(backV, backU);

        if (frontV.Count < 3 || backV.Count < 3)
        {
            // Both sides should have ≥ 3 vertices for a true split. If
            // one side collapsed below the minimum (rare numerical
            // accident — the splitter chord was epsilon-close to an
            // edge), treat as a no-op and report the non-degenerate
            // side as the unchanged input.
            if (frontV.Count >= 3 && backV.Count < 3) return SplitOutcome.AllFront;
            if (backV.Count >= 3 && frontV.Count < 3) return SplitOutcome.AllBack;
            return SplitOutcome.Degenerate;
        }

        front = new PolygonFragment(
            frontV.ToArray(), frontU.ToArray(),
            input.SourceQuadIndex, input.Plane);
        back = new PolygonFragment(
            backV.ToArray(), backU.ToArray(),
            input.SourceQuadIndex, input.Plane);
        return SplitOutcome.Split;
    }

    /// <summary>
    /// Three-way signed-distance classification with caller-supplied
    /// tolerance. Returns +1 for strictly front, −1 for strictly back,
    /// 0 for on-plane.
    /// </summary>
    public static int SignedSide(float signedDistance, float epsilon)
    {
        if (signedDistance > epsilon) return +1;
        if (signedDistance < -epsilon) return -1;
        return 0;
    }

    /// <summary>
    /// Appends a vertex (with its UV) to the destination rings unless
    /// it would be a near-duplicate of the previous vertex in the
    /// front ring. This deduplication is the primary line of defence
    /// against zero-length output edges, which arise when an On-plane
    /// vertex coincides with a freshly-computed cut point.
    /// </summary>
    private static void AppendIfDistinct(List<Vector3> verts, List<Vector2> uvs, Vector3 v, Vector2 uv)
    {
        if (verts.Count > 0)
        {
            var last = verts[verts.Count - 1];
            if (Vector3.DistanceSquared(last, v) < CoincidentVertexEpsilonSquared) return;
        }
        verts.Add(v);
        uvs.Add(uv);
    }

    /// <summary>
    /// Collapses an exact wrap-around duplicate: if the last appended
    /// vertex coincides with the first, drop the last (so the ring is
    /// implicitly closed via the array boundary, not via a repeated
    /// vertex). Called once per output ring after the walk.
    /// </summary>
    private static void StripWrapDuplicate(List<Vector3> verts, List<Vector2> uvs)
    {
        if (verts.Count >= 2)
        {
            var first = verts[0];
            var last = verts[verts.Count - 1];
            if (Vector3.DistanceSquared(first, last) < CoincidentVertexEpsilonSquared)
            {
                verts.RemoveAt(verts.Count - 1);
                uvs.RemoveAt(uvs.Count - 1);
            }
        }
    }

    /// <summary>
    /// Squared-distance threshold for "two vertices are the same point".
    /// Calibrated to match <see cref="GeometryPredicates.DistanceEpsilon"/>:
    /// vertices within DistanceEpsilon (1e-4 in unit-cube object space)
    /// of each other are coincident, so the squared threshold is 1e-8.
    /// </summary>
    public const float CoincidentVertexEpsilonSquared =
        GeometryPredicates.DistanceEpsilon * GeometryPredicates.DistanceEpsilon;
}
