using System.Numerics;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>Plane3 unit tests — the math primitive everything else builds on.</summary>
public class Plane3Tests
{
    [Fact]
    public void FromPointAndNormal_RecoversD()
    {
        var p = new Vector3(1, 2, 3);
        var n = Vector3.Normalize(new Vector3(0, 1, 0));
        var plane = Plane3.FromPointAndNormal(p, n);
        Assert.Equal(2f, plane.D, 5);
        Assert.Equal(0f, plane.SignedDistance(new Vector3(7, 2, 9)), 5);
    }

    [Fact]
    public void FromTriangle_NormalIsUnitAndOrientedByWinding()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(1, 0, 0);
        var c = new Vector3(0, 1, 0);
        var plane = Plane3.FromTriangle(a, b, c);
        Assert.Equal(1f, plane.Normal.Length(), 4);
        Assert.True(plane.Normal.Z > 0); // CCW from +Z gives +Z normal.
    }

    [Fact]
    public void SignedDistance_PositiveOnNormalSide_NegativeOnOpposite()
    {
        var plane = new Plane3(new Vector3(0, 0, 1), 0); // z = 0 plane
        Assert.True(plane.SignedDistance(new Vector3(0, 0, +1)) > 0);
        Assert.True(plane.SignedDistance(new Vector3(0, 0, -1)) < 0);
        Assert.Equal(0f, plane.SignedDistance(new Vector3(1, 1, 0)), 5);
    }

    [Fact]
    public void IntersectSegment_ReturnsMidpointForSymmetricCrossing()
    {
        var plane = new Plane3(new Vector3(0, 0, 1), 0);
        var p = plane.IntersectSegment(new Vector3(0, 0, -1), new Vector3(0, 0, +1), out var t);
        Assert.Equal(0.5f, t, 4);
        Assert.Equal(Vector3.Zero, p);
    }

    [Fact]
    public void IntersectSegment_ClampsToSegmentBounds()
    {
        var plane = new Plane3(new Vector3(0, 0, 1), 0);
        // Both endpoints on the same side (parallel-ish): caller violates contract.
        // Result should be clamped to [0, 1].
        var p = plane.IntersectSegment(new Vector3(0, 0, +1), new Vector3(0, 0, +0.5f), out var t);
        Assert.InRange(t, 0f, 1f);
    }
}
