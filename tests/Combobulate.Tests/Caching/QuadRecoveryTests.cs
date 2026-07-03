using Combobulate.Caching;
using System.Numerics;
using Windows.UI;

namespace Combobulate.Tests.Caching;

/// <summary>
/// Tests for <see cref="QuadRecovery"/>, the preprocess that fuses coplanar
/// adjacent triangle pairs back into single quads so they hit the existing
/// 1-sprite-per-quad fast path.
/// </summary>
public class QuadRecoveryTests
{
    /// <summary>Helper: build a triangle CachedQuad from three vertices,
    /// computing the outward normal and using default unit-triangle UVs.
    /// Defaults to <c>hasExplicitUv=true</c> so the UV continuity check fires
    /// in tests — explicitly pass <c>hasExplicitUv: false</c> to exercise the
    /// implicit-UV permissive path.</summary>
    private static CachedQuad Tri(int srcIdx, Vector3 a, Vector3 b, Vector3 c,
        string? material = null,
        Vector2? uvA = null, Vector2? uvB = null, Vector2? uvC = null,
        bool hasExplicitUv = true)
    {
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        return CachedQuad.Triangle(srcIdx, a, b, c, normal, Color.FromArgb(255, 0, 0, 0),
            material,
            uvA ?? new Vector2(0, 0),
            uvB ?? new Vector2(1, 0),
            uvC ?? new Vector2(0, 1),
            hasExplicitUv: hasExplicitUv);
    }

    [Fact]
    public void FusesTwoCcwAdjacentTrianglesIntoQuad()
    {
        // Unit square in z=0 plane, split along diagonal (0,0)-(1,1).
        // No explicit UVs — recovery should fuse on geometry alone.
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            hasExplicitUv: false);
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            hasExplicitUv: false);

        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out var leftovers);

        var q = Assert.Single(quads);
        Assert.Empty(leftovers);
        Assert.False(q.IsTriangle);
        // No explicit UVs in: the result should get canonical unit-square UVs.
        Assert.False(q.HasExplicitUv);
        var allUv = new[] { q.Uv0, q.Uv1, q.Uv2, q.Uv3 };
        Assert.Contains(new Vector2(0, 0), allUv);
        Assert.Contains(new Vector2(1, 0), allUv);
        Assert.Contains(new Vector2(1, 1), allUv);
        Assert.Contains(new Vector2(0, 1), allUv);

        // Re-triangulating the recovered quad along V0->V2 must reproduce the
        // two source triangles (positions only).
        var rtri1Pos = new[] { q.V0, q.V1, q.V2 };
        var rtri2Pos = new[] { q.V0, q.V2, q.V3 };
        var allowed = new[] { t1.V0, t1.V1, t1.V2, t2.V0, t2.V1, t2.V2 };
        AssertTriangleCovers(rtri1Pos, allowed);
        AssertTriangleCovers(rtri2Pos, allowed);

        // Quad normal must agree with the original triangle normals.
        Assert.True(Vector3.Dot(q.Normal, t1.Normal) > 0.999f);

        // Centroid is the average of the four corners.
        var expectedCentroid = (q.V0 + q.V1 + q.V2 + q.V3) * 0.25f;
        Assert.True((q.Centroid - expectedCentroid).Length() < 1e-5f);
    }

    [Fact]
    public void DoesNotFuseTrianglesWithDifferentMaterials()
    {
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            material: "matA", hasExplicitUv: false);
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            material: "matB", hasExplicitUv: false);

        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out var leftovers);

        Assert.Empty(quads);
        Assert.Equal(2, leftovers.Count);
        Assert.True(leftovers[0].IsTriangle);
        Assert.True(leftovers[1].IsTriangle);
    }

    [Fact]
    public void DoesNotFuseNonCoplanarTriangles()
    {
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            hasExplicitUv: false);
        var t2 = Tri(1, new Vector3(1, 0, 0), new Vector3(0, 0, 0), new Vector3(0, 0, 1),
            hasExplicitUv: false);

        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out var leftovers);

        Assert.Empty(quads);
        Assert.Equal(2, leftovers.Count);
    }

    [Fact]
    public void DoesNotFuseTrianglesWithoutSharedEdge()
    {
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            hasExplicitUv: false);
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, -1, 0),
            hasExplicitUv: false);

        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out var leftovers);

        Assert.Empty(quads);
        Assert.Equal(2, leftovers.Count);
    }

    [Fact]
    public void DoesNotFuseTrianglesWithSameOrientationAcrossSharedEdge()
    {
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            hasExplicitUv: false);
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0.5f, -1, 0),
            hasExplicitUv: false);

        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out var leftovers);

        Assert.Empty(quads);
        Assert.Equal(2, leftovers.Count);
    }

    [Fact]
    public void DoesNotFuseTrianglesWithMismatchedUvAtSharedEdge()
    {
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            uvA: new Vector2(0, 0), uvB: new Vector2(1, 0), uvC: new Vector2(1, 1));
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            uvA: new Vector2(0, 0), uvB: new Vector2(0.5f, 0.5f) /* wrong */, uvC: new Vector2(0, 1));

        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out var leftovers);

        Assert.Empty(quads);
        Assert.Equal(2, leftovers.Count);
    }

    [Fact]
    public void FusesTrianglesWithMatchingUvAtSharedEdge()
    {
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            uvA: new Vector2(0, 0), uvB: new Vector2(1, 0), uvC: new Vector2(1, 1));
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            uvA: new Vector2(0, 0), uvB: new Vector2(1, 1), uvC: new Vector2(0, 1));

        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out var leftovers);

        var q = Assert.Single(quads);
        Assert.Empty(leftovers);

        // The four UVs of the recovered quad should be the four distinct UVs.
        var allUv = new[] { q.Uv0, q.Uv1, q.Uv2, q.Uv3 };
        Assert.Contains(new Vector2(0, 0), allUv);
        Assert.Contains(new Vector2(1, 0), allUv);
        Assert.Contains(new Vector2(1, 1), allUv);
        Assert.Contains(new Vector2(0, 1), allUv);
    }

    [Fact]
    public void DoesNotFuseTrianglesProducingNonConvexQuad()
    {
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(1, 1, 0),
            hasExplicitUv: false);
        var t2 = Tri(1, new Vector3(2, 0, 0), new Vector3(0, 0, 0), new Vector3(1, 0.3f, 0),
            hasExplicitUv: false);

        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out var leftovers);

        Assert.Empty(quads);
        Assert.Equal(2, leftovers.Count);
    }

    [Fact]
    public void GreedyPairingIsDeterministic()
    {
        var t0 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            hasExplicitUv: false);
        var t1 = Tri(1, new Vector3(1, 0, 0), new Vector3(0, 0, 0), new Vector3(0.5f, -1, 0),
            hasExplicitUv: false);
        var t2 = Tri(2, new Vector3(0, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, -1, 0),
            hasExplicitUv: false);

        QuadRecovery.Recover(new[] { t0, t1, t2 }, out var quads, out var leftovers);

        Assert.Single(quads);
        Assert.Single(leftovers);
        Assert.Equal(2, leftovers[0].SourceIndex);
    }

    [Fact]
    public void EmptyInputProducesEmptyOutput()
    {
        QuadRecovery.Recover(System.Array.Empty<CachedQuad>(), out var quads, out var leftovers);
        Assert.Empty(quads);
        Assert.Empty(leftovers);
    }

    [Fact]
    public void SingleTriangleLeftAsLeftover()
    {
        var t = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        QuadRecovery.Recover(new[] { t }, out var quads, out var leftovers);

        Assert.Empty(quads);
        var only = Assert.Single(leftovers);
        Assert.True(only.IsTriangle);
        Assert.Equal(0, only.SourceIndex);
    }

    [Fact]
    public void EachTrianglePairsAtMostOnce()
    {
        var t0 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            hasExplicitUv: false);
        var t1 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            hasExplicitUv: false);
        var t2 = Tri(2, new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 1, 0),
            hasExplicitUv: false);
        var t3 = Tri(3, new Vector3(1, 0, 0), new Vector3(2, 1, 0), new Vector3(1, 1, 0),
            hasExplicitUv: false);

        QuadRecovery.Recover(new[] { t0, t1, t2, t3 }, out var quads, out var leftovers);

        Assert.Equal(2, quads.Count);
        Assert.Empty(leftovers);
    }

    [Fact]
    public void NullMaterialsAreTreatedAsMatching()
    {
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            material: null, hasExplicitUv: false);
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            material: null, hasExplicitUv: false);
        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out var leftovers);
        Assert.Single(quads);
        Assert.Empty(leftovers);
    }

    [Fact]
    public void RecoveredQuadHasMaterialOfFirstTriangle()
    {
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            material: "shared", hasExplicitUv: false);
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            material: "shared", hasExplicitUv: false);
        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out _);
        Assert.Equal("shared", quads[0].MaterialName);
    }

    [Fact]
    public void ExplicitUvContinuityIsEnforcedWhenBothTrianglesHaveExplicitUv()
    {
        // Both explicit, UVs at shared vertices match → fuse.
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            uvA: new Vector2(0, 0), uvB: new Vector2(1, 0), uvC: new Vector2(1, 1),
            hasExplicitUv: true);
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            uvA: new Vector2(0, 0), uvB: new Vector2(1, 1), uvC: new Vector2(0, 1),
            hasExplicitUv: true);
        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out _);
        Assert.Single(quads);
        Assert.True(quads[0].HasExplicitUv);
    }

    [Fact]
    public void ExplicitUvContinuityIsNotEnforcedWhenEitherTriangleHasImplicitUv()
    {
        // t1 explicit, t2 implicit → UV check is skipped (no semantic continuity to enforce).
        var t1 = Tri(0, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0),
            uvA: new Vector2(0, 0), uvB: new Vector2(1, 0), uvC: new Vector2(1, 1),
            hasExplicitUv: true);
        var t2 = Tri(1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            hasExplicitUv: false);
        QuadRecovery.Recover(new[] { t1, t2 }, out var quads, out _);
        Assert.Single(quads);
        Assert.True(quads[0].HasExplicitUv); // at least one was explicit → mark recovered as explicit
    }

    /// <summary>Assert that all positions of <paramref name="triangle"/>
    /// appear in <paramref name="allowedPositions"/>.</summary>
    private static void AssertTriangleCovers(Vector3[] triangle, Vector3[] allowedPositions)
    {
        foreach (var p in triangle)
        {
            bool found = false;
            foreach (var a in allowedPositions)
                if ((p - a).LengthSquared() < 1e-8f) { found = true; break; }
            Assert.True(found, $"Triangle vertex {p} not present in allowed set.");
        }
    }
}
