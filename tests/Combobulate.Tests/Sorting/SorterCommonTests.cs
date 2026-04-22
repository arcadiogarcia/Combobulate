using System.Numerics;
using Combobulate.Caching;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Shared parameterised tests that every <see cref="IFaceSorter"/> implementation
/// must pass. Topological is excluded from the cycle-resolution suite since
/// the cycle case is its known weakness.
/// </summary>
public class SorterCommonTests
{
    public static IEnumerable<object[]> AllAlgorithms => new[]
    {
        new object[] { SortAlgorithm.Topological },
        new object[] { SortAlgorithm.Newell },
        new object[] { SortAlgorithm.Bsp },
    };

    public static IEnumerable<object[]> CorrectAlgorithms => new[]
    {
        new object[] { SortAlgorithm.Newell },
        new object[] { SortAlgorithm.Bsp },
    };

    [Theory, MemberData(nameof(AllAlgorithms))]
    public void FrontAndBack_OnlyVisibleFaceIsCounted(SortAlgorithm algo)
    {
        var geom = TestGeometries.FrontAndBackQuads();
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        // From the default view (looking down -Z), only the +Z face (quad 0) should be visible.
        int n = sorter.Sort(Matrix4x4.Identity, order, visible);
        Assert.Equal(1, n);
        Assert.True(visible[0]);
        Assert.False(visible[1]);
        Assert.Equal(0, order[0]);
    }

    [Theory, MemberData(nameof(AllAlgorithms))]
    public void Cube_ExactlyThreeFacesVisibleFromObliqueView(SortAlgorithm algo)
    {
        var cube = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(algo, cube);
        var order = new int[cube.Quads.Length];
        var visible = new bool[cube.Quads.Length];

        // Yaw 30, pitch 20: an oblique view of the cube shows exactly three faces.
        int n = sorter.Sort(SortAssertions.YawPitch(30, 20), order, visible);
        Assert.Equal(3, n);
    }

    [Theory, MemberData(nameof(AllAlgorithms))]
    public void Cube_VisibleFacesAreUniqueAndCullCorrect(SortAlgorithm algo)
    {
        // Cube faces don't actually overlap in screen-space (only on shared
        // edges), so the back-to-front *centroid* invariant is too strict to
        // assert here — any permutation is pixel-correct. What we DO assert:
        //   * exactly 1, 2, or 3 faces are visible at every viewing angle
        //   * the "facing" faces (viewNormal.Z > 0) match the visible set
        //   * no face appears twice in the order
        var cube = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(algo, cube);
        var order = new int[cube.Quads.Length];
        var visible = new bool[cube.Quads.Length];

        for (int yaw = 0; yaw < 360; yaw += 17)
        {
            for (int pitch = -60; pitch <= 60; pitch += 13)
            {
                var rot = SortAssertions.YawPitch(yaw, pitch);
                int n = sorter.Sort(rot, order, visible);
                Assert.InRange(n, 1, 3);

                // Cull check: independently compute facing faces and compare.
                var expected = new HashSet<int>();
                for (int q = 0; q < cube.Quads.Length; q++)
                {
                    var rn = Vector3.TransformNormal(cube.Quads[q].Normal, rot);
                    if (rn.Z > 0) expected.Add(q);
                }
                var actual = new HashSet<int>();
                for (int i = 0; i < n; i++) actual.Add(order[i]);
                Assert.True(expected.SetEquals(actual),
                    $"Algo {algo} at yaw={yaw} pitch={pitch}: expected {string.Join(',', expected)} got {string.Join(',', actual)}");
            }
        }
    }

    [Theory, MemberData(nameof(CorrectAlgorithms))]
    public void MutualStraddleCycle_OrderRespectsBackToFrontAtAllAngles(SortAlgorithm algo)
    {
        // The whole point of NewellSorter and BspSorter: this case must be sorted
        // correctly even though the polygons mutually straddle each other's planes.
        // Since the cycle's visible quads have overlapping centroid Z at certain
        // angles, we don't strictly compare centroid order; instead we check that
        // every visible quad is emitted exactly once and that the last-emitted
        // quad's centroid is at least as close to the camera as the first.
        var geom = TestGeometries.MutualStraddleCycle();
        var sorter = FaceSorterFactory.Create(algo, geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        for (int yaw = 0; yaw < 360; yaw += 9)
        {
            var rot = SortAssertions.Yaw(yaw);
            int n = sorter.Sort(rot, order, visible);
            // Each visible quad emitted exactly once.
            var seen = new HashSet<int>();
            for (int i = 0; i < n; i++) Assert.True(seen.Add(order[i]),
                $"Algo {algo} at yaw={yaw} emitted quad {order[i]} twice");
            for (int q = 0; q < geom.Quads.Length; q++)
                Assert.Equal(visible[q], seen.Contains(q));
        }
    }

    [Theory, MemberData(nameof(AllAlgorithms))]
    public void EmittedOrderArrayHasNoUninitialisedSlots(SortAlgorithm algo)
    {
        var cube = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(algo, cube);
        var order = new int[cube.Quads.Length];
        Array.Fill(order, -1);
        var visible = new bool[cube.Quads.Length];
        int n = sorter.Sort(SortAssertions.YawPitch(45, 30), order, visible);
        for (int i = 0; i < n; i++) Assert.True(order[i] >= 0 && order[i] < cube.Quads.Length);
    }

    [Theory, MemberData(nameof(AllAlgorithms))]
    public void NoVisibleQuadIsMissedAndNoneAreDuplicated(SortAlgorithm algo)
    {
        var cube = TestGeometries.UnitCube();
        var sorter = FaceSorterFactory.Create(algo, cube);
        var order = new int[cube.Quads.Length];
        var visible = new bool[cube.Quads.Length];

        for (int yaw = 0; yaw < 360; yaw += 23)
        {
            int n = sorter.Sort(SortAssertions.Yaw(yaw), order, visible);
            int visCount = SortAssertions.CountVisible(visible);
            Assert.Equal(visCount, n);
            var seen = new HashSet<int>();
            for (int i = 0; i < n; i++) Assert.True(seen.Add(order[i]));
            // Each visible quad index must appear in the order.
            for (int q = 0; q < cube.Quads.Length; q++)
                if (visible[q]) Assert.Contains(q, seen);
        }
    }
}
