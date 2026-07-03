using Combobulate.Caching;
using System.Numerics;

namespace Combobulate.Tests.Caching;

/// <summary>
/// Tests for <see cref="BrushTransformMath"/>: the pure-math affine
/// constructions for quad axis-aligned crops and exact triangle 3-point
/// affines used by the per-face brush <see cref="Matrix3x2"/>.
/// </summary>
public class BrushTransformMathTests
{
    private static Vector2 ApplyAffine(Matrix3x2 m, Vector2 p)
    {
        return new Vector2(
            m.M11 * p.X + m.M21 * p.Y + m.M31,
            m.M12 * p.X + m.M22 * p.Y + m.M32);
    }

    [Fact]
    public void TriangleAffine_MapsBrushCornersToUvs_DefaultMaterial()
    {
        var uv0 = new Vector2(0.1f, 0.2f);
        var uv1 = new Vector2(0.7f, 0.3f);
        var uv2 = new Vector2(0.2f, 0.9f);
        var m = BrushTransformMath.BuildTriangleAffine(
            uv0, uv1, uv2, uvScale: Vector2.One, uvOffset: Vector2.Zero,
            spriteSize: Vector2.One);

        // No V-flip (surface loader is v-up): brush(0,0) -> (u0.X, u0.Y)
        var p00 = ApplyAffine(m, new Vector2(0, 0));
        Assert.Equal(uv0.X, p00.X, 5);
        Assert.Equal(uv0.Y, p00.Y, 5);

        // brush(1,0) -> (u1.X, u1.Y)
        var p10 = ApplyAffine(m, new Vector2(1, 0));
        Assert.Equal(uv1.X, p10.X, 5);
        Assert.Equal(uv1.Y, p10.Y, 5);

        // brush(0,1) -> (u2.X, u2.Y)
        var p01 = ApplyAffine(m, new Vector2(0, 1));
        Assert.Equal(uv2.X, p01.X, 5);
        Assert.Equal(uv2.Y, p01.Y, 5);
    }

    [Fact]
    public void TriangleAffine_DefaultUvsProduceIdentity()
    {
        // Default UVs from ObjGeometry.Build for a triangle: (0,0), (1,0), (0,1).
        // The brush transform maps brush coord directly to UV (no V-flip):
        //   brush(0,0) -> (0, 0)
        //   brush(1,0) -> (1, 0)
        //   brush(0,1) -> (0, 1)
        var m = BrushTransformMath.BuildTriangleAffine(
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
            Vector2.One, Vector2.Zero, Vector2.One);

        Assert.Equal(new Vector2(0, 0), ApplyAffine(m, new Vector2(0, 0)));
        Assert.Equal(new Vector2(1, 0), ApplyAffine(m, new Vector2(1, 0)));
        Assert.Equal(new Vector2(0, 1), ApplyAffine(m, new Vector2(0, 1)));
    }

    [Fact]
    public void TriangleAffine_AppliesUvScaleAsLinearMultiplier()
    {
        var m = BrushTransformMath.BuildTriangleAffine(
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
            uvScale: new Vector2(2f, 3f), uvOffset: Vector2.Zero,
            spriteSize: Vector2.One);

        // The linear deltas double in X and triple in Y; translation unchanged
        // for spriteSize=1.
        Assert.Equal(2f, m.M11, 5);
        Assert.Equal(3f, m.M22, 5); // scale.Y (no V-flip)
        Assert.Equal(0f, m.M31, 5);
        Assert.Equal(0f, m.M32, 5);
    }

    [Fact]
    public void TriangleAffine_AppliesUvOffsetAsTranslation()
    {
        var m = BrushTransformMath.BuildTriangleAffine(
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
            uvScale: Vector2.One, uvOffset: new Vector2(0.25f, -0.5f),
            spriteSize: Vector2.One);

        Assert.Equal(0f + 0.25f, m.M31, 5);
        Assert.Equal(0f + -0.5f, m.M32, 5);
    }

    [Fact]
    public void TriangleAffine_ExactForArbitraryUvs()
    {
        // Pick non-axis-aligned UVs (e.g. a rotated UV island) and verify the
        // affine reproduces the UV exactly at all three corners (no V-flip).
        var uv0 = new Vector2(0.3f, 0.4f);
        var uv1 = new Vector2(0.6f, 0.55f);
        var uv2 = new Vector2(0.45f, 0.8f);
        var m = BrushTransformMath.BuildTriangleAffine(
            uv0, uv1, uv2, Vector2.One, Vector2.Zero, Vector2.One);

        Assert.Equal(uv0.X, ApplyAffine(m, new Vector2(0, 0)).X, 5);
        Assert.Equal(uv0.Y, ApplyAffine(m, new Vector2(0, 0)).Y, 5);
        Assert.Equal(uv1.X, ApplyAffine(m, new Vector2(1, 0)).X, 5);
        Assert.Equal(uv1.Y, ApplyAffine(m, new Vector2(1, 0)).Y, 5);
        Assert.Equal(uv2.X, ApplyAffine(m, new Vector2(0, 1)).X, 5);
        Assert.Equal(uv2.Y, ApplyAffine(m, new Vector2(0, 1)).Y, 5);
    }

    [Fact]
    public void TriangleAffine_BarycentricInteriorPointMapsCorrectly()
    {
        // The brush-space centroid (1/3, 1/3) should map to the UV-space
        // centroid of the triangle UVs (no V-flip), by linearity of the
        // affine.
        var uv0 = new Vector2(0.2f, 0.3f);
        var uv1 = new Vector2(0.8f, 0.2f);
        var uv2 = new Vector2(0.4f, 0.9f);
        var m = BrushTransformMath.BuildTriangleAffine(
            uv0, uv1, uv2, Vector2.One, Vector2.Zero, Vector2.One);

        var centroidBrush = new Vector2(1f / 3f, 1f / 3f);
        var centroidUv = (uv0 + uv1 + uv2) / 3f;

        var got = ApplyAffine(m, centroidBrush);
        Assert.Equal(centroidUv.X, got.X, 5);
        Assert.Equal(centroidUv.Y, got.Y, 5);
    }

    [Fact]
    public void QuadAxisAlignedCrop_StandardUnitSquareProducesIdentity()
    {
        var m = BrushTransformMath.BuildQuadAxisAlignedCrop(
            new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            Vector2.One, Vector2.Zero, Vector2.One);
        // Canonical UVs map directly (no V-flip; surface loader is v-up) →
        // Matrix(1, 0, 0, 1, 0, 0), identical to the triangle path for the
        // same UVs.
        Assert.Equal(1f, m.M11, 5);
        Assert.Equal(0f, m.M12, 5);
        Assert.Equal(0f, m.M21, 5);
        Assert.Equal(1f, m.M22, 5);
        Assert.Equal(0f, m.M31, 5);
        Assert.Equal(0f, m.M32, 5);
    }

    [Fact]
    public void TriangleAffine_TranslationScaledBySpriteSize()
    {
        // Verifies the critical CompositionBrush.TransformMatrix unit
        // semantics: translation is expressed in sprite-brush pixels, not
        // normalised UV. The same UV layout must produce different
        // translations for different sprite sizes.
        var uv0 = new Vector2(0.25f, 0.25f);
        var uv1 = new Vector2(0.75f, 0.25f);
        var uv2 = new Vector2(0.25f, 0.75f);
        var spriteSize = new Vector2(200f, 100f);

        var m = BrushTransformMath.BuildTriangleAffine(
            uv0, uv1, uv2, Vector2.One, Vector2.Zero, spriteSize);

        // Linear part unchanged by sprite size.
        Assert.Equal(0.5f, m.M11, 5);
        Assert.Equal(0.5f, m.M22, 5);
        // Translation = (uv0.X, uv0.Y) × spriteSize = (0.25 × 200, 0.25 × 100).
        Assert.Equal(0.25f * 200f, m.M31, 5);
        Assert.Equal(0.25f * 100f, m.M32, 5);
    }

    [Fact]
    public void TriangleAffine_NegativeDiagonalsLandInsideBrushPixelRect()
    {
        // Regression: subdivided triangles with V-flipped UVs commonly produce
        // matrices with negative diagonals. CompositionBrush.TransformMatrix
        // renders blank when the transformed brush rect leaves the surface;
        // multiplying the translation by sprite size keeps the sampled rectangle
        // anchored inside [0, spriteSize].
        var uv0 = new Vector2(0.5f, 0.5f);
        var uv1 = new Vector2(0.6f, 0.5f);
        var uv2 = new Vector2(0.5f, 0.6f);
        var spriteSize = new Vector2(158f, 158f);

        var m = BrushTransformMath.BuildTriangleAffine(
            uv0, uv1, uv2, Vector2.One, Vector2.Zero, spriteSize);

        // For sprite pixel coordinates within the triangle the sampled brush
        // pixel must lie inside [0, spriteSize] so the surface is sampled
        // rather than returning transparent off-surface texels.
        foreach (var p in new[] {
            new Vector2(0, 0),
            new Vector2(spriteSize.X, 0),
            new Vector2(0, spriteSize.Y) })
        {
            var sample = ApplyAffine(m, p);
            Assert.InRange(sample.X, 0f, spriteSize.X);
            Assert.InRange(sample.Y, 0f, spriteSize.Y);
        }
    }

    [Fact]
    public void QuadAxisAlignedCrop_TranslationScaledBySpriteSize()
    {
        var spriteSize = new Vector2(300f, 150f);
        var m = BrushTransformMath.BuildQuadAxisAlignedCrop(
            new Vector2(0.25f, 0.10f), new Vector2(0.75f, 0.10f),
            new Vector2(0.75f, 0.90f), new Vector2(0.25f, 0.90f),
            Vector2.One, Vector2.Zero, spriteSize);

        // Linear part scales unchanged (no V-flip on the Y basis).
        Assert.Equal(0.5f, m.M11, 5);
        Assert.Equal(0.8f, m.M22, 5);
        // Translation = uv0.X × spriteSize.X, uv0.Y × spriteSize.Y.
        Assert.Equal(0.25f * 300f, m.M31, 5);
        Assert.Equal(0.10f * 150f, m.M32, 5);
    }

    [Theory]
    // 0° canonical winding: V0/V1/V2/V3 ↔ (0,0)(1,0)(1,1)(0,1)
    [InlineData(0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 1.0f, 0.0f, 1.0f)]
    // 90° CCW: walk started one corner later → V0/V1/V2/V3 ↔ (1,0)(1,1)(0,1)(0,0)
    [InlineData(1.0f, 0.0f, 1.0f, 1.0f, 0.0f, 1.0f, 0.0f, 0.0f)]
    // 180°: V0/V1/V2/V3 ↔ (1,1)(0,1)(0,0)(1,0)
    [InlineData(1.0f, 1.0f, 0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f)]
    // 270° CCW: V0/V1/V2/V3 ↔ (0,1)(0,0)(1,0)(1,1)
    [InlineData(0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 1.0f)]
    // Asymmetric crop with rotated winding (the actual book.obj sub-quad case)
    [InlineData(0.96f, 0.971f, 0.96f, 1.0f, 0.0f, 1.0f, 0.0f, 0.971f)]
    [InlineData(0.0f, 0.971f, 0.96f, 0.971f, 0.96f, 1.0f, 0.0f, 1.0f)]
    public void QuadAxisAlignedCrop_AllWindingRotations_MapCornersToUVs(
        float u0X, float u0Y, float u1X, float u1Y,
        float u2X, float u2Y, float u3X, float u3Y)
    {
        var uv0 = new Vector2(u0X, u0Y);
        var uv1 = new Vector2(u1X, u1Y);
        var uv2 = new Vector2(u2X, u2Y);
        var uv3 = new Vector2(u3X, u3Y);
        var spriteSize = new Vector2(120f, 60f); // asymmetric to catch axis-confusion bugs

        var m = BrushTransformMath.BuildQuadAxisAlignedCrop(
            uv0, uv1, uv2, uv3, Vector2.One, Vector2.Zero, spriteSize);

        // The Y axis maps directly (surface sample = uv.Y × spriteSize.Y; no V-flip).
        Assert.Equal(uv0.X * spriteSize.X, ApplyAffine(m, new Vector2(0, 0)).X, 3);
        Assert.Equal(uv0.Y * spriteSize.Y, ApplyAffine(m, new Vector2(0, 0)).Y, 3);
        Assert.Equal(uv1.X * spriteSize.X, ApplyAffine(m, new Vector2(spriteSize.X, 0)).X, 3);
        Assert.Equal(uv1.Y * spriteSize.Y, ApplyAffine(m, new Vector2(spriteSize.X, 0)).Y, 3);
        Assert.Equal(uv3.X * spriteSize.X, ApplyAffine(m, new Vector2(0, spriteSize.Y)).X, 3);
        Assert.Equal(uv3.Y * spriteSize.Y, ApplyAffine(m, new Vector2(0, spriteSize.Y)).Y, 3);
        // V2 corner (implied by parallelogram): uv2 should be at sprite-(W,H).
        Assert.Equal(uv2.X * spriteSize.X, ApplyAffine(m, spriteSize).X, 3);
        Assert.Equal(uv2.Y * spriteSize.Y, ApplyAffine(m, spriteSize).Y, 3);
    }

    [Fact]
    public void QuadAxisAlignedCrop_AnyWinding_MapsSpriteCornersToUvCorners()
    {
        var uv0 = new Vector2(0.96f, 0.971f);  // sub-quad's V0 corner
        var uv1 = new Vector2(0.96f, 1.0f);    // V1 corner
        var uv2 = new Vector2(0.0f, 1.0f);     // V2 corner (implied)
        var uv3 = new Vector2(0.0f, 0.971f);   // V3 corner
        var spriteSize = new Vector2(96f, 2.9f);
        var m = BrushTransformMath.BuildQuadAxisAlignedCrop(
            uv0, uv1, uv2, uv3, Vector2.One, Vector2.Zero, spriteSize);

        var p00 = ApplyAffine(m, new Vector2(0, 0));
        Assert.Equal(uv0.X * spriteSize.X, p00.X, 3);
        Assert.Equal(uv0.Y * spriteSize.Y, p00.Y, 3);

        var pW0 = ApplyAffine(m, new Vector2(spriteSize.X, 0));
        Assert.Equal(uv1.X * spriteSize.X, pW0.X, 3);
        Assert.Equal(uv1.Y * spriteSize.Y, pW0.Y, 3);

        var p0H = ApplyAffine(m, new Vector2(0, spriteSize.Y));
        Assert.Equal(uv3.X * spriteSize.X, p0H.X, 3);
        Assert.Equal(uv3.Y * spriteSize.Y, p0H.Y, 3);
    }

    [Fact]
    public void QuadAxisAlignedCrop_NonZeroUv0_DoesNotSampleFromOrigin()
    {
        // Regression: prior implementation used `ty = (1 - vMax)` regardless
        // of uv0.y. For a sub-quad whose uv0 sits in the interior of the
        // source texture (uv0=(0.96, 0.971), uv-bbox=[0..0.96]×[0.971..1]),
        // the prior formula returned ty=(1-1)=0 — so sprite-(0,0) sampled
        // the TEXTURE TOP-LEFT instead of uv0. Effect: the floating "thumbnail"
        // sub-quad bug where a corner sub-quad showed the full cover image
        // crammed into its small screen rectangle.
        var uv0 = new Vector2(0.96f, 0.971f);
        var uv1 = new Vector2(0.96f, 1.0f);
        var uv2 = new Vector2(0.0f, 1.0f);
        var uv3 = new Vector2(0.0f, 0.971f);
        var spriteSize = new Vector2(50f, 50f);
        var m = BrushTransformMath.BuildQuadAxisAlignedCrop(
            uv0, uv1, uv2, uv3, Vector2.One, Vector2.Zero, spriteSize);

        var p00 = ApplyAffine(m, new Vector2(0, 0));
        // sprite-(0,0) must sample texture at uv0, NOT (0, 0).
        Assert.NotEqual(0f, p00.X, 3);
        Assert.Equal(uv0.X * spriteSize.X, p00.X, 3);
        Assert.Equal(uv0.Y * spriteSize.Y, p00.Y, 3);
    }
}
