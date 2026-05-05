using System.Numerics;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Regression tests for the back-face cull threshold. Quads viewed essentially
/// edge-on (cos of the view angle hovering at floating-point noise) used to
/// slip through the &gt;0 cull and smear their texture across the screen,
/// because <c>cos(MathF.PI / 2)</c> evaluates to ~6.1e-17 (positive) in single
/// precision. The fix is a small positive cull epsilon shared across all
/// sorter implementations.
/// </summary>
public class CullEpsilonTests
{
    private static readonly SortAlgorithm[] All =
    {
        SortAlgorithm.Topological,
        SortAlgorithm.Newell,
        SortAlgorithm.Bsp,
    };

    [Theory]
    [InlineData(SortAlgorithm.Topological)]
    [InlineData(SortAlgorithm.Newell)]
    [InlineData(SortAlgorithm.Bsp)]
    public void EdgeOnQuadAtYaw90_IsCulled(SortAlgorithm algo)
    {
        // Two parallel quads: cover at z=+0.1 (+Z normal) and back at z=-0.1 (-Z).
        // At yaw exactly 90°, both face +X (perpendicular to camera's view),
        // their viewNormal.Z components are at floating-point noise levels,
        // and they should be culled.
        var geom = TestGeometries.FrontAndBackQuads();
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        var rot = SortAssertions.Yaw(90);
        int n = sorter.Sort(rot, order, visible);

        Assert.Equal(0, n);
        Assert.False(visible[0], $"{algo}: front quad must be culled at yaw=90°");
        Assert.False(visible[1], $"{algo}: back quad must be culled at yaw=90°");
    }

    [Theory]
    [InlineData(SortAlgorithm.Topological)]
    [InlineData(SortAlgorithm.Newell)]
    [InlineData(SortAlgorithm.Bsp)]
    public void NearEdgeOnQuadAtYaw89_99_IsCulled(SortAlgorithm algo)
    {
        // Sub-cull-threshold: face is 0.01° from edge-on. viewNormal.Z ≈ 1.7e-4,
        // below the 1e-3 cull threshold. Should be culled.
        var geom = TestGeometries.FrontAndBackQuads();
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        int n = sorter.Sort(SortAssertions.Yaw(89.99f), order, visible);
        Assert.Equal(0, n);
    }

    [Theory]
    [InlineData(SortAlgorithm.Topological)]
    [InlineData(SortAlgorithm.Newell)]
    [InlineData(SortAlgorithm.Bsp)]
    public void GenuinelyVisibleQuadAtYaw89_IsRendered(SortAlgorithm algo)
    {
        // Above-threshold: face is 1° off edge-on. viewNormal.Z ≈ 0.0175, well
        // above 1e-3. Should render.
        var geom = TestGeometries.FrontAndBackQuads();
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        int n = sorter.Sort(SortAssertions.Yaw(89), order, visible);
        Assert.Equal(1, n);
        Assert.True(visible[0]);
    }

    [Fact]
    public void CullEpsilonIsPositiveAndAboveSinglePrecisionCosNoise()
    {
        // The whole point: must be > the noise floor of MathF.Cos(MathF.PI/2).
        Assert.True(FaceSorterFactory.CullEpsilon > 1e-6f, "Epsilon too tight; fp noise will leak through");
        Assert.True(FaceSorterFactory.CullEpsilon < 1e-2f, "Epsilon too loose; legitimate near-edge-on faces would be culled");
    }

    /// <summary>
    /// Regression for "double-sided cover faces vanish at pitch=±90°".
    ///
    /// <para>The Deet book model (and book.obj generally) emits each cover face TWICE
    /// with opposite windings — an outward face and an inward (back-side) companion —
    /// so the cover is visible from either side. At pitch=±90° both companions' rotated
    /// normals lie in the XY plane (Z=0), so the orthographic cull rejects them both
    /// and the cover disappears.</para>
    ///
    /// <para>Under perspective the camera is a finite point and the view ray from
    /// camera to a (now off-axis) cover centroid has a non-zero Y component, so the
    /// dot product with one of the two normals is solidly positive and that companion
    /// is correctly classified as front-facing.</para>
    ///
    /// <para>Two stacked, double-sided quads at z=±0.5 (mirroring the book's front and
    /// back covers). At pitch=90°: the front-cover-OUTSIDE and back-cover-OUTSIDE faces
    /// rotate to point away from the camera; the front-cover-INSIDE and back-cover-INSIDE
    /// faces rotate to point toward the off-axis view rays. Exactly two of the four
    /// quads should survive perspective cull.</para>
    /// </summary>
    [Theory]
    [InlineData(SortAlgorithm.Topological)]
    [InlineData(SortAlgorithm.Newell)]
    [InlineData(SortAlgorithm.Bsp)]
    public void DoubleSidedCovers_AtPitch90_VisibleUnderPerspective_AllCulledUnderOrtho(SortAlgorithm algo)
    {
        // Two parallel covers at z=±0.5, each emitted with both windings (outward + inward).
        // Quad indices: 0 = front outside (+Z), 1 = front inside (-Z),
        //               2 = back  outside (-Z), 3 = back  inside (+Z).
        var geom = TestGeometries.Build(
            // 0: front cover outside, normal +Z
            new[] { new Vector3(-0.5f,-0.5f,+0.5f), new Vector3(+0.5f,-0.5f,+0.5f), new Vector3(+0.5f,+0.5f,+0.5f), new Vector3(-0.5f,+0.5f,+0.5f) },
            // 1: front cover inside, normal -Z (reverse winding)
            new[] { new Vector3(-0.5f,+0.5f,+0.5f), new Vector3(+0.5f,+0.5f,+0.5f), new Vector3(+0.5f,-0.5f,+0.5f), new Vector3(-0.5f,-0.5f,+0.5f) },
            // 2: back cover outside, normal -Z
            new[] { new Vector3(+0.5f,-0.5f,-0.5f), new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(-0.5f,+0.5f,-0.5f), new Vector3(+0.5f,+0.5f,-0.5f) },
            // 3: back cover inside, normal +Z (reverse winding)
            new[] { new Vector3(+0.5f,+0.5f,-0.5f), new Vector3(-0.5f,+0.5f,-0.5f), new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(+0.5f,-0.5f,-0.5f) });
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        var rot = SortAssertions.YawPitch(0, 90);

        // Orthographic: every normal has Z=0 after pitch=90° → all 4 quads culled.
        int nOrtho = sorter.Sort(rot, order, visible);
        Assert.Equal(0, nOrtho);

        // Perspective: exactly 2 quads should be visible — one face per cover.
        int nPersp = sorter.Sort(rot, order, visible, cameraDistance: 4f);
        Assert.Equal(2, nPersp);
        // The two visible quads must be one from each cover (one front-side, one back-side companion).
        Assert.True(visible[0] ^ visible[1], $"{algo}: exactly one of the front-cover companions visible");
        Assert.True(visible[2] ^ visible[3], $"{algo}: exactly one of the back-cover companions visible");
    }

    /// <summary>
    /// A face directly on the optical axis (centroid X=Y=0) with its normal perpendicular
    /// to the view axis must remain culled under perspective: the view ray and the normal
    /// are still perpendicular, so dot(normal, ray)=0. Guards against
    /// <c>IsFrontFacingPerspective</c> accidentally accepting genuinely zero-area faces.
    /// </summary>
    [Theory]
    [InlineData(SortAlgorithm.Topological)]
    [InlineData(SortAlgorithm.Newell)]
    [InlineData(SortAlgorithm.Bsp)]
    public void OnAxisEdgeOnFace_RemainsCulledUnderPerspective(SortAlgorithm algo)
    {
        // A single quad centred on the origin in XZ with normal +Y. View ray from
        // (0,0,d) to centroid (0,0,0) is (0,0,d) — perpendicular to the +Y normal.
        var geom = TestGeometries.Build(
            new[]
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(+0.5f, 0f, -0.5f),
                new Vector3(+0.5f, 0f, +0.5f),
                new Vector3(-0.5f, 0f, +0.5f),
            });
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        int n = sorter.Sort(Matrix4x4.Identity, order, visible, cameraDistance: 4f);
        Assert.Equal(0, n);
        Assert.False(visible[0], $"{algo}: on-axis edge-on face must stay culled under perspective");
    }
}
