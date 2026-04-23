using System;
using System.Collections.Generic;
using System.Numerics;
using Combobulate.Caching;

namespace Combobulate.Sorting;

/// <summary>
/// Binary Space Partitioning sorter. Builds a BSP tree once at
/// construction time from the triangulated source mesh, splitting
/// triangles against partition planes as needed. The resulting tree is
/// guaranteed cycle-free for any view direction. Per-frame Sort is an
/// O(N) recursive walk: at each node, the camera position determines
/// whether to visit the back subtree or the front subtree first.
///
/// <para>Splitter selection: a small randomised tournament — pick the
/// candidate (from K randomly-sampled triangles, K=5) that minimises
/// <c>α · split_count + β · |front − back|</c>. This keeps tree depth
/// O(log N) while limiting fragment-count blowup.</para>
///
/// <para>Output is a permutation of source-quad indices: each visible
/// quad is emitted at the position of its rearmost triangle fragment
/// in the back-to-front walk, deduplicating later occurrences.</para>
/// </summary>
public sealed class BspSorter : IFaceSorter
{
    /// <summary>Splitter-selection: number of candidate triangles to score per node.</summary>
    public const int CandidateSampleCount = 5;

    /// <summary>Splitter-selection weight on split-count.</summary>
    public const float SplitCostWeight = 4f;

    /// <summary>Splitter-selection weight on |front − back| imbalance.</summary>
    public const float ImbalanceWeight = 1f;

    private readonly ObjGeometry _geometry;
    private readonly Node _root;
    private readonly int _triangleCount;

    /// <summary>The triangle list owned by tree nodes (for tests/diagnostics).</summary>
    public int TriangleCount => _triangleCount;

    /// <summary>Maximum tree depth (for diagnostics).</summary>
    public int MaxDepth { get; }

    public BspSorter(ObjGeometry geometry) : this(geometry, seed: 12345) { }

    /// <summary>Construct with a deterministic RNG seed for splitter selection (used by tests).</summary>
    public BspSorter(ObjGeometry geometry, int seed)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        var triangles = Triangulator.Triangulate(geometry.Quads);
        _triangleCount = triangles.Count;
        var rng = new Random(seed);
        _root = Build(triangles, rng, depth: 0, out int maxDepth);
        MaxDepth = maxDepth;
    }

    public int QuadCount => _geometry.Quads.Length;

    public int Sort(Matrix4x4 rotation, int[] orderBuffer, bool[] visibleBuffer, float cameraDistance = 0f, float cullMarginCos = 0f)
    {
        var quads = _geometry.Quads;
        int qc = quads.Length;
        if (qc == 0) return 0;

        // Per-quad cull. Under perspective (cameraDistance > 0) we test the rotated
        // normal against the per-face view ray from the camera to the face centroid;
        // under orthographic projection (cameraDistance == 0) this collapses to
        // viewNormal.Z > eps. The perspective branch is what keeps off-centre faces
        // visible when their normal becomes perpendicular to the global view axis
        // — e.g. the inside face of a double-sided book cover at pitch=±90°.
        // cullMarginCos > 0 widens the front-facing cone to absorb small CPU-vs-GPU
        // rotation mismatches during animations.
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

        // Camera in object space. Two regimes:
        //   - Orthographic (cameraDistance == 0): the camera is at infinity along the
        //     view's +Z axis. Per-plane hemisphere is determined by dot(plane.Normal,
        //     cameraDir) — a cosine-scale test, because position is irrelevant.
        //   - Perspective (cameraDistance > 0): the camera is at the finite point
        //     (0, 0, cameraDistance) in view space. Per-plane hemisphere is the SIGN of
        //     the signed distance from that point to the plane — a distance-scale test.
        // Mixing these up at extreme tilts (e.g. pitch=±90°) causes visible reordering
        // bugs because a plane's normal and the camera's offset along that normal can
        // disagree about which side the viewer is on.
        if (!Matrix4x4.Invert(rotation, out var invRot)) invRot = Matrix4x4.Identity;
        var cameraDir = Vector3.TransformNormal(new Vector3(0, 0, 1), invRot);
        var cameraPosObj = persp
            ? Vector3.Transform(new Vector3(0, 0, cameraDistance), invRot)
            : Vector3.Zero;

        var seen = new bool[qc];
        int written = 0;
        Walk(_root, cameraDir, cameraPosObj, persp, visibleBuffer, seen, orderBuffer, ref written);
        return written;
    }

    /// <summary>
    /// In-order walk: at each node, render the side AWAY from the camera
    /// first (back-to-front from the viewer's perspective), then the
    /// node's own coplanar bundle, then the side TOWARD the camera.
    /// Hemisphere selection is direction-based under orthographic
    /// projection and position-based under perspective — see
    /// <see cref="Sort"/> for why mixing them produces order glitches at
    /// extreme tilts.
    /// </summary>
    private static void Walk(Node? node, Vector3 cameraDir, Vector3 cameraPosObj, bool isPerspective, bool[] visible, bool[] seen, int[] order, ref int written)
    {
        if (node == null) return;

        int hemi;
        if (isPerspective)
        {
            // Distance-scale: which side of an infinite plane the camera point sits on.
            // SignedDistanceSide is symmetric and snaps |sd| < DistanceEpsilon to 0
            // (camera lies essentially on the plane → the splitter is a thin sliver and
            // either traversal order is equally correct; deterministic fallback below).
            hemi = GeometryPredicates.SignedDistanceSide(node.Plane.SignedDistance(cameraPosObj));
        }
        else
        {
            // Cosine-scale: camera is at infinity along +cameraDir relative to the model.
            // Snaps |dot| < CosineEpsilon to 0 for the same grazing-plane reason.
            hemi = GeometryPredicates.CameraHemisphere(Vector3.Dot(node.Plane.Normal, cameraDir));
        }

        if (hemi >= 0)
        {
            // Camera on/above front side → render back subtree, then node, then front.
            Walk(node.Back, cameraDir, cameraPosObj, isPerspective, visible, seen, order, ref written);
            EmitBundle(node, visible, seen, order, ref written);
            Walk(node.Front, cameraDir, cameraPosObj, isPerspective, visible, seen, order, ref written);
        }
        else
        {
            Walk(node.Front, cameraDir, cameraPosObj, isPerspective, visible, seen, order, ref written);
            EmitBundle(node, visible, seen, order, ref written);
            Walk(node.Back,  cameraDir, cameraPosObj, isPerspective, visible, seen, order, ref written);
        }
    }

    private static void EmitBundle(Node node, bool[] visible, bool[] seen, int[] order, ref int written)
    {
        // Each tree node owns a bundle of coplanar-with-its-plane triangles
        // (at minimum, the splitter triangle itself). They are mutually
        // ordered by input index, which is fine because they don't occlude
        // each other in any nontrivial way at this granularity.
        for (int i = 0; i < node.Bundle.Count; i++)
        {
            int sq = node.Bundle[i].SourceQuadIndex;
            if (sq < 0 || seen[sq]) continue;
            if (!visible[sq]) continue;
            seen[sq] = true;
            order[written++] = sq;
        }
    }

    // ===== build =====

    private sealed class Node
    {
        public Plane3 Plane;
        public List<RenderTriangle> Bundle = new();
        public Node? Front;
        public Node? Back;
    }

    private static Node Build(List<RenderTriangle> tris, Random rng, int depth, out int maxDepth)
    {
        maxDepth = depth;
        var node = new Node();
        if (tris.Count == 0) { node.Plane = new Plane3(new Vector3(0, 0, 1), 0); return node; }

        // Pick a splitter via K-candidate tournament.
        int splitterIdx = ChooseSplitter(tris, rng);
        var splitter = tris[splitterIdx];
        node.Plane = splitter.Plane;
        node.Bundle.Add(splitter);

        var frontList = new List<RenderTriangle>();
        var backList  = new List<RenderTriangle>();
        var coplanarFront = new List<RenderTriangle>();
        var coplanarBack  = new List<RenderTriangle>();

        for (int i = 0; i < tris.Count; i++)
        {
            if (i == splitterIdx) continue;
            PolygonSplitter.Split(tris[i], node.Plane, frontList, backList, coplanarFront, coplanarBack);
        }

        // Coplanar triangles join the splitter's bundle (they all share the plane).
        for (int i = 0; i < coplanarFront.Count; i++) node.Bundle.Add(coplanarFront[i]);
        for (int i = 0; i < coplanarBack.Count;  i++) node.Bundle.Add(coplanarBack[i]);

        if (frontList.Count > 0)
        {
            node.Front = Build(frontList, rng, depth + 1, out var d);
            if (d > maxDepth) maxDepth = d;
        }
        if (backList.Count > 0)
        {
            node.Back = Build(backList, rng, depth + 1, out var d);
            if (d > maxDepth) maxDepth = d;
        }
        return node;
    }

    private static int ChooseSplitter(List<RenderTriangle> tris, Random rng)
    {
        if (tris.Count == 1) return 0;
        int k = Math.Min(CandidateSampleCount, tris.Count);
        int bestIdx = 0;
        float bestScore = float.PositiveInfinity;

        for (int s = 0; s < k; s++)
        {
            int idx = (s == 0) ? 0 : rng.Next(tris.Count); // first candidate is deterministic to ensure progress
            var plane = tris[idx].Plane;
            int splits = 0, front = 0, back = 0;
            for (int i = 0; i < tris.Count; i++)
            {
                if (i == idx) continue;
                var c = PolygonSplitter.ClassifyTriangle(tris[i], plane);
                switch (c.Side)
                {
                    case PlaneSide.Front: front++; break;
                    case PlaneSide.Back:  back++; break;
                    case PlaneSide.Spanning: splits++; front++; back++; break; // a span produces ≥1 fragment per side
                    case PlaneSide.On: /* coplanar joins splitter; doesn't affect children */ break;
                }
            }
            float score = SplitCostWeight * splits + ImbalanceWeight * Math.Abs(front - back);
            if (score < bestScore) { bestScore = score; bestIdx = idx; }
        }
        return bestIdx;
    }
}
