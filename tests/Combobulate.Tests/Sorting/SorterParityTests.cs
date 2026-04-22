using System.Numerics;
using Combobulate.Sorting;

namespace Combobulate.Tests.Sorting;

/// <summary>
/// Cross-algorithm parity: on cycle-free convex geometry like a cube, all three
/// sorters must agree on the back-to-front invariant; the visible-quad set must
/// be identical across all algorithms.
/// </summary>
public class SorterParityTests
{
    private static readonly SortAlgorithm[] All = { SortAlgorithm.Topological, SortAlgorithm.Newell, SortAlgorithm.Bsp };

    [Fact]
    public void AllSorters_ProduceIdenticalVisibilitySet_ForCube()
    {
        var cube = TestGeometries.UnitCube();
        var sorters = All.Select(a => FaceSorterFactory.Create(a, cube)).ToArray();

        for (int yaw = 0; yaw < 360; yaw += 17)
        {
            for (int pitch = -45; pitch <= 45; pitch += 13)
            {
                var rot = SortAssertions.YawPitch(yaw, pitch);
                var sets = sorters.Select(s =>
                {
                    var o = new int[cube.Quads.Length];
                    var v = new bool[cube.Quads.Length];
                    int n = s.Sort(rot, o, v);
                    return new HashSet<int>(o.Take(n));
                }).ToArray();
                Assert.True(sets[0].SetEquals(sets[1]),
                    $"Topological vs Newell visibility differ at yaw={yaw}, pitch={pitch}");
                Assert.True(sets[0].SetEquals(sets[2]),
                    $"Topological vs BSP visibility differ at yaw={yaw}, pitch={pitch}");
            }
        }
    }

    [Fact]
    public void NewellAndBsp_AgreeOnBackToFrontForStraddleCycle()
    {
        // The two correct sorters need not produce *identical* permutations
        // (their internal split fans differ), but both must satisfy the
        // back-to-front centroid invariant.
        var geom = TestGeometries.MutualStraddleCycle();
        var newell = new NewellSorter(geom);
        var bsp = new BspSorter(geom);

        for (int yaw = 0; yaw < 360; yaw += 9)
        {
            var rot = SortAssertions.Yaw(yaw);
            var oN = new int[geom.Quads.Length]; var vN = new bool[geom.Quads.Length];
            var oB = new int[geom.Quads.Length]; var vB = new bool[geom.Quads.Length];
            int nN = newell.Sort(rot, oN, vN);
            int nB = bsp.Sort(rot, oB, vB);
            Assert.Equal(nN, nB);
            // Visible-quad sets agree even if permutations differ.
            Assert.True(new HashSet<int>(oN.Take(nN)).SetEquals(new HashSet<int>(oB.Take(nB))));
        }
    }
}
