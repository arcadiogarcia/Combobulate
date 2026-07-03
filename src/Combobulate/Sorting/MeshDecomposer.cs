using System;
using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;

namespace Combobulate.Sorting;

/// <summary>
/// Pre-processes a polygon mesh into a refined fragment set where, for
/// <b>any</b> view direction (orthographic) or finite camera point
/// (perspective) outside the mesh, a single back-to-front ordering of
/// fragments is guaranteed to be a correct painter's-algorithm order.
///
/// <para><b>Why this is needed.</b> Combobulate paints faces with the
/// XAML compositor: each <see cref="CachedQuad"/> becomes one
/// <c>SpriteVisual</c>, and inter-sprite order is the only depth cue
/// available (there is no per-pixel z-buffer in WinUI Composition). The
/// per-frame painter sort (<see cref="BspSorter"/>, <see cref="NewellSorter"/>,
/// <see cref="TopologicalSorter"/>) emits one slot per source face — fine
/// for <b>convex</b> meshes (a closed convex polyhedron has no two faces
/// that mutually occlude), but ambiguous for <b>non-convex</b> meshes
/// where two faces can each occlude a different pixel-sub-region of the
/// other. A book's cover overhanging the inset pages-block is the
/// canonical case: at some yaw the cover is in front of the page-edge
/// strip, at adjacent pixels of the same frame the page-edge strip is
/// in front of the cover. No single per-face order is correct.</para>
///
/// <para><b>What this does.</b> Splits every source face against every
/// <em>distinct</em> source plane it crosses (after plane de-duplication),
/// using a <b>quad-preserving</b> pipeline that emits triangle fragments
/// only when forced by the cut geometry. After the algorithm:
/// <list type="bullet">
///   <item>Every output fragment lies entirely on the <em>same side</em>
///         (or coplanar) of every other source plane.</item>
///   <item>Consequently, for any pair of non-coplanar fragments, the
///         "in front of" relation is determined per-pair by which side
///         of each other's plane the camera sits on — and is consistent
///         across all overlapping screen pixels (the side test does not
///         depend on which pixel).</item>
///   <item>The induced pairwise relation is acyclic
///         (classical BSP-correctness theorem; see Foley &amp; van Dam
///         Ch. 13, or Schumacker et al. 1969). Therefore any
///         topological sort of fragments is a correct painter order.</item>
/// </list>
/// </para>
///
/// <para><b>Quad preservation.</b> A plane cut against a convex polygon
/// is classified by which polygon features the chord intersects:
/// <list type="bullet">
///   <item><b>Case A — opposite-edge cut:</b> a 4-vertex quad splits into
///         two 4-vertex quads. Common for axis-aligned meshes where every
///         cut is parallel to two of the input's edges (cubes, books,
///         architecture).</item>
///   <item><b>Case B — adjacent-edge cut:</b> a 4-vertex quad splits into
///         a 3-vertex triangle and a 5-vertex pentagon. Pentagons may be
///         re-split into quads + triangles by later cuts; if any survive
///         to emission they are fan-triangulated.</item>
///   <item><b>Case C — edge-to-vertex cut:</b> 1 triangle + 1 quad.</item>
///   <item><b>Case D — vertex-to-vertex (diagonal) cut:</b> 2 triangles.</item>
///   <item><b>Case E — chord runs along an existing edge:</b> classified
///         as no-cross, no split.</item>
/// </list>
/// Real-world impact: on book.obj (9 source quads, 6 distinct planes,
/// covers overhanging pages in 3 axes) the quad-first pipeline emits ~33
/// quad fragments where the previous triangle-only pipeline emitted ~69
/// triangle fragments. The win compounds because the renderer's quad
/// path uses a 4-corner affine brush (no triangle clip + no 3-point
/// affine singularities) and the bake's per-cell predicate length scales
/// as <c>O(N²)</c> in fragment count.</para>
///
/// <para><b>Pre-validation.</b> Source quads whose four vertices are not
/// coplanar within <see cref="DecomposerOptions.CoplanarDistanceThreshold"/>,
/// or whose ring is not convex, are eagerly split into two canonical
/// (V0–V1–V2, V0–V2–V3) triangles before entering the working set. The
/// painter-correctness guarantee assumes each input face is a single
/// planar convex polygon; non-conforming input is rendered safely as
/// triangles even though it cannot preserve quadness.</para>
///
/// <para><b>What this preserves.</b> Per-fragment material name, UV
/// coordinates (linearly interpolated along cut edges), outward normal,
/// fallback colour, and HasExplicitUv flag are all inherited from the
/// source quad. Each fragment carries the source quad's SourceIndex so
/// material lookups continue to work; the renderer treats fragments as
/// independent slots in <see cref="ObjGeometry.Quads"/>.</para>
///
/// <para><b>What this does not preserve.</b> Splits never introduce
/// Steiner vertices on un-cut edges — that would create T-junctions with
/// the corresponding edges of neighbouring faces, which the compositor
/// rasterises as sub-pixel cracks. A face whose chord enters/exits
/// adjacent edges therefore emits at least one triangle: pure quadness
/// is recoverable only for Case-A cuts (and the convexity-preserving
/// chain of Case-A cuts that compose into a grid).</para>
///
/// <para><b>Complexity.</b> O(P · F) where P is the number of distinct
/// source planes and F is the worst-case fragment count. F is bounded
/// above by <c>InputCount · MaxFragmentsPerSourceQuad</c>. In practice,
/// for axis-aligned shapes and modest non-convexity, F grows by a small
/// constant factor — a cube produces 0 extra fragments (every face lies
/// cleanly on one side of every other face's plane); book.obj produces
/// 33 fragments (all quads) from 9 source quads.
/// <see cref="DecomposerOptions.MaxFragmentsPerSourceQuad"/> caps the
/// worst case for pathological inputs.</para>
///
/// <para><b>Convex meshes.</b> By construction, no face of a closed
/// convex polyhedron straddles another face's plane (the whole
/// polyhedron lies on one side of each face's plane). Convex inputs
/// pass through unchanged in fragment count — quads stay quads.</para>
///
/// <para><b>Plane ordering.</b> Splitter planes are processed in
/// axis-aligned-first order: planes whose normal is parallel (within
/// <see cref="DecomposerOptions.CoplanarCosThreshold"/>) to a world
/// axis come first. The final fragment set is identical regardless of
/// plane order (the BSP-partition property is commutative); ordering
/// only affects intermediate polygon shapes during the sweep, and
/// processing axis-aligned planes first maximises the chance that each
/// successive cut is a Case-A opposite-edge split (which preserves
/// quadness through long cut chains).</para>
///
/// <para><b>Coplanar inputs.</b> A face is considered "coplanar" with
/// the splitter plane when its normal is parallel (within <see cref="DecomposerOptions.CoplanarCosThreshold"/>)
/// AND every vertex's signed distance from the plane is within
/// <see cref="DecomposerOptions.CoplanarDistanceThreshold"/>. Coplanar
/// faces are passed through unchanged — even if they have opposite
/// winding, since the per-fragment painter order is determined by other
/// planes and back-face cull resolves the duplicate inside the
/// renderer.</para>
///
/// <para><b>Coplanar grouping.</b> Use
/// <see cref="ComputeCoplanarGroups"/> on the output to obtain a
/// per-fragment plane-equivalence-class index. Two fragments sharing a
/// group never occlude each other from any view angle (they live on the
/// same plane and were created by disjoint splits), so downstream
/// painter-order baking can omit the runtime depth test for them. This
/// cuts the quadratic per-cell predicate length significantly on
/// meshes whose decomposition produces many sub-pieces per face.</para>
///
/// <para><b>Determinism.</b> Output ordering is deterministic and
/// depends only on the input order and options — the algorithm uses no
/// randomness. The same input always produces the same fragment list in
/// the same order, which is important for cache stability across
/// rebuilds.</para>
/// </summary>
public static class MeshDecomposer
{
    /// <summary>
    /// Per-call tuning for the decomposer. Defaults match Combobulate's
    /// production usage: aggressive coplanar dedup, generous safety cap.
    /// </summary>
    public readonly struct DecomposerOptions
    {
        /// <summary>
        /// Dot-product threshold for treating two plane normals as parallel
        /// (either same or opposite direction). Default ≈ cos(2.56°).
        /// Two planes are considered coplanar when <c>|dot(n1, n2)| ≥
        /// CoplanarCosThreshold</c> AND every vertex of one lies within
        /// <see cref="CoplanarDistanceThreshold"/> of the other.
        /// </summary>
        public float CoplanarCosThreshold { get; init; }

        /// <summary>
        /// Signed-distance threshold for treating a vertex as lying on a
        /// plane. Re-uses the central distance scale; lifting this above
        /// <see cref="GeometryPredicates.DistanceEpsilon"/> trades aggressive
        /// fragment dedup for the risk of merging near-but-not-coplanar
        /// faces.
        /// </summary>
        public float CoplanarDistanceThreshold { get; init; }

        /// <summary>
        /// Hard upper bound on the total fragment count, expressed as a
        /// multiplier over the input quad count. If the decomposer would
        /// produce more fragments than <c>InputCount · MaxFragmentsPerSourceQuad</c>,
        /// it stops splitting (the partially-split output is returned as
        /// the final result). Use to protect against pathological inputs
        /// — e.g. randomly oriented triangles where every pair straddles
        /// every other's plane.
        /// </summary>
        public int MaxFragmentsPerSourceQuad { get; init; }

        /// <summary>
        /// Minimum acceptable area (in model-space units²) of an output
        /// triangle fragment. Splits inevitably produce slivers whose two
        /// edges are nearly parallel; rendering them is wasteful (sub-pixel
        /// sprite) and risks numerical instability in the renderer's
        /// basis-vector computation. Fragments below this area are dropped
        /// from the output. Their absence is harmless: a triangle with
        /// area below model-space-pixel² does not occlude any pixel after
        /// projection, so leaving it out cannot violate painter
        /// correctness against the remaining fragments.
        /// <para>Default <c>0</c> (no area-based culling — only true
        /// degenerates with zero cross are dropped). Set to a small
        /// positive value proportional to your model's expected feature
        /// size for additional protection against rendering artefacts.
        /// The Combobulate renderer itself also guards
        /// <c>Normalize(Cross)</c> against zero-length crosses, so a
        /// MinFragmentArea of 0 is safe by default.</para>
        /// </summary>
        public float MinFragmentArea { get; init; }

        /// <summary>
        /// Minimum acceptable area (in UV-space units²) of an output
        /// triangle fragment's UV triangle. This guards against fragments
        /// whose 3D area is non-trivial but whose three UV corners are
        /// near-collinear (or two of them near-coincident). Such fragments
        /// produce a near-singular 3-point brush affine in
        /// <see cref="Combobulate.Caching.BrushTransformMath.BuildTriangleAffine"/>,
        /// which collapses
        /// <see cref="Microsoft.UI.Composition.CompositionSurfaceBrush.TransformMatrix"/>
        /// and renders the sprite invisible.
        ///
        /// <para>How they arise: subdivision cuts a parent triangle along
        /// a plane whose intersection with the parent's UV gradient is
        /// nearly parallel to one of the triangle's UV edges. The cut
        /// point's interpolated UV ends up coincident (within float
        /// precision) with an existing vertex's UV even though the cut
        /// point's 3D position is distinct. The resulting fragment has
        /// large screen-space basis vectors (the sprite gets a 100+ px
        /// Size) but the actual triangle is a thin sliver whose visible
        /// area is sub-pixel.</para>
        ///
        /// <para>Painter-safety: skipping these slivers is safe because
        /// their 3D triangle covers only a few pixels (the actual
        /// triangle is collapsed; the bounding box that the renderer
        /// uses for sprite Size is the |V1-V0|×|V2-V0| parallelogram,
        /// which can be large even when the triangle itself is
        /// collapsed). Adjacent non-degenerate fragments cover the same
        /// pixels correctly.</para>
        ///
        /// <para>Default <c>1e-6</c> (UV area below ≈ one texel of a
        /// 1024×1024 texture). Set higher to be more aggressive about
        /// dropping fragments whose texture mapping is poorly conditioned,
        /// or to <c>0</c> to disable UV-area culling entirely.</para>
        /// </summary>
        public float MinUvFragmentArea { get; init; }

        /// <summary>Default options for production use.</summary>
        public static DecomposerOptions Default => new()
        {
            CoplanarCosThreshold = 0.999f,                          // ≈ cos(2.56°)
            CoplanarDistanceThreshold = GeometryPredicates.DistanceEpsilon,
            MaxFragmentsPerSourceQuad = 64,
            MinFragmentArea = 0f,
            MinUvFragmentArea = 1e-3f,
        };
    }

    /// <summary>
    /// Returns true iff the input set requires no subdivision — every
    /// face is convex with the rest of the mesh (no face straddles
    /// another face's plane). Useful as an early-exit fast path and for
    /// diagnostics.
    /// </summary>
    public static bool IsAlreadyPainterReady(IReadOnlyList<CachedQuad> sourceQuads, DecomposerOptions? options = null)
    {
        if (sourceQuads == null || sourceQuads.Count <= 1) return true;
        var opts = options ?? DecomposerOptions.Default;

        var triangles = Triangulator.Triangulate(sourceQuads);
        var planes = BuildDedupedPlanes(sourceQuads, opts);
        for (int p = 0; p < planes.Count; p++)
        {
            var plane = planes[p].Plane;
            for (int t = 0; t < triangles.Count; t++)
            {
                if (AreCoplanar(triangles[t].Plane, plane, opts)) continue;
                if (PolygonSplitter.ClassifyTriangle(triangles[t], plane).Side == PlaneSide.Spanning)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Decomposes <paramref name="sourceQuads"/> into a fragment list
    /// suitable for per-fragment painter ordering, preserving "quadness"
    /// wherever the cut geometry allows. See the class-level remarks for
    /// what is preserved and what is not.
    /// </summary>
    /// <param name="sourceQuads">Input faces. Both quad-shaped and triangle-shaped
    /// <see cref="CachedQuad"/>s are accepted. Output is a mix of quad and triangle
    /// CachedQuads (see <see cref="CachedQuad.IsTriangle"/>).</param>
    /// <param name="options">Tuning options. Pass <c>null</c> to use defaults.</param>
    /// <returns>
    /// New list of fragment <see cref="CachedQuad"/>s. The returned list
    /// is owned by the caller; the input is not modified. The output may
    /// equal the input (in spirit — same shape, no extra splits) when
    /// the mesh is already painter-ready (e.g. any convex closed
    /// polyhedron).
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sourceQuads"/> is null.</exception>
    public static List<CachedQuad> DecomposeForPainterOrder(
        IReadOnlyList<CachedQuad> sourceQuads,
        DecomposerOptions? options = null)
    {
        if (sourceQuads == null) throw new ArgumentNullException(nameof(sourceQuads));
        var opts = options ?? DecomposerOptions.Default;
        if (opts.MaxFragmentsPerSourceQuad <= 0)
            throw new ArgumentException("MaxFragmentsPerSourceQuad must be positive.", nameof(options));

        var result = new List<CachedQuad>();
        if (sourceQuads.Count == 0) return result;

        // Step 1: build the working set of PolygonFragments from the input
        // CachedQuads. Source quads that are planar AND convex remain as
        // 4-vertex polygons (the canonical case — the quad-preserving win
        // arises here). Source quads that fail planarity/convexity (or
        // are pre-triangulated) become triangle fragments, since those
        // cannot be kept as a single convex polygon for clipping.
        var current = new List<PolygonFragment>(sourceQuads.Count);
        for (int i = 0; i < sourceQuads.Count; i++)
        {
            BuildInitialFragments(sourceQuads[i], i, opts, current);
        }
        if (current.Count == 0) return result;

        // Step 2: collect the distinct splitting planes drawn from the
        // INPUT quad set (each face contributes exactly one plane even
        // when later sub-triangulated). Coplanar duplicates (e.g.
        // opposite-winding inward faces) are merged so the same plane is
        // not used twice.
        var planes = BuildDedupedPlanes(sourceQuads, opts);

        // Step 3: order planes axis-aligned-first. The final fragment set
        // is identical regardless of order (BSP partitions commute), but
        // ordering only affects intermediate polygon shapes during the
        // sweep — processing axis-aligned planes first maximises the
        // chance that each successive cut is a Case-A opposite-edge
        // split, which preserves quadness through long cut chains.
        SortPlanesAxisAlignedFirst(planes);

        // Step 4: sweep. For each plane, partition every current polygon
        // (unless it is coplanar with the splitter, in which case the
        // splitter is a no-op for that polygon).
        long capTotal = checked((long)sourceQuads.Count * opts.MaxFragmentsPerSourceQuad);
        for (int p = 0; p < planes.Count; p++)
        {
            var plane = planes[p].Plane;
            var next = new List<PolygonFragment>(current.Count + 8);
            for (int f = 0; f < current.Count; f++)
            {
                var poly = current[f];
                // Coplanar fragments are unaffected by this splitter.
                if (AreCoplanar(poly.Plane, plane, opts))
                {
                    next.Add(poly);
                    continue;
                }
                var outcome = PolygonClipper.Split(poly, plane, opts.CoplanarDistanceThreshold,
                    out var frontFrag, out var backFrag);
                switch (outcome)
                {
                    case PolygonClipper.SplitOutcome.AllFront:
                    case PolygonClipper.SplitOutcome.AllBack:
                    case PolygonClipper.SplitOutcome.OnPlane:
                        next.Add(poly);
                        break;
                    case PolygonClipper.SplitOutcome.Split:
                        next.Add(frontFrag);
                        next.Add(backFrag);
                        break;
                    case PolygonClipper.SplitOutcome.Degenerate:
                        // Drop: an empty input has no contribution.
                        break;
                }
            }
            current = next;
            if (current.Count > capTotal)
            {
                // Hit the cap — accept the partial output. Painter-correctness
                // is not guaranteed for the un-split remainder, but the
                // output is still a valid fragment list (each fragment is a
                // planar polygon).
                break;
            }
        }

        // Step 5: emit. 3-vertex polygons become triangle CachedQuads;
        // 4-vertex polygons stay as quad CachedQuads; 5+-vertex polygons
        // are fan-triangulated from V0. Per-fragment material name,
        // normal, fallback colour, and HasExplicitUv are inherited from
        // the parent source quad.
        float minAreaSq = opts.MinFragmentArea * opts.MinFragmentArea * 4f; // |Cross|² == 4·area²
        result.Capacity = current.Count;
        for (int i = 0; i < current.Count; i++)
        {
            EmitFragment(current[i], sourceQuads, opts, minAreaSq, result);
        }
        return result;
    }

    /// <summary>
    /// Groups output fragments into plane-equivalence classes: two
    /// fragments share a group iff they lie on the same plane (within
    /// <see cref="DecomposerOptions.CoplanarCosThreshold"/> and
    /// <see cref="DecomposerOptions.CoplanarDistanceThreshold"/>).
    ///
    /// <para>The result is an int array of length <c>fragments.Count</c>
    /// where each element is the 0-based group index. Groups are numbered
    /// in the order they are first encountered. Fragments in the same
    /// group never occlude each other from any view angle, so downstream
    /// painter-order baking can skip their pairwise depth tests.</para>
    /// </summary>
    public static int[] ComputeCoplanarGroups(IReadOnlyList<CachedQuad> fragments, DecomposerOptions? options = null)
    {
        if (fragments == null) throw new ArgumentNullException(nameof(fragments));
        var opts = options ?? DecomposerOptions.Default;
        var groups = new int[fragments.Count];
        // Anchor planes: representative plane + an in-plane vertex for
        // each group encountered so far. Linear scan per fragment is
        // O(N · G); G is bounded by the number of distinct source planes.
        var anchorPlanes = new List<Plane3>();
        var anchorPoints = new List<Vector3>();
        for (int i = 0; i < fragments.Count; i++)
        {
            var q = fragments[i];
            // Degenerate (zero-normal) fragments don't have a meaningful
            // plane to compare against — assign them to their own group.
            if (q.Normal.LengthSquared() < GeometryPredicates.DivisorEpsilon)
            {
                groups[i] = anchorPlanes.Count;
                anchorPlanes.Add(default);
                anchorPoints.Add(q.Centroid);
                continue;
            }
            var plane = new Plane3(q.Normal, Vector3.Dot(q.Normal, q.Centroid));
            int found = -1;
            for (int j = 0; j < anchorPlanes.Count; j++)
            {
                if (PlaneEquivalent(anchorPlanes[j], anchorPoints[j], plane, q.Centroid, opts))
                {
                    found = j;
                    break;
                }
            }
            if (found < 0)
            {
                found = anchorPlanes.Count;
                anchorPlanes.Add(plane);
                anchorPoints.Add(q.Centroid);
            }
            groups[i] = found;
        }
        return groups;
    }

    // ---------- emission ----------

    private static void BuildInitialFragments(
        CachedQuad q, int sourceQuadIndex, DecomposerOptions opts, List<PolygonFragment> dest)
    {
        // Triangle source faces are already triangles — emit a single
        // 3-vertex fragment. V3==V2 by convention.
        if (q.IsTriangle)
        {
            var crossT = Vector3.Cross(q.V1 - q.V0, q.V2 - q.V0);
            if (crossT.LengthSquared() < GeometryPredicates.DivisorEpsilon) return;
            var triPlane = Plane3.FromTriangle(q.V0, q.V1, q.V2);
            dest.Add(new PolygonFragment(
                new[] { q.V0, q.V1, q.V2 },
                new[] { q.Uv0, q.Uv1, q.Uv2 },
                sourceQuadIndex, triPlane));
            return;
        }

        // Quad source face. Validate planarity + convexity. If either
        // fails, fall back to two canonical triangles — quadness cannot
        // be preserved through the decomposer for ill-formed input.
        if (IsPlanarConvexQuad(q, opts, out var quadPlane))
        {
            dest.Add(new PolygonFragment(
                new[] { q.V0, q.V1, q.V2, q.V3 },
                new[] { q.Uv0, q.Uv1, q.Uv2, q.Uv3 },
                sourceQuadIndex, quadPlane));
            return;
        }

        // Fallback triangulation: V0-V1-V2 + V0-V2-V3, matching Triangulator.
        var cross1 = Vector3.Cross(q.V1 - q.V0, q.V2 - q.V0);
        if (cross1.LengthSquared() >= GeometryPredicates.DivisorEpsilon)
        {
            dest.Add(new PolygonFragment(
                new[] { q.V0, q.V1, q.V2 },
                new[] { q.Uv0, q.Uv1, q.Uv2 },
                sourceQuadIndex,
                Plane3.FromTriangle(q.V0, q.V1, q.V2)));
        }
        var cross2 = Vector3.Cross(q.V2 - q.V0, q.V3 - q.V0);
        if (cross2.LengthSquared() >= GeometryPredicates.DivisorEpsilon)
        {
            dest.Add(new PolygonFragment(
                new[] { q.V0, q.V2, q.V3 },
                new[] { q.Uv0, q.Uv2, q.Uv3 },
                sourceQuadIndex,
                Plane3.FromTriangle(q.V0, q.V2, q.V3)));
        }
    }

    /// <summary>
    /// Checks that a source quad's four vertices are coplanar within
    /// <see cref="DecomposerOptions.CoplanarDistanceThreshold"/> AND form
    /// a convex CCW polygon (all four edge-pair crosses have the same
    /// sign relative to the face normal). Outputs the canonical plane on
    /// success.
    /// </summary>
    private static bool IsPlanarConvexQuad(CachedQuad q, DecomposerOptions opts, out Plane3 plane)
    {
        plane = default;
        var e01 = q.V1 - q.V0;
        var e03 = q.V3 - q.V0;
        var crossN = Vector3.Cross(e01, e03);
        var lenSq = crossN.LengthSquared();
        if (lenSq < GeometryPredicates.DivisorEpsilon) return false;
        var normal = crossN / MathF.Sqrt(lenSq);

        // Planarity: V2's distance from plane(V0, V1, V3) within epsilon.
        var d = Vector3.Dot(normal, q.V0);
        var dist2 = Vector3.Dot(normal, q.V2) - d;
        if (MathF.Abs(dist2) > opts.CoplanarDistanceThreshold) return false;

        // Convexity: all four edge-crosses point along the same side of
        // the normal. For CCW input, every (E_i × E_{i+1}) · N is positive.
        // For CW input, every dot is negative. We accept either as long as
        // the sign is consistent.
        var edges = new Vector3[]
        {
            q.V1 - q.V0,
            q.V2 - q.V1,
            q.V3 - q.V2,
            q.V0 - q.V3,
        };
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = edges[i];
            var b = edges[(i + 1) % 4];
            var cr = Vector3.Cross(a, b);
            var dot = Vector3.Dot(cr, normal);
            int s = dot > GeometryPredicates.DivisorEpsilon ? 1
                  : dot < -GeometryPredicates.DivisorEpsilon ? -1 : 0;
            if (s == 0) return false; // collinear edges → degenerate corner
            if (sign == 0) sign = s;
            else if (sign != s) return false; // sign flip → concave
        }

        plane = new Plane3(normal, d);
        return true;
    }

    private static void EmitFragment(
        PolygonFragment poly,
        IReadOnlyList<CachedQuad> sourceQuads,
        DecomposerOptions opts,
        float minAreaSq,
        List<CachedQuad> dest)
    {
        if (poly.Count < 3) return;
        var parent = sourceQuads[poly.SourceQuadIndex];

        // Quad emission: emit as a single CachedQuad if 4 vertices and
        // the parallelogram cross is non-degenerate. UV sliver guard
        // protects against poorly-conditioned brush transforms.
        if (poly.Count == 4)
        {
            var v0 = poly.Vertices[0];
            var v1 = poly.Vertices[1];
            var v2 = poly.Vertices[2];
            var v3 = poly.Vertices[3];
            if (!IsFinite(v0) || !IsFinite(v1) || !IsFinite(v2) || !IsFinite(v3)) return;
            var uv0 = poly.Uvs[0];
            var uv1 = poly.Uvs[1];
            var uv2 = poly.Uvs[2];
            var uv3 = poly.Uvs[3];
            if (!IsFinite(uv0) || !IsFinite(uv1) || !IsFinite(uv2) || !IsFinite(uv3)) return;

            // Total area = |cross(V1-V0, V2-V0)|/2 + |cross(V0-V2, V3-V2)|/2
            // Use either parallelogram cross as the sliver-test scale; a
            // CCW quad has both crosses pointing the same direction.
            var crossA = Vector3.Cross(v1 - v0, v3 - v0);
            if (crossA.LengthSquared() < minAreaSq) return;

            // No UV sliver guard for quads. The renderer's quad brush path
            // (BrushTransformMath.BuildQuadAxisAlignedCrop) floors the
            // UV width/height at 1e-6 so a near-collinear UV rectangle
            // cannot produce a degenerate Composition transform. Skipping
            // the guard preserves small but visible cover-corner sub-quads
            // that the cover-overhang case generates (UV area ~1e-3 per
            // sub-quad on book-like meshes).

            var centroid = (v0 + v1 + v2 + v3) * 0.25f;
            dest.Add(new CachedQuad(
                sourceIndex: parent.SourceIndex,
                v0: v0, v1: v1, v2: v2, v3: v3,
                centroid: centroid,
                normal: parent.Normal,
                fallbackColor: parent.FallbackColor,
                materialName: parent.MaterialName,
                uv0: uv0, uv1: uv1, uv2: uv2, uv3: uv3,
                isTriangle: false,
                hasExplicitUv: parent.HasExplicitUv));
            return;
        }

        // Triangle emission for 3-vertex polygons.
        if (poly.Count == 3)
        {
            EmitTriangle(poly.Vertices[0], poly.Vertices[1], poly.Vertices[2],
                poly.Uvs[0], poly.Uvs[1], poly.Uvs[2],
                parent, opts, minAreaSq, dest);
            return;
        }

        // 5+ vertices: fan-triangulate from V0. Each emitted triangle
        // inherits material/normal from the parent and is checked against
        // sliver thresholds in EmitTriangle.
        for (int i = 1; i < poly.Count - 1; i++)
        {
            EmitTriangle(poly.Vertices[0], poly.Vertices[i], poly.Vertices[i + 1],
                poly.Uvs[0], poly.Uvs[i], poly.Uvs[i + 1],
                parent, opts, minAreaSq, dest);
        }
    }

    private static void EmitTriangle(
        Vector3 a, Vector3 b, Vector3 c,
        Vector2 uvA, Vector2 uvB, Vector2 uvC,
        CachedQuad parent,
        DecomposerOptions opts,
        float minAreaSq,
        List<CachedQuad> dest)
    {
        if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c)) return;
        if (!IsFinite(uvA) || !IsFinite(uvB) || !IsFinite(uvC)) return;
        var crossVec = Vector3.Cross(b - a, c - a);
        if (crossVec.LengthSquared() < minAreaSq) return;
        float uvCross = (uvB.X - uvA.X) * (uvC.Y - uvA.Y)
                      - (uvB.Y - uvA.Y) * (uvC.X - uvA.X);
        if (MathF.Abs(uvCross) < 2f * opts.MinUvFragmentArea) return;
        dest.Add(CachedQuad.Triangle(
            sourceIndex: parent.SourceIndex,
            v0: a, v1: b, v2: c,
            normal: parent.Normal,
            fallbackColor: parent.FallbackColor,
            materialName: parent.MaterialName,
            uv0: uvA, uv1: uvB, uv2: uvC,
            hasExplicitUv: parent.HasExplicitUv));
    }

    /// <summary>
    /// Sorts the splitter plane list so axis-aligned planes (normal
    /// parallel to ±X, ±Y, or ±Z within the coplanar threshold) come
    /// first, then the remaining planes. The internal ordering of each
    /// bucket is preserved (stable sort) so deterministic behaviour is
    /// kept for callers depending on input order.
    /// </summary>
    private static void SortPlanesAxisAlignedFirst(List<CandidatePlane> planes)
    {
        // Build a parallel key array, then stable-sort by key=0 first.
        if (planes.Count <= 1) return;
        var axisAligned = new List<CandidatePlane>(planes.Count);
        var rest = new List<CandidatePlane>(planes.Count);
        for (int i = 0; i < planes.Count; i++)
        {
            if (IsAxisAligned(planes[i].Plane.Normal))
                axisAligned.Add(planes[i]);
            else
                rest.Add(planes[i]);
        }
        planes.Clear();
        planes.AddRange(axisAligned);
        planes.AddRange(rest);
    }

    private static bool IsAxisAligned(Vector3 n)
    {
        // Normal n is unit length. Axis-aligned iff one component has
        // magnitude ≥ AxisAlignedThreshold and the other two are tiny.
        // 0.999 ≈ cos(2.56°), matching the coplanar threshold.
        const float Thresh = 0.999f;
        return MathF.Abs(n.X) >= Thresh
            || MathF.Abs(n.Y) >= Thresh
            || MathF.Abs(n.Z) >= Thresh;
    }

    // ---------- internals ----------

    private readonly struct CandidatePlane
    {
        public CandidatePlane(Plane3 plane, Vector3 anchorPoint)
        {
            Plane = plane;
            AnchorPoint = anchorPoint;
        }
        /// <summary>The plane itself (normal + D).</summary>
        public Plane3 Plane { get; }
        /// <summary>A point known to lie on the plane (used for coplanarity tests against later candidates).</summary>
        public Vector3 AnchorPoint { get; }
    }

    private static List<CandidatePlane> BuildDedupedPlanes(IReadOnlyList<CachedQuad> sourceQuads, DecomposerOptions opts)
    {
        var planes = new List<CandidatePlane>(sourceQuads.Count);
        for (int i = 0; i < sourceQuads.Count; i++)
        {
            var q = sourceQuads[i];
            // Skip degenerate (zero-normal) source quads — they don't
            // contribute a meaningful partitioning plane.
            if (q.Normal.LengthSquared() < GeometryPredicates.DivisorEpsilon) continue;
            var plane = new Plane3(q.Normal, Vector3.Dot(q.Normal, q.Centroid));
            // Linear-scan dedup. n is typically small (≤ a few dozen).
            bool dup = false;
            for (int j = 0; j < planes.Count; j++)
            {
                if (ArePlanesEquivalent(planes[j], plane, q.Centroid, opts))
                {
                    dup = true;
                    break;
                }
            }
            if (!dup) planes.Add(new CandidatePlane(plane, q.Centroid));
        }
        return planes;
    }

    private static bool ArePlanesEquivalent(CandidatePlane existing, Plane3 candidate, Vector3 candidateAnchor, DecomposerOptions opts)
    {
        var dot = Vector3.Dot(existing.Plane.Normal, candidate.Normal);
        if (MathF.Abs(dot) < opts.CoplanarCosThreshold) return false;
        // Normals are parallel. Check vertex-vs-plane distance both ways
        // — symmetric so the test doesn't depend on which plane we treat
        // as the reference.
        if (MathF.Abs(existing.Plane.SignedDistance(candidateAnchor)) > opts.CoplanarDistanceThreshold) return false;
        if (MathF.Abs(candidate.SignedDistance(existing.AnchorPoint)) > opts.CoplanarDistanceThreshold) return false;
        return true;
    }

    /// <summary>
    /// Anchor-point variant of <see cref="ArePlanesEquivalent"/> used by
    /// <see cref="ComputeCoplanarGroups"/>. Treats two planes (each with
    /// an in-plane anchor) as equivalent iff their normals are parallel
    /// within <see cref="DecomposerOptions.CoplanarCosThreshold"/> AND
    /// each plane's anchor lies within
    /// <see cref="DecomposerOptions.CoplanarDistanceThreshold"/> of the
    /// other plane.
    /// </summary>
    private static bool PlaneEquivalent(Plane3 a, Vector3 aAnchor, Plane3 b, Vector3 bAnchor, DecomposerOptions opts)
    {
        var dot = Vector3.Dot(a.Normal, b.Normal);
        if (MathF.Abs(dot) < opts.CoplanarCosThreshold) return false;
        if (MathF.Abs(a.SignedDistance(bAnchor)) > opts.CoplanarDistanceThreshold) return false;
        if (MathF.Abs(b.SignedDistance(aAnchor)) > opts.CoplanarDistanceThreshold) return false;
        return true;
    }

    private static bool AreCoplanar(Plane3 a, Plane3 b, DecomposerOptions opts)
    {
        var dot = Vector3.Dot(a.Normal, b.Normal);
        if (MathF.Abs(dot) < opts.CoplanarCosThreshold) return false;
        // For two unit-normal planes that are parallel, equivalence
        // reduces to <c>|D1| == |D2|</c> with the sign flipped iff the
        // normals oppose. Combine into <c>|D1 − sign(dot)·D2| < eps</c>.
        var dDiff = a.D - MathF.Sign(dot) * b.D;
        return MathF.Abs(dDiff) <= opts.CoplanarDistanceThreshold;
    }

    private static bool IsFinite(Vector3 v) =>
        !float.IsNaN(v.X) && !float.IsNaN(v.Y) && !float.IsNaN(v.Z) &&
        !float.IsInfinity(v.X) && !float.IsInfinity(v.Y) && !float.IsInfinity(v.Z);

    private static bool IsFinite(Vector2 v) =>
        !float.IsNaN(v.X) && !float.IsNaN(v.Y) &&
        !float.IsInfinity(v.X) && !float.IsInfinity(v.Y);
}
