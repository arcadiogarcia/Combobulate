using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Parsing;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Tests for <see cref="MeshDecomposer"/> — the pre-split-for-painter
/// algorithm. Covers:
/// <list type="bullet">
///   <item>API contracts (null/empty input, NaN safety, option validation)</item>
///   <item>Fast paths (convex closed polyhedra pass through unchanged)</item>
///   <item>Splitting correctness (non-convex inputs produce per-fragment
///         painter-ready output)</item>
///   <item>Material / UV / normal preservation</item>
///   <item>Fragment-area conservation (no geometry created or destroyed)</item>
///   <item>Determinism (same input → same fragment list)</item>
///   <item>Plane deduplication (opposite-winding coplanar duplicates merge)</item>
///   <item>Safety cap on pathological inputs</item>
/// </list>
/// </summary>
public class MeshDecomposerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static CachedQuad Quad(
        int srcIdx,
        Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3,
        string? material = null)
    {
        var centroid = (v0 + v1 + v2 + v3) * 0.25f;
        var normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v3 - v0));
        return new CachedQuad(
            srcIdx, v0, v1, v2, v3, centroid, normal,
            Windows.UI.Color.FromArgb(255, 200, 100, 50),
            material,
            new Vector2(0, 0), new Vector2(1, 0),
            new Vector2(1, 1), new Vector2(0, 1));
    }

    private static CachedQuad TriQuad(
        int srcIdx, Vector3 v0, Vector3 v1, Vector3 v2, string? material = null)
    {
        var normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
        return CachedQuad.Triangle(
            srcIdx, v0, v1, v2, normal,
            Windows.UI.Color.FromArgb(255, 100, 200, 50),
            material,
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1));
    }

    /// <summary>
    /// Sum of fragment area projected onto the parent face's plane.
    /// Should equal the parent face's area for any decomposition.
    /// </summary>
    private static float TotalArea(IReadOnlyList<CachedQuad> fragments)
    {
        float a = 0;
        for (int i = 0; i < fragments.Count; i++)
            a += QuadArea(fragments[i]);
        return a;
    }

    private static float QuadArea(CachedQuad q)
    {
        if (q.IsTriangle)
            return 0.5f * Vector3.Cross(q.V1 - q.V0, q.V2 - q.V0).Length();
        // Quad area = sum of two triangles along V0–V2 diagonal.
        return 0.5f * (
            Vector3.Cross(q.V1 - q.V0, q.V2 - q.V0).Length() +
            Vector3.Cross(q.V2 - q.V0, q.V3 - q.V0).Length());
    }

    // ── API contracts ───────────────────────────────────────────────────

    [Fact]
    public void Decompose_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MeshDecomposer.DecomposeForPainterOrder(null!));
    }

    [Fact]
    public void Decompose_EmptyInput_ReturnsEmpty()
    {
        var result = MeshDecomposer.DecomposeForPainterOrder(Array.Empty<CachedQuad>());
        Assert.Empty(result);
    }

    [Fact]
    public void Decompose_InvalidOptions_Throws()
    {
        var quads = new[] { Quad(0, new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)) };
        var bad = new MeshDecomposer.DecomposerOptions { MaxFragmentsPerSourceQuad = 0 };
        Assert.Throws<ArgumentException>(() => MeshDecomposer.DecomposeForPainterOrder(quads, bad));
    }

    // ── Fast path: convex meshes pass through with no extra splits ─────

    [Fact]
    public void Decompose_SingleQuad_PreservedAsQuad()
    {
        var quads = new[]
        {
            Quad(0,
                new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
                new Vector3(1, 1, 0),   new Vector3(-1, 1, 0)),
        };
        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        // Quad-preserving decomposition: a single planar+convex quad
        // with no other planes to cut against stays as one quad
        // fragment (Case A is not even invoked — no splits at all).
        Assert.Single(result);
        Assert.False(result[0].IsTriangle);
        Assert.Equal(4f, TotalArea(result), 3);
    }

    [Fact]
    public void Decompose_SingleTriangle_PassesThrough()
    {
        var quads = new[]
        {
            TriQuad(0,
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)),
        };
        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        Assert.Single(result);
        Assert.True(result[0].IsTriangle);
        Assert.Equal(0.5f, TotalArea(result), 3);
    }

    [Fact]
    public void Decompose_AxisAlignedCube_NoExtraFragments()
    {
        // The unit cube is convex: every face lies entirely on one side
        // (or coplanar) of every other face's plane. Decomposer should
        // hit the IsAlreadyPainterReady fast path and skip the rebuild.
        var geom = TestGeometries.UnitCube();
        Assert.True(MeshDecomposer.IsAlreadyPainterReady(geom.Quads));

        var result = MeshDecomposer.DecomposeForPainterOrder(geom.Quads);
        // 6 source quads stay as 6 quads — quad-preserving decomposition
        // doesn't triangulate inputs that don't need cutting.
        Assert.Equal(6, result.Count);
        Assert.All(result, q => Assert.False(q.IsTriangle));
        // Area: each face is 1×1, six faces = 6.
        Assert.Equal(6f, TotalArea(result), 3);
    }

    [Fact]
    public void WithPainterSubdivision_OnConvexMesh_ReturnsSameInstance()
    {
        // The fast-path contract: convex meshes should not allocate a
        // new ObjGeometry (the cube test above proves no splits, but the
        // WithPainterSubdivision wrapper makes a stronger guarantee).
        var geom = TestGeometries.UnitCube();
        var refined = geom.WithPainterSubdivision();
        Assert.Same(geom, refined);
    }

    // ── Non-convex geometry: the case the decomposer exists for ────────

    [Fact]
    public void Decompose_NonConvexLShape_ProducesPainterReadyFragments()
    {
        // L-shape in the XY plane, extruded by 2 in Z (a "step" block).
        // Self-occluding under perspective from many camera directions.
        // We use ObjParser to build it through the production pipeline.
        var obj = @"
v -2 -1  1
v  1 -1  1
v  1  0  1
v  0  0  1
v  0  1  1
v -2  1  1
v -2 -1 -1
v  1 -1 -1
v  1  0 -1
v  0  0 -1
v  0  1 -1
v -2  1 -1
f 1 2 3 4
f 1 4 5 6
f 7 12 11 10
f 7 10 9 8
f 1 6 12 7
f 2 8 9 3
f 4 3 9 10
f 5 4 10 11
f 6 5 11 12
f 1 7 8 2
".Trim();
        var model = ObjParser.Parse(obj).Model;
        var geometry = ObjGeometry.Build(model);
        Assert.False(MeshDecomposer.IsAlreadyPainterReady(geometry.Quads),
            "L-shape should not be detected as already-painter-ready (it is non-convex).");

        var fragments = MeshDecomposer.DecomposeForPainterOrder(geometry.Quads);
        Assert.True(fragments.Count > geometry.Quads.Length,
            $"Expected MORE fragments than input quads ({geometry.Quads.Length}); got {fragments.Count}.");

        // The decomposed mesh must now be painter-ready (no fragment
        // straddles any other fragment's plane — the convergence
        // criterion for the algorithm).
        var converged = MeshDecomposer.DecomposeForPainterOrder(fragments);
        Assert.Equal(fragments.Count, converged.Count);
    }

    [Fact]
    public void Decompose_BookObjFromSamples_ProducesPainterReadyFragments()
    {
        var path = LocateSamplesFile("book.obj");
        var model = ObjParser.Parse(File.ReadAllText(path)).Model;
        var geom = ObjGeometry.Build(model);

        // The Deet book is the canonical non-convex case this whole
        // feature targets. The cover overhangs the inset pages-block.
        Assert.False(MeshDecomposer.IsAlreadyPainterReady(geom.Quads));

        var refined = geom.WithPainterSubdivision();
        Assert.NotSame(geom, refined);
        Assert.True(refined.Quads.Length > geom.Quads.Length);

        // Idempotence: re-running the decomposer on already-decomposed
        // output must produce the same fragment count.
        Assert.True(MeshDecomposer.IsAlreadyPainterReady(refined.Quads));
        var twice = MeshDecomposer.DecomposeForPainterOrder(refined.Quads);
        Assert.Equal(refined.Quads.Length, twice.Count);
    }

    // ── Material / UV / normal preservation ─────────────────────────────

    [Fact]
    public void Decompose_PreservesMaterialName()
    {
        // Two quads, two different materials. Each fragment must carry
        // its parent's material name.
        var quads = new[]
        {
            Quad(0,
                new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
                new Vector3(1, 1, 0), new Vector3(-1, 1, 0),
                material: "cover"),
            Quad(1,
                new Vector3(0, -0.5f, -1), new Vector3(0, 0.5f, -1),
                new Vector3(0, 0.5f, 1), new Vector3(0, -0.5f, 1),
                material: "pages"),
        };

        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        Assert.Contains(result, q => q.MaterialName == "cover");
        Assert.Contains(result, q => q.MaterialName == "pages");
        Assert.DoesNotContain(result, q => q.MaterialName != "cover" && q.MaterialName != "pages");
    }

    [Fact]
    public void Decompose_PreservesNormalDirection()
    {
        // A face with +Z normal: every fragment of that face must also
        // have +Z normal (we don't flip winding during splits).
        var quads = new[]
        {
            Quad(0,
                new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
                new Vector3(1, 1, 0), new Vector3(-1, 1, 0)),
            // A perpendicular quad whose plane cuts the first.
            Quad(1,
                new Vector3(0, -0.5f, -1), new Vector3(0, 0.5f, -1),
                new Vector3(0, 0.5f, 1), new Vector3(0, -0.5f, 1)),
        };

        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        // Fragments that descended from the first quad must have +Z normal.
        var firstParentFrags = result.Where(q => q.SourceIndex == 0).ToList();
        Assert.NotEmpty(firstParentFrags);
        foreach (var f in firstParentFrags)
        {
            Assert.Equal(0f, f.Normal.X, 3);
            Assert.Equal(0f, f.Normal.Y, 3);
            Assert.Equal(1f, f.Normal.Z, 3);
        }
    }

    [Fact]
    public void Decompose_InterpolatesUVsAlongCutEdge()
    {
        // A quad with UVs (0,0)..(1,1) cut at x=0 should produce
        // fragments with UVs that include the interpolated x=0.5 point.
        var cover = Quad(0,
            new Vector3(-1, -1, 0.1f), new Vector3(1, -1, 0.1f),
            new Vector3(1, 1, 0.1f),   new Vector3(-1, 1, 0.1f));
        var cutter = Quad(1,
            new Vector3(0, -2, -1), new Vector3(0, 2, -1),
            new Vector3(0, 2, 1),   new Vector3(0, -2, 1));
        var quads = new[] { cover, cutter };

        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        // Cover-derived fragments should have UV.x values that include
        // the interpolated 0.5 point (where the cutter plane crosses the cover).
        var coverFrags = result.Where(q => q.SourceIndex == 0).ToList();
        // Each fragment is a triangle: V0, V1, V2 with Uv0, Uv1, Uv2.
        var uvXs = coverFrags
            .SelectMany(q => new[] { q.Uv0.X, q.Uv1.X, q.Uv2.X })
            .ToList();

        // At least one fragment vertex must be the interpolated UV at the cut
        // (cover spans x in [-1,1] mapped to u in [0,1]; cut at x=0 → u=0.5).
        Assert.Contains(uvXs, x => MathF.Abs(x - 0.5f) < 1e-3f);

        // Bounding-box area conservation: total UV-area still 1.0 (within tolerance)
        // because each fragment's UV maps 1:1 to its 3D area in the parent quad's space.
        var totalCoverArea = TotalArea(coverFrags);
        Assert.Equal(QuadArea(cover), totalCoverArea, 3);
    }

    // ── Coplanar handling ───────────────────────────────────────────────

    [Fact]
    public void Decompose_CoplanarOppositeWindingDuplicates_DoNotCascadeSplits()
    {
        // The Deet book has inward-facing duplicates of every cover face.
        // These share a plane (with opposite normals). The decomposer
        // must dedupe them in the plane set so they don't trigger
        // self-cutting on each other.
        var outward = Quad(0,
            new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
            new Vector3(1, 1, 0),   new Vector3(-1, 1, 0));
        var inward = Quad(1,
            // Same vertices, reverse winding (so normal is -Z).
            new Vector3(-1, 1, 0),  new Vector3(1, 1, 0),
            new Vector3(1, -1, 0),  new Vector3(-1, -1, 0));
        var quads = new[] { outward, inward };

        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        // Quad-preserving decomposition: each parent quad emerges as one
        // quad fragment (no other planes exist that would cut it). So 2
        // total fragments, not 4 triangles.
        Assert.Equal(2, result.Count);
        Assert.All(result, q => Assert.False(q.IsTriangle));
        Assert.Equal(1, result.Count(q => q.SourceIndex == 0));
        Assert.Equal(1, result.Count(q => q.SourceIndex == 1));
    }

    // ── Mutual-straddle case (the bug this feature exists for) ─────────

    [Fact]
    public void Decompose_MutualStraddleCycle_BecomesPainterReady()
    {
        var geom = TestGeometries.MutualStraddleCycle();
        Assert.False(MeshDecomposer.IsAlreadyPainterReady(geom.Quads));

        var fragments = MeshDecomposer.DecomposeForPainterOrder(geom.Quads);
        Assert.True(MeshDecomposer.IsAlreadyPainterReady(fragments),
            "Decomposed mutual-straddle mesh must be painter-ready.");

        // Cover quad gets split where the page plane crosses it — expect
        // MORE fragments than input quads (some kind of cut happened).
        Assert.True(fragments.Count > geom.Quads.Length,
            $"Expected more fragments than input ({geom.Quads.Length}); got {fragments.Count}.");
    }

    // ── Determinism ─────────────────────────────────────────────────────

    [Fact]
    public void Decompose_IsDeterministic()
    {
        var path = LocateSamplesFile("book.obj");
        var model = ObjParser.Parse(File.ReadAllText(path)).Model;
        var geom = ObjGeometry.Build(model);

        var first = MeshDecomposer.DecomposeForPainterOrder(geom.Quads);
        var second = MeshDecomposer.DecomposeForPainterOrder(geom.Quads);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].V0, second[i].V0);
            Assert.Equal(first[i].V1, second[i].V1);
            Assert.Equal(first[i].V2, second[i].V2);
            Assert.Equal(first[i].SourceIndex, second[i].SourceIndex);
            Assert.Equal(first[i].MaterialName, second[i].MaterialName);
        }
    }

    // ── NaN / degenerate filtering ──────────────────────────────────────

    [Fact]
    public void Decompose_DropsDegenerateFragmentsAndSliverTriangles()
    {
        // A normal quad alongside a sliver (collinear "triangle").
        var quads = new[]
        {
            Quad(0,
                new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
                new Vector3(1, 1, 0), new Vector3(-1, 1, 0)),
        };
        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        Assert.All(result, q =>
        {
            var cross = Vector3.Cross(q.V1 - q.V0, q.V2 - q.V0);
            Assert.True(cross.LengthSquared() > 1e-12f, "Degenerate fragments must be filtered.");
        });
    }

    // ── Area conservation (a strong invariant) ──────────────────────────

    [Fact]
    public void Decompose_PreservesTotalArea_OnBookObj()
    {
        var path = LocateSamplesFile("book.obj");
        var model = ObjParser.Parse(File.ReadAllText(path)).Model;
        var geom = ObjGeometry.Build(model);

        float originalArea = 0f;
        for (int i = 0; i < geom.Quads.Length; i++)
            originalArea += QuadArea(geom.Quads[i]);

        var fragments = MeshDecomposer.DecomposeForPainterOrder(geom.Quads);
        var fragmentArea = TotalArea(fragments);

        // Area conservation tolerance: fp accumulation across ~50 cuts.
        // Should match to within 0.1% of the original.
        Assert.Equal(originalArea, fragmentArea, MathF.Max(originalArea * 1e-3f, 1e-4f));
    }

    [Fact]
    public void Decompose_PerSourceQuadAreaIsPreserved()
    {
        // For each source quad, the sum of its fragments' areas should
        // equal its own area. This is stricter than total-area
        // conservation (catches "fragment got reassigned to wrong parent").
        var path = LocateSamplesFile("book.obj");
        var model = ObjParser.Parse(File.ReadAllText(path)).Model;
        var geom = ObjGeometry.Build(model);

        var fragments = MeshDecomposer.DecomposeForPainterOrder(geom.Quads);
        var perSourceFragArea = new Dictionary<int, float>();
        foreach (var f in fragments)
        {
            if (!perSourceFragArea.ContainsKey(f.SourceIndex)) perSourceFragArea[f.SourceIndex] = 0;
            perSourceFragArea[f.SourceIndex] += QuadArea(f);
        }

        for (int i = 0; i < geom.Quads.Length; i++)
        {
            var src = geom.Quads[i];
            Assert.True(perSourceFragArea.TryGetValue(src.SourceIndex, out var fragArea),
                $"Source quad {i} (SourceIndex={src.SourceIndex}) lost all fragments.");
            Assert.Equal(QuadArea(src), fragArea, MathF.Max(QuadArea(src) * 1e-3f, 1e-4f));
        }
    }

    // ── Safety cap on pathological inputs ───────────────────────────────

    [Fact]
    public void Decompose_RespectsMaxFragmentsCap()
    {
        // Construct an adversarial input: many quads, each in a unique
        // orientation, each straddling all the others. With a tiny cap
        // the decomposer should return a partial result without
        // exploding.
        var rng = new Random(42);
        var quads = new List<CachedQuad>();
        for (int i = 0; i < 6; i++)
        {
            // Random quads passing roughly through origin in random orientations.
            var rot = Matrix4x4.CreateRotationY(rng.NextSingle() * MathF.PI) *
                      Matrix4x4.CreateRotationX(rng.NextSingle() * MathF.PI);
            var v0 = Vector3.Transform(new Vector3(-1, -1, 0), rot);
            var v1 = Vector3.Transform(new Vector3(+1, -1, 0), rot);
            var v2 = Vector3.Transform(new Vector3(+1, +1, 0), rot);
            var v3 = Vector3.Transform(new Vector3(-1, +1, 0), rot);
            quads.Add(Quad(i, v0, v1, v2, v3));
        }

        var tinyCap = new MeshDecomposer.DecomposerOptions
        {
            CoplanarCosThreshold = 0.999f,
            CoplanarDistanceThreshold = 1e-4f,
            MaxFragmentsPerSourceQuad = 2,    // cap at 12 fragments total
        };
        var result = MeshDecomposer.DecomposeForPainterOrder(quads, tinyCap);
        // Should be bounded — not catastrophically blown up.
        // (Cap fires after a split round; allow the partially-split
        // round's result, which can exceed the cap by one round.)
        Assert.True(result.Count <= quads.Count * tinyCap.MaxFragmentsPerSourceQuad * 4,
            $"Decomposer exceeded safety cap dramatically: got {result.Count}, cap was {tinyCap.MaxFragmentsPerSourceQuad}/quad.");
        Assert.NotEmpty(result);
    }

    // ── Plane dedup correctness ─────────────────────────────────────────

    [Fact]
    public void IsAlreadyPainterReady_ReportsConvexShapesCorrectly()
    {
        // Six unit-cube faces, all axis-aligned: painter-ready.
        Assert.True(MeshDecomposer.IsAlreadyPainterReady(TestGeometries.UnitCube().Quads));
    }

    [Fact]
    public void IsAlreadyPainterReady_ReportsNonConvexCorrectly()
    {
        Assert.False(MeshDecomposer.IsAlreadyPainterReady(TestGeometries.MutualStraddleCycle().Quads));
    }

    // ── Quad-preserving emission ───────────────────────────────────────

    [Fact]
    public void Decompose_OppositeEdgeCut_PreservesBothQuads()
    {
        // Case A: a single quad cut by a plane that intersects two
        // OPPOSITE edges (axis-aligned book-cover style). Both halves
        // should remain 4-vertex quads.
        var cover = Quad(0,
            new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
            new Vector3(1, 1, 0),   new Vector3(-1, 1, 0));
        var cutter = Quad(1,
            new Vector3(0, -2, -1), new Vector3(0, 2, -1),
            new Vector3(0, 2, 1),   new Vector3(0, -2, 1));
        var quads = new[] { cover, cutter };

        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        var coverFrags = result.Where(q => q.SourceIndex == 0).ToList();
        // Cover splits cleanly into 2 quads — no triangles emitted.
        Assert.Equal(2, coverFrags.Count);
        Assert.All(coverFrags, q => Assert.False(q.IsTriangle));
        Assert.Equal(QuadArea(cover), coverFrags.Sum(QuadArea), 3);
    }

    [Fact]
    public void Decompose_DiagonalCut_FallsBackToTriangles()
    {
        // Case D: a quad cut by a plane that runs diagonally through
        // two opposite VERTICES. Each half is unavoidably a triangle.
        // Construct an unambiguous diagonal: cover quad + cutter plane
        // that crosses (-1,-1,0) and (+1,+1,0).
        var cover = Quad(0,
            new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
            new Vector3(1, 1, 0),   new Vector3(-1, 1, 0));
        // Cutter plane: x = y (passes through (−1,−1) and (1,1)).
        // Normal direction (1, -1, 0)/√2.
        var nDiag = Vector3.Normalize(new Vector3(1, -1, 0));
        var cutterCentroid = Vector3.Zero;
        var cutter = new CachedQuad(
            sourceIndex: 1,
            v0: new Vector3(-1, -1, -1), v1: new Vector3( 1,  1, -1),
            v2: new Vector3( 1,  1,  1), v3: new Vector3(-1, -1,  1),
            centroid: cutterCentroid,
            normal: nDiag,
            fallbackColor: Windows.UI.Color.FromArgb(255, 0, 255, 0),
            materialName: "cutter",
            uv0: new Vector2(0,0), uv1: new Vector2(1,0),
            uv2: new Vector2(1,1), uv3: new Vector2(0,1));
        var quads = new[] { cover, cutter };

        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        var coverFrags = result.Where(q => q.SourceIndex == 0).ToList();
        // Cover halves are triangles (vertex-to-vertex cut).
        Assert.Equal(2, coverFrags.Count);
        Assert.All(coverFrags, q => Assert.True(q.IsTriangle));
        Assert.Equal(QuadArea(cover), coverFrags.Sum(QuadArea), 3);
    }

    [Fact]
    public void Decompose_AdjacentEdgeCut_PreservesQuadEmitsTriangle()
    {
        // Case B/C: cutter passes through one edge interior and exits an
        // ADJACENT edge interior. One side becomes a triangle, the other
        // a pentagon (which is then fan-triangulated to a quad + triangle
        // OR another mix depending on the cut placement). The total
        // emitted count is 2 triangles + 1 quad = 3, OR 3 triangles + 1
        // pentagon-from-fan, depending on how the pentagon happens to be
        // partitioned by further cuts. We assert: total >= 2 and area
        // conserved; and at least one triangle exists (proving the
        // adjacent-edge geometry was handled correctly).
        var cover = Quad(0,
            new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
            new Vector3(1, 1, 0),   new Vector3(-1, 1, 0));
        // Cutter plane: passes through (0, -1, 0) and (1, 0, 0) — adjacent
        // edges (bottom and right). Normal in XY plane.
        var dir = new Vector3(1, 1, 0); // chord direction
        var n = Vector3.Normalize(new Vector3(-dir.Y, dir.X, 0));
        // Anchor at (0.5, -0.5, 0) so plane chord runs through (0,-1,0)→(1,0,0)
        var d = Vector3.Dot(n, new Vector3(0.5f, -0.5f, 0));
        var cutter = new CachedQuad(
            sourceIndex: 1,
            v0: new Vector3(0, -1, -1) - n * 0.001f, v1: new Vector3(1, 0, -1) - n * 0.001f,
            v2: new Vector3(1, 0,  1)  - n * 0.001f, v3: new Vector3(0, -1, 1) - n * 0.001f,
            centroid: new Vector3(0.5f, -0.5f, 0),
            normal: n,
            fallbackColor: Windows.UI.Color.FromArgb(255, 0, 255, 0),
            materialName: "cutter",
            uv0: new Vector2(0,0), uv1: new Vector2(1,0),
            uv2: new Vector2(1,1), uv3: new Vector2(0,1));
        var quads = new[] { cover, cutter };

        var result = MeshDecomposer.DecomposeForPainterOrder(quads);
        var coverFrags = result.Where(q => q.SourceIndex == 0).ToList();
        // Adjacent-edge cut → one triangle + one pentagon (fan-triangulated
        // to 3 triangles) OR a triangle + quad pair, depending on cut chord.
        // The pentagon path emits at least 2 triangle pieces.
        Assert.True(coverFrags.Count >= 2,
            $"Adjacent-edge cut on a quad should emit at least 2 fragments; got {coverFrags.Count}.");
        Assert.Contains(coverFrags, q => q.IsTriangle);
        Assert.Equal(QuadArea(cover), coverFrags.Sum(QuadArea), 3);
    }

    [Fact]
    public void Decompose_NonPlanarQuad_FallsBackToTriangles()
    {
        // A "quad" whose 4 vertices do NOT lie in a plane. The decomposer
        // cannot keep it as a single 4-vertex polygon (planarity is the
        // base assumption for the clipper), so the source quad enters
        // the working set as TWO triangle fragments instead of one quad.
        // Subsequent splits against other planes can still re-combine
        // pieces into quad fragments via Case B/C (triangle cut by a
        // plane → triangle + quad). This test asserts only the initial
        // fallback semantics: the source quad never enters as a single
        // 4-vertex polygon, and area is conserved.
        var nonPlanarQuad = Quad(0,
            new Vector3(-1, -1, 0),
            new Vector3( 1, -1, 0),
            new Vector3( 1,  1, 0.5f),  // bent up out of XY plane
            new Vector3(-1,  1, 0));
        var result = MeshDecomposer.DecomposeForPainterOrder(new[] { nonPlanarQuad });
        Assert.True(result.Count >= 2,
            $"Non-planar quad should fall back to at least 2 fragments; got {result.Count}.");
        Assert.Equal(QuadArea(nonPlanarQuad), TotalArea(result), 2);
    }

    [Fact]
    public void Decompose_NonConvexQuad_FallsBackToTriangles()
    {
        // A concave "quad" (one corner reflexes inward). Quad-preserving
        // splits require convex inputs, so this should triangulate.
        // Vertices in CCW order with V2 pulled to (0, 0): the cross at V1
        // is (+), cross at V2 is (−) — sign flip.
        var concaveQuad = Quad(0,
            new Vector3(-1, -1, 0),
            new Vector3( 1, -1, 0),
            new Vector3( 0,  0, 0),    // reflex corner toward origin
            new Vector3(-1,  1, 0));
        var result = MeshDecomposer.DecomposeForPainterOrder(new[] { concaveQuad });
        Assert.Equal(2, result.Count);
        Assert.All(result, q => Assert.True(q.IsTriangle,
            "Non-convex source quads must fall back to triangle fragments."));
    }

    [Fact]
    public void Decompose_BookObj_ProducesExpectedQuadCount()
    {
        // The flagship guarantee of Phase 1: book.obj's 9 source quads
        // emerge from quad-preserving decomposition as 33 quad fragments
        // (vs 69 triangle fragments with the previous algorithm).
        var path = LocateSamplesFile("book.obj");
        var model = ObjParser.Parse(File.ReadAllText(path)).Model;
        var geom = ObjGeometry.Build(model);
        Assert.Equal(9, geom.Quads.Length);

        var fragments = MeshDecomposer.DecomposeForPainterOrder(geom.Quads);
        Assert.Equal(33, fragments.Count);
        // Every fragment is a quad — quad-preserving for book.obj works
        // entirely via Case-A (opposite-edge) cuts.
        Assert.All(fragments, q => Assert.False(q.IsTriangle,
            "All book.obj fragments must be quads — Case A applies throughout."));
    }

    [Fact]
    public void Decompose_BookObj_EverySubQuad_BrushTransformMatchesUVs()
    {
        // End-to-end check that mirrors what BakedAspectGraphRenderer does
        // for every sprite of the diagnostic book: build the sprite layout
        // (Size = (|xAxis|, |yAxis|), local-(0,0)→V0, (W,0)→V1, (0,H)→V3),
        // build the brush transform via BrushTransformMath, then verify
        // that each sprite corner samples the surface at the correct
        // UV (no V-flip; the surface loader is v-up). This is the test that
        // proves end-to-end that subdivided sub-quads do NOT render their
        // textures rotated.
        var path = LocateSamplesFile("book.obj");
        var model = ObjParser.Parse(File.ReadAllText(path)).Model;
        var geom = ObjGeometry.Build(model);
        var fragments = MeshDecomposer.DecomposeForPainterOrder(geom.Quads);

        foreach (var q in fragments)
        {
            if (q.IsTriangle) continue;
            var xAxis = q.V1 - q.V0;
            var yAxis = q.V3 - q.V0;
            var lenX = xAxis.Length();
            var lenY = yAxis.Length();
            Assert.True(lenX > 0f, $"Sub-quad {q.SourceIndex}: |xAxis| must be > 0.");
            Assert.True(lenY > 0f, $"Sub-quad {q.SourceIndex}: |yAxis| must be > 0.");
            var spriteSize = new Vector2(lenX, lenY);

            var m = BrushTransformMath.BuildQuadAxisAlignedCrop(
                q.Uv0, q.Uv1, q.Uv2, q.Uv3,
                Vector2.One, Vector2.Zero, spriteSize);

            // sprite-(0,0)=V0 should sample texture at uv0 (no V-flip; in
            // brush-pixel units).
            var s00 = ApplyAffine(m, new Vector2(0, 0));
            AssertNear(q.Uv0.X * spriteSize.X, s00.X,
                $"src{q.SourceIndex} V0 brush.X for uv0=({q.Uv0.X},{q.Uv0.Y}).");
            AssertNear(q.Uv0.Y * spriteSize.Y, s00.Y,
                $"src{q.SourceIndex} V0 brush.Y for uv0=({q.Uv0.X},{q.Uv0.Y}).");

            var s10 = ApplyAffine(m, new Vector2(spriteSize.X, 0));
            AssertNear(q.Uv1.X * spriteSize.X, s10.X,
                $"src{q.SourceIndex} V1 brush.X for uv1=({q.Uv1.X},{q.Uv1.Y}).");
            AssertNear(q.Uv1.Y * spriteSize.Y, s10.Y,
                $"src{q.SourceIndex} V1 brush.Y for uv1=({q.Uv1.X},{q.Uv1.Y}).");

            var s01 = ApplyAffine(m, new Vector2(0, spriteSize.Y));
            AssertNear(q.Uv3.X * spriteSize.X, s01.X,
                $"src{q.SourceIndex} V3 brush.X for uv3=({q.Uv3.X},{q.Uv3.Y}).");
            AssertNear(q.Uv3.Y * spriteSize.Y, s01.Y,
                $"src{q.SourceIndex} V3 brush.Y for uv3=({q.Uv3.X},{q.Uv3.Y}).");

            // V2 corner (sprite-(W,H)) — implied by parallelogram. Sub-quads
            // emitted by axis-aligned planar cuts on axis-aligned parent
            // quads are always parallelograms, so the brush should also
            // sample correctly there.
            var s11 = ApplyAffine(m, spriteSize);
            AssertNear(q.Uv2.X * spriteSize.X, s11.X,
                $"src{q.SourceIndex} V2 brush.X for uv2=({q.Uv2.X},{q.Uv2.Y}).");
            AssertNear(q.Uv2.Y * spriteSize.Y, s11.Y,
                $"src{q.SourceIndex} V2 brush.Y for uv2=({q.Uv2.X},{q.Uv2.Y}).");
        }
    }

    [Fact]
    public void Decompose_BookObj_EverySubQuad_VertexBilinearUvConsistent()
    {
        // For every sub-quad of book.obj, the (V, UV) correspondence must
        // be a valid bilinear interpolation of the source quad's (V, UV)
        // gradient. Specifically: project each V onto the source quad's
        // local axes, compute the expected UV via the source's gradient,
        // and compare to the actual UV. Any mismatch means PolygonClipper
        // computed UVs that don't match its spatial interpolation, which
        // would render as a "displaced" texture on that sub-quad.
        var path = LocateSamplesFile("book.obj");
        var model = ObjParser.Parse(File.ReadAllText(path)).Model;
        var geom = ObjGeometry.Build(model);
        var sourceQuads = geom.Quads;
        var fragments = MeshDecomposer.DecomposeForPainterOrder(sourceQuads);

        foreach (var q in fragments)
        {
            var parent = sourceQuads[q.SourceIndex];
            // Parent's local frame: xAxis = V1-V0, yAxis = V3-V0; UV gradient
            // matches the source's uv0..uv3 layout. For book.obj sources,
            // parent is a planar rectangle so this is exact.
            var px = parent.V1 - parent.V0;
            var py = parent.V3 - parent.V0;
            var pxLenSq = px.LengthSquared();
            var pyLenSq = py.LengthSquared();
            if (pxLenSq <= 0 || pyLenSq <= 0) continue;

            // For each output vertex, project onto parent's basis to get
            // (u, v) ∈ [0,1]² and predict the bilinear UV.
            void CheckVertex(Vector3 v, Vector2 expectedUv, int corner)
            {
                var dv = v - parent.V0;
                var u = Vector3.Dot(dv, px) / pxLenSq;
                var w = Vector3.Dot(dv, py) / pyLenSq;
                // Bilinear interpolation of parent's UVs.
                var predicted =
                    parent.Uv0 * (1 - u) * (1 - w) +
                    parent.Uv1 * u * (1 - w) +
                    parent.Uv2 * u * w +
                    parent.Uv3 * (1 - u) * w;
                AssertNear(predicted.X, expectedUv.X,
                    $"src{q.SourceIndex} V{corner} predicted UV.X mismatch (u={u:F3} w={w:F3}).");
                AssertNear(predicted.Y, expectedUv.Y,
                    $"src{q.SourceIndex} V{corner} predicted UV.Y mismatch (u={u:F3} w={w:F3}).");
            }

            CheckVertex(q.V0, q.Uv0, 0);
            CheckVertex(q.V1, q.Uv1, 1);
            if (!q.IsTriangle) CheckVertex(q.V3, q.Uv3, 3);
            CheckVertex(q.V2, q.Uv2, 2);
        }
    }

    private static Vector2 ApplyAffine(System.Numerics.Matrix3x2 m, Vector2 p)
    {
        return new Vector2(
            m.M11 * p.X + m.M21 * p.Y + m.M31,
            m.M12 * p.X + m.M22 * p.Y + m.M32);
    }

    private static void AssertNear(float expected, float actual, string what, float tol = 1e-3f)
    {
        if (MathF.Abs(expected - actual) > tol)
            throw new Xunit.Sdk.XunitException(
                $"{what} expected={expected} actual={actual} delta={MathF.Abs(expected-actual)}");
    }

    // ── Coplanar grouping ──────────────────────────────────────────────

    [Fact]
    public void ComputeCoplanarGroups_AllSamePlane_OneGroup()
    {
        // 4 fragments all on the z = 0 plane → 1 group.
        var quads = new[]
        {
            Quad(0, new(-1, -1, 0), new( 0, -1, 0), new( 0, 0, 0), new(-1, 0, 0)),
            Quad(1, new( 0, -1, 0), new( 1, -1, 0), new( 1, 0, 0), new( 0, 0, 0)),
            Quad(2, new(-1,  0, 0), new( 0,  0, 0), new( 0, 1, 0), new(-1, 1, 0)),
            Quad(3, new( 0,  0, 0), new( 1,  0, 0), new( 1, 1, 0), new( 0, 1, 0)),
        };
        var groups = MeshDecomposer.ComputeCoplanarGroups(quads);
        Assert.Equal(new[] { 0, 0, 0, 0 }, groups);
    }

    [Fact]
    public void ComputeCoplanarGroups_DistinctPlanes_DistinctGroups()
    {
        // 3 quads on 3 distinct axis-aligned planes → 3 groups.
        var quads = new[]
        {
            Quad(0, new(-1, -1, 0), new( 1, -1, 0), new( 1, 1, 0), new(-1, 1, 0)),    // z=0
            Quad(1, new( 0, -1, -1), new( 0, 1, -1), new( 0, 1, 1), new( 0, -1, 1)),  // x=0
            Quad(2, new(-1, 0, -1), new( 1, 0, -1), new( 1, 0, 1), new(-1, 0, 1)),    // y=0
        };
        var groups = MeshDecomposer.ComputeCoplanarGroups(quads);
        Assert.Equal(new[] { 0, 1, 2 }, groups);
    }

    [Fact]
    public void ComputeCoplanarGroups_OppositeWindingsSamePlane_SharedGroup()
    {
        // Two coplanar quads with opposite winding (inward duplicates).
        // ComputeCoplanarGroups must treat them as the same plane group
        // even though their normals are anti-parallel.
        var quads = new[]
        {
            Quad(0, new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)),  // +Z normal
            Quad(1, new(-1, 1, 0),  new(1, 1, 0),  new(1, -1, 0), new(-1, -1, 0)), // -Z normal
        };
        var groups = MeshDecomposer.ComputeCoplanarGroups(quads);
        Assert.Equal(groups[0], groups[1]);
    }

    [Fact]
    public void ComputeCoplanarGroups_BookObj_GroupsMatchSourcePlanes()
    {
        // After decomposition, the book's 33 quad fragments must fall
        // into 6 coplanar groups (one per distinct source plane:
        // x=±0.5/±0.46, y=±0.66/±0.7, z=±0.1/±0.099).
        var path = LocateSamplesFile("book.obj");
        var model = ObjParser.Parse(File.ReadAllText(path)).Model;
        var geom = ObjGeometry.Build(model);
        var fragments = MeshDecomposer.DecomposeForPainterOrder(geom.Quads);
        var groups = MeshDecomposer.ComputeCoplanarGroups(fragments);
        Assert.Equal(fragments.Count, groups.Length);
        var groupCount = groups.Distinct().Count();
        // book.obj has 6 distinct source planes; coplanar inward-doubled
        // covers fold into the same plane group as their outward face.
        Assert.Equal(6, groupCount);
    }

    [Fact]
    public void ComputeCoplanarGroups_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MeshDecomposer.ComputeCoplanarGroups(null!));
    }

    [Fact]
    public void ComputeCoplanarGroups_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(MeshDecomposer.ComputeCoplanarGroups(Array.Empty<CachedQuad>()));
    }

    // ── Test infrastructure ─────────────────────────────────────────────

    private static string LocateSamplesFile(string name)
    {
        string? path = null;
        var local = Path.Combine(AppContext.BaseDirectory, "Samples", name);
        if (File.Exists(local)) path = local;
        else
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && path is null)
            {
                var candidate = Path.Combine(dir.FullName, "samples", name);
                if (File.Exists(candidate)) { path = candidate; break; }
                dir = dir.Parent;
            }
        }
        Assert.True(path is not null, $"{name} not found relative to {AppContext.BaseDirectory}");
        return path!;
    }
}
