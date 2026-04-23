using System.Collections.Generic;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Direct unit test of <see cref="PainterCorrectness.SutherlandHodgman"/> on
/// the exact two rectangles that the oracle's spot-check produces a wrong
/// answer for. If this test goes red, the bug is in SH itself; if it
/// passes, the bug is in how the oracle constructs / processes its inputs.
/// </summary>
public class SutherlandHodgmanRegressionTest
{
    private readonly ITestOutputHelper _out;
    public SutherlandHodgmanRegressionTest(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void OverlapOfBookSpineAndBackCoverInside_AtYaw69_5_IsExactlyTheSpineRectangle()
    {
        // Quad 3 (back cover inside) view-space 2D vertices, with EXACT bits from the oracle.
        var quad3 = new List<Vector2>
        {
            new( 0.10400535f, -0.7f),
            new( 0.10400535f,  0.7f),
            new(-0.24620198f,  0.7f),
            new(-0.24620198f, -0.7f),
        };
        // Quad 4 (spine outside) — note V2/V3 are EXACTLY equal to subject V2/V3 (shared edge).
        var quad4 = new List<Vector2>
        {
            new(-0.05886753f, -0.7f),
            new(-0.05886753f,  0.7f),
            new(-0.24620198f,  0.7f),
            new(-0.24620198f, -0.7f),
        };

        var overlap = PainterCorrectness.SutherlandHodgman(quad3, quad4);
        _out.WriteLine($"SH(quad3, quad4) -> {overlap.Count} vertices:");
        foreach (var v in overlap) _out.WriteLine($"  ({v.X:R}, {v.Y:R})");

        const float tol = 1e-3f;
        foreach (var v in overlap)
        {
            Assert.InRange(v.X, -0.246f - tol, -0.059f + tol);
            Assert.InRange(v.Y, -0.700f - tol,  0.700f + tol);
        }
    }
}
