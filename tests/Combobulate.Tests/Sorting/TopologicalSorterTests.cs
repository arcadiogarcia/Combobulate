using System.Numerics;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

public class TopologicalSorterTests
{
    /// <summary>
    /// Documents the known limitation of the topological sort: when two quads
    /// mutually straddle each other's planes, the predecessor graph contains
    /// a 2-cycle that Kahn's algorithm cannot resolve. The fallback (sort
    /// remaining nodes by view-Z) may still pass the back-to-front
    /// invariant if centroids happen to disambiguate, but is not guaranteed.
    /// This test verifies the sort still completes (no hang) and produces
    /// a permutation, even if order is suboptimal.
    /// </summary>
    [Fact]
    public void StraddleCycle_DoesNotHangAndEmitsAllVisibleQuads()
    {
        var geom = TestGeometries.MutualStraddleCycle();
        var sorter = new TopologicalSorter(geom);
        var order = new int[geom.Quads.Length];
        var visible = new bool[geom.Quads.Length];

        for (int yaw = 0; yaw < 360; yaw += 11)
        {
            int n = sorter.Sort(SortAssertions.Yaw(yaw), order, visible);
            int visCount = SortAssertions.CountVisible(visible);
            Assert.Equal(visCount, n);
        }
    }
}
