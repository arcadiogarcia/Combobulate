using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Analytical painter's-algorithm correctness oracle for sort-test diagnostics.
///
/// <para>
/// Given a sorted draw order produced by <see cref="Combobulate.Sorting.IFaceSorter"/>,
/// this checker decides — without ever rasterising a pixel — whether the order
/// is a <i>valid</i> back-to-front order for the painter's algorithm: i.e.
/// for every pair <c>(A, B)</c> where <c>A</c> is drawn first, <c>B</c> never
/// gets occluded by <c>A</c> in a region where <c>A</c> should actually be
/// behind <c>B</c>. Concretely:
/// </para>
///
/// <list type="number">
///   <item>Project both quads to the 2D screen plane (orthographic for now —
///         camera at infinite +Z looking toward origin, so projection just drops Z).</item>
///   <item>Compute the convex polygon overlap of the two projected quads using
///         <see href="https://en.wikipedia.org/wiki/Sutherland%E2%80%93Hodgman_algorithm">
///         Sutherland–Hodgman</see> clipping.</item>
///   <item>If the overlap is empty (or degenerate), no constraint exists between
///         this pair — they can be drawn in either order.</item>
///   <item>Otherwise, evaluate the depth (view-space Z) of each face's plane at
///         every overlap-polygon vertex. If <c>A</c>'s depth ever exceeds
///         <c>B</c>'s (i.e. <c>A</c> is closer to the camera than <c>B</c> at
///         a point inside the overlap region), the painter's order is wrong:
///         <c>B</c> drawn second will paint over <c>A</c> at a pixel where
///         <c>A</c> was supposed to be in front.</item>
/// </list>
///
/// <para>
/// This is dramatically stricter than <see cref="SortAssertions.AssertBackToFront"/>
/// (which only checks centroid-Z monotonicity) and catches actual visible
/// painter glitches that the centroid heuristic misses entirely.
/// </para>
///
/// <para>
/// All quads in <see cref="ObjGeometry"/> are convex (the parser only accepts
/// quads, not n-gons), so Sutherland–Hodgman applies without precondition.
/// </para>
/// </summary>
internal static class PainterCorrectness
{
    /// <summary>
    /// Depth-overlap epsilon. The painter's order is reported as wrong only when
    /// face <c>A</c> exceeds face <c>B</c>'s depth at an overlap vertex by more
    /// than this. Set to match <see cref="Combobulate.Sorting.GeometryPredicates.DistanceEpsilon"/>
    /// scale so coplanar / shared-edge faces (e.g. a book's cover meeting its
    /// spine along their seam) are not flagged.
    /// </summary>
    public const float DepthEpsilon = 1e-3f;

    /// <summary>
    /// One painter's-algorithm violation: <c>FirstIdx</c> is drawn before
    /// <c>SecondIdx</c>, but at <c>OverlapPoint</c> the first face is in front
    /// of the second by <c>DepthDelta</c> (in view-space Z units).
    /// </summary>
    public readonly struct Violation
    {
        public Violation(int firstIdx, int secondIdx, Vector2 overlapPoint, float depthDelta)
        {
            FirstIdx = firstIdx;
            SecondIdx = secondIdx;
            OverlapPoint = overlapPoint;
            DepthDelta = depthDelta;
        }

        public int FirstIdx { get; }
        public int SecondIdx { get; }
        public Vector2 OverlapPoint { get; }
        public float DepthDelta { get; }

        public override string ToString()
            => $"pair=({FirstIdx},{SecondIdx}) at ({OverlapPoint.X:F3},{OverlapPoint.Y:F3}) depthDelta={DepthDelta:F4}";
    }

    /// <summary>
    /// When non-null, the oracle calls this once per pair-check with diagnostic info.
    /// Used by spot-check tests to dump the per-pair internal state without
    /// polluting the production code path.
    /// </summary>
    internal static System.Action<int, int, List<Vector2>, List<Vector2>, List<Vector2>>? PerPairDiagnostic;

    /// <summary>
    /// Walks every drawn-before pair in <paramref name="order"/> (i.e. every
    /// <c>i &lt; j</c>, not just adjacent — a sort error can put the wrong
    /// face many slots away) and returns the worst violation found, or
    /// <c>null</c> if the order is valid.
    /// </summary>
    /// <remarks>
    /// Cost is O(n²·k) where n is the visible-face count and k is the number
    /// of vertices in each pairwise overlap polygon (≤ 8 for two quads).
    /// For the book mesh n ≤ 12, so 66 pair checks per yaw — trivially fast.
    /// </remarks>
    public static Violation? FindWorstViolation(ObjGeometry geometry, int[] order, int count, Matrix4x4 rotation)
    {
        var quads = geometry.Quads;
        // Pre-rotate the visible quads' four corners into view space.
        var v = new Vector3[count, 4];
        for (int i = 0; i < count; i++)
        {
            var q = quads[order[i]];
            v[i, 0] = Vector3.Transform(q.V0, rotation);
            v[i, 1] = Vector3.Transform(q.V1, rotation);
            v[i, 2] = Vector3.Transform(q.V2, rotation);
            v[i, 3] = Vector3.Transform(q.V3, rotation);
        }

        Violation? worst = null;
        for (int i = 0; i < count; i++)
        {
            // Subject polygon (drawn first).
            var subject = new List<Vector2>(4)
            {
                new(v[i, 0].X, v[i, 0].Y),
                new(v[i, 1].X, v[i, 1].Y),
                new(v[i, 2].X, v[i, 2].Y),
                new(v[i, 3].X, v[i, 3].Y),
            };
            // Make subject CCW for SH; if it's CW (back-facing in 2D), reverse.
            if (PolygonSignedArea(subject) < 0) subject.Reverse();
            var planeI = PlaneFromTriangle(v[i, 0], v[i, 1], v[i, 2]);
            // If the plane is degenerate (rare — would need a sliver quad) skip.
            if (planeI.IsDegenerate) continue;

            for (int j = i + 1; j < count; j++)
            {
                // Clip polygon (drawn second).
                var clip = new List<Vector2>(4)
                {
                    new(v[j, 0].X, v[j, 0].Y),
                    new(v[j, 1].X, v[j, 1].Y),
                    new(v[j, 2].X, v[j, 2].Y),
                    new(v[j, 3].X, v[j, 3].Y),
                };
                if (PolygonSignedArea(clip) < 0) clip.Reverse();
                var planeJ = PlaneFromTriangle(v[j, 0], v[j, 1], v[j, 2]);
                if (planeJ.IsDegenerate) continue;

                var overlap = SutherlandHodgman(subject, clip);
                PerPairDiagnostic?.Invoke(order[i], order[j], subject, clip, overlap);
                if (overlap.Count < 3) continue;
                if (PolygonSignedArea(overlap) <= 1e-8f) continue;

                // Sample depth at overlap vertices + centroid (centroid catches
                // mid-region violations when all vertices happen to lie on the
                // shared seam edge of two coplanar-along-edge faces).
                Vector2 cx = default;
                for (int k = 0; k < overlap.Count; k++) cx += overlap[k];
                cx /= overlap.Count;

                float maxFront = 0f;
                Vector2 maxAt = default;
                for (int k = 0; k <= overlap.Count; k++)
                {
                    var p = k < overlap.Count ? overlap[k] : cx;
                    if (!planeI.TryDepthAt(p, out float zi)) continue;
                    if (!planeJ.TryDepthAt(p, out float zj)) continue;
                    var delta = zi - zj; // > 0 means I is in front of J at p, painter order is wrong
                    if (delta > maxFront)
                    {
                        maxFront = delta;
                        maxAt = p;
                    }
                }

                if (maxFront > DepthEpsilon)
                {
                    var viol = new Violation(order[i], order[j], maxAt, maxFront);
                    if (worst is null || viol.DepthDelta > worst.Value.DepthDelta)
                        worst = viol;
                }
            }
        }

        return worst;
    }

    // ---------- 2D polygon helpers ----------

    private static float PolygonSignedArea(List<Vector2> poly)
    {
        if (poly.Count < 3) return 0f;
        float a = 0f;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return 0.5f * a;
    }

    /// <summary>
    /// Sutherland–Hodgman convex polygon clip. <paramref name="clip"/> must be
    /// convex and CCW. Returns a (possibly empty / degenerate) list of vertices.
    /// Exposed internal so that the diagnostic spot-check tests can call it
    /// directly to verify oracle behaviour on hand-traced inputs.
    ///
    /// <para>
    /// Robustness note: the inside-half-plane test uses a small <c>boundaryEps</c>
    /// tolerance so vertices that lie on (or float-precision-close to) a clip
    /// edge are treated as "inside". Without this, a vertex produced by an
    /// earlier iteration's <see cref="LineIntersect"/> may carry sub-ulp noise
    /// (e.g. <c>y = 0.7000005f</c> instead of <c>0.7f</c>) that pushes it
    /// over to the "outside" side on the next iteration's strict <c>&gt;= 0</c>
    /// test, triggering a degenerate parallel-edge intersection that emits a
    /// vertex far outside the actual overlap region. The tolerance closes
    /// that hole at the cost of including a strip of width <c>boundaryEps</c>
    /// of the bounding-box outside the strict overlap — which is harmless for
    /// our depth-evaluation use case (those points are still inside both
    /// faces' planes) and dramatically more robust against shared-edge inputs
    /// like the book's cover-meets-spine seam.
    /// </para>
    /// </summary>
    internal static List<Vector2> SutherlandHodgman(List<Vector2> subject, List<Vector2> clip)
    {
        const float boundaryEps = 1e-5f;
        var output = new List<Vector2>(subject);
        for (int i = 0; i < clip.Count && output.Count > 0; i++)
        {
            var input = output;
            output = new List<Vector2>(input.Count + 1);
            var a = clip[i];
            var b = clip[(i + 1) % clip.Count];
            // Inside half-plane: left of directed edge a→b (CCW means interior on left).
            for (int k = 0; k < input.Count; k++)
            {
                var p = input[(k - 1 + input.Count) % input.Count];
                var q = input[k];
                bool pIn = Cross(a, b, p) >= -boundaryEps;
                bool qIn = Cross(a, b, q) >= -boundaryEps;
                if (qIn)
                {
                    if (!pIn) output.Add(LineIntersect(p, q, a, b));
                    output.Add(q);
                }
                else if (pIn)
                {
                    output.Add(LineIntersect(p, q, a, b));
                }
            }
        }
        return output;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 p)
        => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static Vector2 LineIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        // Robust enough for our inputs (no parallel edges in practice — SH guarantees
        // we only call this when the segment crosses the half-plane).
        float a1 = p2.Y - p1.Y, b1 = p1.X - p2.X, c1 = a1 * p1.X + b1 * p1.Y;
        float a2 = p4.Y - p3.Y, b2 = p3.X - p4.X, c2 = a2 * p3.X + b2 * p3.Y;
        float det = a1 * b2 - a2 * b1;
        if (System.MathF.Abs(det) < 1e-12f) return p2; // degenerate; bias to the segment endpoint
        return new Vector2((b2 * c1 - b1 * c2) / det, (a1 * c2 - a2 * c1) / det);
    }

    // ---------- Plane helper ----------

    private readonly struct Plane
    {
        public readonly Vector3 Normal;
        public readonly float D;
        public readonly bool IsDegenerate;

        public Plane(Vector3 normal, float d, bool degenerate)
        {
            Normal = normal;
            D = d;
            IsDegenerate = degenerate;
        }

        /// <summary>
        /// Solve <c>n.X·x + n.Y·y + n.Z·z = D</c> for z given (x, y).
        /// Returns false when the plane is parallel to the view ray
        /// (<c>n.Z ≈ 0</c>, which means the face is edge-on and has zero
        /// projected area anyway — pair is safely skippable).
        /// </summary>
        public bool TryDepthAt(Vector2 xy, out float z)
        {
            if (System.MathF.Abs(Normal.Z) < 1e-6f) { z = 0f; return false; }
            z = (D - Normal.X * xy.X - Normal.Y * xy.Y) / Normal.Z;
            return true;
        }
    }

    private static Plane PlaneFromTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        var len = n.Length();
        if (len < 1e-8f) return new Plane(default, 0f, true);
        n /= len;
        return new Plane(n, Vector3.Dot(n, a), false);
    }
}
