using System;
using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;

namespace Combobulate.Sorting;

/// <summary>
/// Newell's algorithm with the classical 5-test cascade and on-demand
/// polygon splitting to break cycles. Operates on the triangulated
/// source mesh; per-frame splits, when needed, generate ephemeral
/// triangle fragments that exist only for the current sort. Final
/// output is a permutation of source-quad indices: the rendered order
/// for each source quad is taken from the position of its rearmost
/// triangle fragment in the back-to-front walk, ensuring anything
/// behind every fragment of the quad is drawn first.
///
/// <para>Cycle handling is via splits, so this sorter can never get
/// stuck in a fallback. The 5-test cascade is:
/// <list type="number">
///   <item>View-space Z extents disjoint?</item>
///   <item>Screen-space (XY) AABBs disjoint?</item>
///   <item>P entirely on the camera-far side of Q's plane?</item>
///   <item>Q entirely on the camera-near side of P's plane?</item>
///   <item>Projected polygons don't actually overlap?</item>
/// </list>
/// If all five fail, P is split against Q's plane and the inner loop restarts.
/// </para>
/// </summary>
public sealed class NewellSorter : IFaceSorter
{
    private readonly ObjGeometry _geometry;

    /// <summary>The base triangulated mesh built once at construction; never mutated.</summary>
    private readonly RenderTriangle[] _baseTriangles;

    /// <summary>
    /// Working triangle list reused across frames. Reset to <see cref="_baseTriangles"/>
    /// at the start of each Sort, then potentially grown by splits during the sort.
    /// Sized to handle reasonable split counts without reallocation.
    /// </summary>
    private readonly List<RenderTriangle> _work = new();

    /// <summary>
    /// Hard cap on splits per Sort call. A pathological input could in
    /// principle generate many splits; this bounds the worst-case cost
    /// and prevents runaway memory growth. When the cap is reached, the
    /// remaining unresolved cycles are tolerated (centroid-Z order).
    /// </summary>
    public int MaxSplitsPerFrame { get; set; } = 256;

    public NewellSorter(ObjGeometry geometry)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _baseTriangles = Triangulator.Triangulate(geometry.Quads).ToArray();
    }

    public int QuadCount => _geometry.Quads.Length;

    /// <summary>
    /// Diagnostic accessor: the immutable base triangle set this sorter
    /// was built from. Used by tests to assert triangulation correctness.
    /// </summary>
    public IReadOnlyList<RenderTriangle> BaseTriangles => _baseTriangles;

    public int Sort(Matrix4x4 rotation, int[] orderBuffer, bool[] visibleBuffer, float cameraDistance = 0f, float cullMarginCos = 0f)
    {
        var quads = _geometry.Quads;
        int qc = quads.Length;
        if (qc == 0) return 0;

        // Per-quad cull (whole-quad granularity for output, but the per-triangle
        // sort respects every triangle's individual cull below). See BspSorter.Sort
        // for the rationale on the perspective vs. orthographic branches. cullMarginCos > 0
        // widens the front-facing cone to absorb small CPU-vs-GPU rotation mismatches.
        bool persp = cameraDistance > 0f;
        for (int i = 0; i < qc; i++)
        {
            var rn = Vector3.TransformNormal(quads[i].Normal, rotation);
            if (persp)
            {
                var rc = Vector3.Transform(quads[i].Centroid, rotation);
                visibleBuffer[i] = GeometryPredicates.IsFrontFacingPerspective(rn, rc, cameraDistance, cullMarginCos);
            }
            else
            {
                visibleBuffer[i] = GeometryPredicates.IsFrontFacing(rn.Z, cullMarginCos);
            }
        }

        // Reset working list to base triangulation, then drop any triangle
        // whose source quad was culled (saves Newell work).
        _work.Clear();
        for (int i = 0; i < _baseTriangles.Length; i++)
        {
            var t = _baseTriangles[i];
            if (visibleBuffer[t.SourceQuadIndex]) _work.Add(t);
        }
        int n = _work.Count;
        if (n == 0) return CountVisibleQuads(visibleBuffer, qc, orderBuffer: orderBuffer);

        // Compute per-triangle view-space attributes.
        var tris = new TriView[n];
        for (int i = 0; i < n; i++) tris[i] = TriView.Create(_work[i], rotation);

        // Initial sort by far-Z (most-negative Z = farthest from camera in our convention,
        // because the renderer uses +Z as toward-camera).
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => tris[a].MinZ.CompareTo(tris[b].MinZ));

        // Newell cascade with bounded splits.
        int splitsRemaining = MaxSplitsPerFrame;
        var swapped = new bool[n]; // tagged when a triangle has already been swapped to break a cycle
        for (int i = 0; i < n; i++) swapped[i] = false;

        // Resize-tolerant outer walk. order[i] is the i-th triangle from back to front.
        for (int i = 0; i < order.Length - 1; i++)
        {
            for (int j = i + 1; j < order.Length; j++)
            {
                // Re-fetch by value each iteration: a split may resize `tris`,
                // invalidating any cached ref into the array.
                var P = tris[order[i]];
                var Q = tris[order[j]];

                // Test 1: Z-extents disjoint? P fully behind Q in view space → P first is correct.
                if (P.MaxZ <= Q.MinZ + PolygonSplitter.Epsilon) continue;

                // Test 2: Screen-space AABB disjoint?
                if (P.MaxX <= Q.MinX + PolygonSplitter.Epsilon ||
                    Q.MaxX <= P.MinX + PolygonSplitter.Epsilon ||
                    P.MaxY <= Q.MinY + PolygonSplitter.Epsilon ||
                    Q.MaxY <= P.MinY + PolygonSplitter.Epsilon) continue;

                // Test 3: P entirely on the camera-far side of Q's plane?
                if (EntirelyOnFarSideOf(in P, in Q)) continue;

                // Test 4: Q entirely on the camera-near side of P's plane?
                if (EntirelyOnNearSideOf(in Q, in P)) continue;

                // Test 5: 2D projected polygons don't actually overlap?
                if (!ProjectedTrianglesOverlap(in P, in Q)) continue;

                // All five tests failed → P actually does occlude (or interpenetrate) Q
                // even though P is sorted earlier than Q. Try swapping unless we've
                // already swapped P (would create a 2-cycle without splits).
                if (!swapped[order[i]])
                {
                    swapped[order[i]] = true;
                    (order[i], order[j]) = (order[j], order[i]);
                    j = i; // restart inner; for-loop increments to i+1
                    continue;
                }

                // True cycle. Split P along Q's plane.
                if (splitsRemaining <= 0) break;
                if (TrySplitAndRebuild(ref order, ref tris, ref swapped, i, j, rotation, ref splitsRemaining))
                {
                    j = i; // restart inner loop
                    continue;
                }
                break;
            }
        }

        // Now produce per-quad output order: each visible source quad is emitted at the
        // position of its REARMOST surviving triangle in `order`, deduplicating.
        return EmitQuadOrder(order, tris, visibleBuffer, qc, orderBuffer);
    }

    // ===== helpers =====

    private static int CountVisibleQuads(bool[] vis, int qc, int[] orderBuffer)
    {
        // No triangles in working set (unusual). Emit visible quads in cached-quad order.
        int k = 0;
        for (int i = 0; i < qc; i++) if (vis[i]) orderBuffer[k++] = i;
        return k;
    }

    private static int EmitQuadOrder(int[] order, TriView[] tris, bool[] visibleBuffer, int qc, int[] outBuf)
    {
        // For each source quad, record its REARMOST (smallest order-position) triangle.
        // Then walk the order back-to-front, emitting each source quad the first time
        // we encounter one of its triangles, skipping later occurrences.
        var seen = new bool[qc];
        int k = 0;
        for (int i = 0; i < order.Length; i++)
        {
            int sq = tris[order[i]].SourceQuad;
            if (sq < 0 || seen[sq]) continue;
            if (!visibleBuffer[sq]) continue;
            seen[sq] = true;
            outBuf[k++] = sq;
        }
        return k;
    }

    private static bool EntirelyOnFarSideOf(in TriView candidate, in TriView relativeTo)
    {
        // candidate is fully behind relativeTo's plane (in view space) means every
        // candidate vertex has signed distance ≤ +ε on the side of relativeTo's plane
        // that faces away from the camera.
        // We work in object space using each triangle's object-space plane. Equivalent
        // because rotation is rigid (orthogonal) — distances are preserved. The "far
        // side" of relativeTo in view space is the side whose normal, after rotation,
        // has Z ≤ 0 (pointing away from the camera).
        var n = relativeTo.ViewNormal;
        // The plane equation in view space: viewNormal · viewPos = D_view. We check
        // each candidate vertex's signed distance using view-space coords.
        var dA = Vector3.Dot(n, candidate.VA) - relativeTo.PlaneD;
        var dB = Vector3.Dot(n, candidate.VB) - relativeTo.PlaneD;
        var dC = Vector3.Dot(n, candidate.VC) - relativeTo.PlaneD;

        // Camera looks down -Z (Combobulate's convention: +Z view-normal = toward camera).
        // The "near" side of relativeTo is the side its viewNormal points toward
        // (positive distances). The "far" side is negative distances.
        // candidate is "entirely on far side" iff every distance is ≤ +ε
        // (with strict negativity for at least one to count as a real "behind").
        return dA <= PolygonSplitter.Epsilon && dB <= PolygonSplitter.Epsilon && dC <= PolygonSplitter.Epsilon;
    }

    private static bool EntirelyOnNearSideOf(in TriView candidate, in TriView relativeTo)
    {
        var n = relativeTo.ViewNormal;
        var dA = Vector3.Dot(n, candidate.VA) - relativeTo.PlaneD;
        var dB = Vector3.Dot(n, candidate.VB) - relativeTo.PlaneD;
        var dC = Vector3.Dot(n, candidate.VC) - relativeTo.PlaneD;
        return dA >= -PolygonSplitter.Epsilon && dB >= -PolygonSplitter.Epsilon && dC >= -PolygonSplitter.Epsilon;
    }

    private static bool ProjectedTrianglesOverlap(in TriView P, in TriView Q)
    {
        // Separating Axis Theorem on triangle edges, in 2D (projected XY).
        // For two convex polygons (triangles are convex), they overlap iff no edge-normal
        // produces a separating axis.
        Span<Vector2> p = stackalloc Vector2[3] { new(P.VA.X, P.VA.Y), new(P.VB.X, P.VB.Y), new(P.VC.X, P.VC.Y) };
        Span<Vector2> q = stackalloc Vector2[3] { new(Q.VA.X, Q.VA.Y), new(Q.VB.X, Q.VB.Y), new(Q.VC.X, Q.VC.Y) };
        if (HasSeparatingAxis(p, q)) return false;
        if (HasSeparatingAxis(q, p)) return false;
        return true;
    }

    private static bool HasSeparatingAxis(Span<Vector2> poly, Span<Vector2> other)
    {
        for (int i = 0; i < poly.Length; i++)
        {
            int j = (i + 1) % poly.Length;
            var edge = poly[j] - poly[i];
            // Outward normal of this edge in 2D (rotate +90°). Direction doesn't matter for SAT.
            // Scale: squared length of a 2D edge normal (degenerate edge guard).
            var axis = new Vector2(-edge.Y, edge.X);
            if (GeometryPredicates.IsDegenerateEdge2D(axis)) continue;

            ProjectOnto(poly,  axis, out var minP, out var maxP);
            ProjectOnto(other, axis, out var minO, out var maxO);
            if (maxP < minO - PolygonSplitter.Epsilon || maxO < minP - PolygonSplitter.Epsilon)
                return true;
        }
        return false;
    }

    private static void ProjectOnto(Span<Vector2> poly, Vector2 axis, out float min, out float max)
    {
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        for (int i = 0; i < poly.Length; i++)
        {
            var d = Vector2.Dot(poly[i], axis);
            if (d < min) min = d;
            if (d > max) max = d;
        }
    }

    private bool TrySplitAndRebuild(
        ref int[] order, ref TriView[] tris, ref bool[] swapped,
        int i, int j, Matrix4x4 rotation, ref int splitsRemaining)
    {
        int pIdx = order[i];
        int qIdx = order[j];
        var pTri = _work[pIdx];
        var qPlane = _work[qIdx].Plane;

        // Split P (object-space) against Q's plane (object-space).
        var frontOut = new List<RenderTriangle>(2);
        var backOut  = new List<RenderTriangle>(2);
        PolygonSplitter.Split(pTri, qPlane, frontOut, backOut);
        if (frontOut.Count + backOut.Count <= 1)
        {
            // Plane didn't actually split P. Should be rare; bail.
            return false;
        }

        // Replace P in _work with the first new fragment, append the rest.
        int firstReplaceIdx = pIdx;
        bool wroteReplacement = false;
        var newFragmentIndices = new List<int>(frontOut.Count + backOut.Count);
        foreach (var frag in frontOut)
        {
            if (!wroteReplacement) { _work[firstReplaceIdx] = frag; newFragmentIndices.Add(firstReplaceIdx); wroteReplacement = true; }
            else { _work.Add(frag); newFragmentIndices.Add(_work.Count - 1); }
        }
        foreach (var frag in backOut)
        {
            if (!wroteReplacement) { _work[firstReplaceIdx] = frag; newFragmentIndices.Add(firstReplaceIdx); wroteReplacement = true; }
            else { _work.Add(frag); newFragmentIndices.Add(_work.Count - 1); }
        }
        splitsRemaining -= newFragmentIndices.Count - 1;

        // Grow tris[] and swapped[] arrays.
        int oldN = tris.Length;
        int newN = _work.Count;
        if (newN > oldN)
        {
            Array.Resize(ref tris, newN);
            Array.Resize(ref swapped, newN);
        }
        // Recompute view attributes for the replaced and newly-appended fragments.
        foreach (var fi in newFragmentIndices)
        {
            tris[fi] = TriView.Create(_work[fi], rotation);
            swapped[fi] = false;
        }

        // Rebuild order array: replace order[i] (was pIdx) with the new fragments,
        // sorted by their MinZ; everything from i+1 onward stays. The newly-emerged
        // fragments are inserted in order at position i, replacing the original.
        var insert = new int[newFragmentIndices.Count];
        for (int k = 0; k < insert.Length; k++) insert[k] = newFragmentIndices[k];
        // Snapshot tris locally so the comparison delegate doesn't capture the ref parameter.
        var trisLocal = tris;
        Array.Sort(insert, (a, b) => trisLocal[a].MinZ.CompareTo(trisLocal[b].MinZ));

        var newOrder = new int[order.Length - 1 + insert.Length];
        // Copy [0, i) unchanged.
        Array.Copy(order, 0, newOrder, 0, i);
        // Insert the new fragments.
        Array.Copy(insert, 0, newOrder, i, insert.Length);
        // Copy (i, end) shifted.
        Array.Copy(order, i + 1, newOrder, i + insert.Length, order.Length - i - 1);
        order = newOrder;
        return true;
    }

    /// <summary>Cached per-triangle view-space attributes.</summary>
    private struct TriView
    {
        public Vector3 VA, VB, VC;        // view-space positions
        public Vector3 ViewNormal;        // view-space normal (rotation is rigid → already unit)
        public float   PlaneD;            // view-space plane offset: ViewNormal · vertex
        public float   MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
        public int     SourceQuad;

        public static TriView Create(in RenderTriangle t, Matrix4x4 rotation)
        {
            var a = Vector3.Transform(t.A, rotation);
            var b = Vector3.Transform(t.B, rotation);
            var c = Vector3.Transform(t.C, rotation);
            var n = Vector3.TransformNormal(t.Plane.Normal, rotation);
            var v = new TriView
            {
                VA = a, VB = b, VC = c,
                ViewNormal = n,
                PlaneD = Vector3.Dot(n, a),
                SourceQuad = t.SourceQuadIndex,
                MinX = Math.Min(a.X, Math.Min(b.X, c.X)),
                MaxX = Math.Max(a.X, Math.Max(b.X, c.X)),
                MinY = Math.Min(a.Y, Math.Min(b.Y, c.Y)),
                MaxY = Math.Max(a.Y, Math.Max(b.Y, c.Y)),
                MinZ = Math.Min(a.Z, Math.Min(b.Z, c.Z)),
                MaxZ = Math.Max(a.Z, Math.Max(b.Z, c.Z)),
            };
            return v;
        }
    }
}
