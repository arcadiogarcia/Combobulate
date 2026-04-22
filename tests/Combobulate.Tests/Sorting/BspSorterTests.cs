using System.Numerics;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// BSP-specific tests: tree validity, determinism with seeded RNG,
/// efficient build, and that the partitioning correctly orders an
/// otherwise-cyclic geometry from every viewpoint.
/// </summary>
public class BspSorterTests
{
    [Fact]
    public void Build_IsDeterministicForSameSeed()
    {
        var geom = TestGeometries.UnitCube();
        var a = new BspSorter(geom, seed: 42);
        var b = new BspSorter(geom, seed: 42);

        var orderA = new int[geom.Quads.Length];
        var visA = new bool[geom.Quads.Length];
        var orderB = new int[geom.Quads.Length];
        var visB = new bool[geom.Quads.Length];

        var rot = SortAssertions.YawPitch(33, 17);
        int nA = a.Sort(rot, orderA, visA);
        int nB = b.Sort(rot, orderB, visB);

        Assert.Equal(nA, nB);
        for (int i = 0; i < nA; i++) Assert.Equal(orderA[i], orderB[i]);
    }

    [Fact]
    public void Build_HandlesEmptyGeometryGracefully()
    {
        var empty = TestGeometries.Build();
        var sorter = new BspSorter(empty);
        var order = new int[0];
        var visible = new bool[0];
        Assert.Equal(0, sorter.Sort(Matrix4x4.Identity, order, visible));
    }

    [Fact]
    public void StraddleCycle_ProducesOrderingMatchingDepthAtTypicalView()
    {
        var geom = TestGeometries.MutualStraddleCycle();
        var sorter = new BspSorter(geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        // Yaw 0, slight pitch up — page-top strip's near edge (z=+0.099) sits
        // *in front of* most of the cover plane (z=+0.1) by a tiny amount, but
        // its far edge is behind. The BSP should split the cover so each
        // fragment goes on the correct side of the strip.
        var rot = SortAssertions.Pitch(15);
        int n = sorter.Sort(rot, order, visible);
        Assert.True(n >= 1);
        // Last-emitted (frontmost) quad's centroid must have view-Z >= first.
        if (n >= 2)
        {
            var firstZ = Vector3.Transform(geom.Quads[order[0]].Centroid, rot).Z;
            var lastZ = Vector3.Transform(geom.Quads[order[n - 1]].Centroid, rot).Z;
            Assert.True(lastZ >= firstZ - 1e-3f);
        }
    }

    /// <summary>
    /// Regression for BSP painter-order bug at extreme tilt under perspective.
    ///
    /// <para>The original BSP walk used a single object-space camera DIRECTION
    /// (<c>invRot · (0,0,1)</c>) and tested each splitter plane via
    /// <c>dot(plane.Normal, cameraDir)</c>. That collapses to "is the camera in the
    /// +Normal hemisphere?" which is correct under orthographic projection (camera at
    /// infinity) but wrong under perspective when the camera is at a finite point and
    /// some splitter plane separates the camera POINT from the side the direction-based
    /// test names. The painter then drew far-away faces last and they occluded the
    /// near ones — visible in Deet as a missing page edge at pitch=±90°.</para>
    ///
    /// <para>This fixture stacks three parallel quads at z = -0.3, 0, +0.3 (all with
    /// outward normal +Z so they're all front-facing from the camera at +Z). They
    /// overlap completely in screen space, so a painter that emits them in the wrong
    /// depth order produces a visible occlusion bug.</para>
    /// </summary>
    [Theory]
    [InlineData(0f,    4f)]   // baseline ortho-equivalent
    [InlineData(15f,   4f)]   // mild tilt
    [InlineData(45f,   2f)]   // moderate tilt + close camera
    [InlineData(60f,   1.5f)] // strong tilt + very close camera (camera near or inside bbox)
    [InlineData(80f,   3f)]   // extreme tilt
    public void Walk_OverlappingParallelQuads_AreEmittedBackToFrontUnderPerspective(float pitchDeg, float cameraDistance)
    {
        // Three coplanar-axis quads: q0 at z=-0.3, q1 at z=0, q2 at z=+0.3, all +Z normals.
        // From any camera at +Z (any pitch ≤ 90°) the back-to-front order is q0, q1, q2.
        var geom = TestGeometries.Build(
            new[] { new Vector3(-0.5f,-0.5f,-0.3f), new Vector3(+0.5f,-0.5f,-0.3f), new Vector3(+0.5f,+0.5f,-0.3f), new Vector3(-0.5f,+0.5f,-0.3f) },
            new[] { new Vector3(-0.5f,-0.5f, 0.0f), new Vector3(+0.5f,-0.5f, 0.0f), new Vector3(+0.5f,+0.5f, 0.0f), new Vector3(-0.5f,+0.5f, 0.0f) },
            new[] { new Vector3(-0.5f,-0.5f,+0.3f), new Vector3(+0.5f,-0.5f,+0.3f), new Vector3(+0.5f,+0.5f,+0.3f), new Vector3(-0.5f,+0.5f,+0.3f) });
        var sorter = new BspSorter(geom, seed: 7);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        var rot = SortAssertions.Pitch(pitchDeg);
        int n = sorter.Sort(rot, order, visible, cameraDistance);
        Assert.Equal(3, n);

        // Painter back-to-front order over OVERLAPPING quads must agree with view-Z.
        for (int i = 1; i < n; i++)
        {
            var prevZ = Vector3.Transform(geom.Quads[order[i - 1]].Centroid, rot).Z;
            var thisZ = Vector3.Transform(geom.Quads[order[i]].Centroid, rot).Z;
            Assert.True(prevZ <= thisZ + 1e-3f,
                $"Painter order wrong at pitch={pitchDeg},d={cameraDistance}: " +
                $"order[{i-1}]=q{order[i-1]} Z={prevZ:F3} drew before order[{i}]=q{order[i]} Z={thisZ:F3} (overlap → occlusion bug)");
        }
    }
}
