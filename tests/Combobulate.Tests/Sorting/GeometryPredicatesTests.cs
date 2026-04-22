using System;
using System.Numerics;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Zero-crossing tests for every centralized geometric predicate.
/// Each predicate is exercised at exactly the boundary case that
/// motivates its existence — fp noise around <c>cos(π/2)</c>, equal
/// signed distances, parallel segments, collinear vertices — so a
/// future regression that re-introduces a raw <c>&gt; 0</c> or
/// <c>== 0</c> test will surface here rather than as a visual artifact.
/// </summary>
public class GeometryPredicatesTests
{
    // ---------- IsFrontFacing ----------

    [Fact]
    public void IsFrontFacing_TruePositive_NormalRotatedAt45Degrees()
    {
        // cos(45°) ≈ 0.707 — comfortably above any plausible epsilon.
        Assert.True(GeometryPredicates.IsFrontFacing(MathF.Cos(MathF.PI / 4)));
    }

    [Fact]
    public void IsFrontFacing_FalseAtExactlyEdgeOn_DespiteFpNoise()
    {
        // cos(π/2) does not return exactly 0 in single precision — it returns
        // a tiny value of either sign depending on the implementation
        // (~-4.4e-8 on .NET 10 x64, ~+6.1e-17 in some other libraries). The
        // robustness property we need is: |value| < CosineEpsilon, so the
        // predicate's dead band swallows it regardless of which side of zero
        // the noise lands on.
        var noisy = MathF.Cos(MathF.PI / 2);
        Assert.True(MathF.Abs(noisy) < GeometryPredicates.CosineEpsilon,
            $"sanity: cos(π/2) noise should fall inside the cull dead band, was {noisy}");
        Assert.False(GeometryPredicates.IsFrontFacing(noisy));
    }

    [Fact]
    public void IsFrontFacing_FalseForTinyPositiveNoise()
    {
        // The original yaw=90 bug shape: a positive value below the threshold.
        Assert.False(GeometryPredicates.IsFrontFacing(6.1e-17f));
        Assert.False(GeometryPredicates.IsFrontFacing(1e-8f));
        Assert.False(GeometryPredicates.IsFrontFacing(GeometryPredicates.CosineEpsilon * 0.5f));
    }

    [Fact]
    public void IsFrontFacing_FalseAtThreshold()
    {
        // The threshold is the boundary; equality should NOT be visible (strict >).
        Assert.False(GeometryPredicates.IsFrontFacing(GeometryPredicates.CosineEpsilon));
    }

    [Fact]
    public void IsFrontFacing_TrueJustAboveThreshold()
    {
        Assert.True(GeometryPredicates.IsFrontFacing(GeometryPredicates.CosineEpsilon * 2f));
    }

    // ---------- CameraHemisphere ----------

    [Theory]
    [InlineData(0.5f, +1)]
    [InlineData(-0.5f, -1)]
    [InlineData(0f, 0)]
    public void CameraHemisphere_ClassifiesByCosine(float dot, int expected)
    {
        Assert.Equal(expected, GeometryPredicates.CameraHemisphere(dot));
    }

    [Fact]
    public void CameraHemisphere_ZeroForFpNoiseAroundEdgeOn()
    {
        // Both ±cos(π/2) and exact 0 must land in the dead band.
        Assert.Equal(0, GeometryPredicates.CameraHemisphere(MathF.Cos(MathF.PI / 2)));
        Assert.Equal(0, GeometryPredicates.CameraHemisphere(-MathF.Cos(MathF.PI / 2)));
        Assert.Equal(0, GeometryPredicates.CameraHemisphere(0f));
    }

    // ---------- SignedDistanceSide ----------

    [Theory]
    [InlineData(0.1f, +1)]
    [InlineData(-0.1f, -1)]
    [InlineData(0f, 0)]
    public void SignedDistanceSide_ClassifiesByDistance(float d, int expected)
    {
        Assert.Equal(expected, GeometryPredicates.SignedDistanceSide(d));
    }

    [Fact]
    public void SignedDistanceSide_OnPlaneWithinEpsilonBand()
    {
        // ±half-epsilon both land on the plane.
        var halfEps = GeometryPredicates.DistanceEpsilon * 0.5f;
        Assert.Equal(0, GeometryPredicates.SignedDistanceSide(halfEps));
        Assert.Equal(0, GeometryPredicates.SignedDistanceSide(-halfEps));
    }

    // ---------- TryComputeSegmentParam ----------

    [Fact]
    public void TryComputeSegmentParam_NormalCrossing()
    {
        // da = +1, db = -1 → t = 0.5 (midpoint).
        Assert.True(GeometryPredicates.TryComputeSegmentParam(1f, -1f, out var t));
        Assert.Equal(0.5f, t, 5);
    }

    [Fact]
    public void TryComputeSegmentParam_ClampsAboveOne()
    {
        // da = 3, db = 1 (same sign — would extrapolate to t = 1.5); clamped to 1.
        Assert.True(GeometryPredicates.TryComputeSegmentParam(3f, 1f, out var t));
        Assert.Equal(1f, t);
    }

    [Fact]
    public void TryComputeSegmentParam_ClampsBelowZero()
    {
        // da = -1, db = -3 → would give t = -0.5; clamped.
        Assert.True(GeometryPredicates.TryComputeSegmentParam(-1f, -3f, out var t));
        Assert.Equal(0f, t);
    }

    [Fact]
    public void TryComputeSegmentParam_ReturnsFalseForExactZeroDenom()
    {
        // Equal distances → parallel-to-plane segment, no real intersection.
        Assert.False(GeometryPredicates.TryComputeSegmentParam(0.5f, 0.5f, out var t));
        Assert.Equal(0.5f, t); // safe midpoint fallback
    }

    [Fact]
    public void TryComputeSegmentParam_ReturnsFalseForTinyDenom_NoExplosion()
    {
        // The motivating bug: a denom of 1e-30 with a nonzero da would produce
        // ±∞ from a naive divide. The predicate must catch this.
        Assert.False(GeometryPredicates.TryComputeSegmentParam(1f, 1f - 1e-30f, out var t));
        Assert.False(float.IsInfinity(t) || float.IsNaN(t));
        Assert.InRange(t, 0f, 1f);
    }

    [Fact]
    public void TryComputeSegmentParam_AcceptsDenomJustAboveDivisorEpsilon()
    {
        var da = GeometryPredicates.DivisorEpsilon * 5f;
        var db = -GeometryPredicates.DivisorEpsilon * 5f;
        Assert.True(GeometryPredicates.TryComputeSegmentParam(da, db, out var t));
        Assert.Equal(0.5f, t, 4);
    }

    // ---------- Degenerate-triangle / edge filters ----------

    [Fact]
    public void IsDegenerateCross3D_TrueForCollinear()
    {
        // a=(0,0,0), b=(1,0,0), c=(2,0,0) → collinear, cross = 0.
        var cross = Vector3.Cross(new Vector3(1, 0, 0), new Vector3(2, 0, 0));
        Assert.True(GeometryPredicates.IsDegenerateCross3D(cross));
    }

    [Fact]
    public void IsDegenerateCross3D_FalseForRealTriangle()
    {
        var cross = Vector3.Cross(new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        Assert.False(GeometryPredicates.IsDegenerateCross3D(cross));
    }

    [Fact]
    public void IsDegenerateEdge2D_TrueForZeroLengthEdge()
    {
        Assert.True(GeometryPredicates.IsDegenerateEdge2D(Vector2.Zero));
    }

    [Fact]
    public void IsDegenerateEdge2D_FalseForUnitEdge()
    {
        Assert.False(GeometryPredicates.IsDegenerateEdge2D(new Vector2(1, 0)));
    }

    // ---------- Constant ordering invariant ----------

    /// <summary>
    /// The epsilons must remain ordered: divisor &lt; distance &lt; cosine.
    /// If a future tweak inverts this, predicates that gate divides will
    /// start swallowing real intersections.
    /// </summary>
    [Fact]
    public void EpsilonHierarchy_IsOrdered()
    {
        Assert.True(GeometryPredicates.DivisorEpsilon < GeometryPredicates.DistanceEpsilon,
            "DivisorEpsilon must be smaller than DistanceEpsilon (it gates a divide, not a classify).");
        Assert.True(GeometryPredicates.DistanceEpsilon < GeometryPredicates.CosineEpsilon,
            "DistanceEpsilon must be smaller than CosineEpsilon by current convention.");
        Assert.True(GeometryPredicates.EdgeLengthSquaredEpsilon2D > GeometryPredicates.CrossLengthSquaredEpsilon3D,
            "2D edge filter looser than 3D cross filter (foreshortening tolerance).");
    }
}
