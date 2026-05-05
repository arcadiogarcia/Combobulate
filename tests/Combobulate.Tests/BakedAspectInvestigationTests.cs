using System;
using System.Numerics;
using Combobulate.Caching;
using Combobulate.Parsing;
using Combobulate.Sorting;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace Combobulate.Tests;

public class BakedAspectInvestigationTests
{
    private readonly ITestOutputHelper _o;
    public BakedAspectInvestigationTests(ITestOutputHelper o) { _o = o; }

    [Fact]
    public void Investigate_FineGridFailure_Y_Neg9_P11_RNeg17()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        const float D2R = MathF.PI / 180f;
        var rotRuntime = Matrix4x4.CreateFromYawPitchRoll(-8.994015f * D2R, 10.834709f * D2R, -17.554392f * D2R);

        // Find what BSP would produce at the runtime θ.
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geom);
        int n = geom.Quads.Length;
        var orderRT = new int[n]; var visRT = new bool[n];
        int countRT = sorter.Sort(rotRuntime, orderRT, visRT, 0f, 0f);
        var orderRTArr = new int[countRT]; Array.Copy(orderRT, orderRTArr, countRT);

        _o.WriteLine($"BSP at runtime θ: order=[{string.Join(",", orderRTArr)}] vis=[{string.Join("", System.Linq.Enumerable.Select(visRT, b=>b?'1':'0'))}]");
        var vRT = Combobulate.Tests.Sorting.PainterCorrectness.FindWorstViolation(geom, orderRTArr, countRT, rotRuntime);
        _o.WriteLine($"  BSP self-violation at runtime θ: {vRT}");

        // Find closest grid sample (mirror BakeSweep grid).
        const int yawN = 24, pitchN = 12, rollN = 12;
        Matrix4x4 closestRot = Matrix4x4.Identity;
        Vector3 closestYpr = default;
        float bestDelta = float.MaxValue;
        for (int y = 0; y < yawN; y++)
        for (int p = 0; p < pitchN; p++)
        for (int r = 0; r < rollN; r++)
        {
            float yaw = 0f + (y + 0.5f) * 360f / yawN;
            float pitch = -180f + (p + 0.5f) * 360f / pitchN;
            float roll = -180f + (r + 0.5f) * 360f / rollN;
            var rot = Matrix4x4.CreateFromYawPitchRoll(yaw * D2R, pitch * D2R, roll * D2R);
            // Compare visibility patterns: closest match by Frobenius norm.
            float d =
                (rot.M11 - rotRuntime.M11) * (rot.M11 - rotRuntime.M11) +
                (rot.M12 - rotRuntime.M12) * (rot.M12 - rotRuntime.M12) +
                (rot.M13 - rotRuntime.M13) * (rot.M13 - rotRuntime.M13) +
                (rot.M21 - rotRuntime.M21) * (rot.M21 - rotRuntime.M21) +
                (rot.M22 - rotRuntime.M22) * (rot.M22 - rotRuntime.M22) +
                (rot.M23 - rotRuntime.M23) * (rot.M23 - rotRuntime.M23) +
                (rot.M31 - rotRuntime.M31) * (rot.M31 - rotRuntime.M31) +
                (rot.M32 - rotRuntime.M32) * (rot.M32 - rotRuntime.M32) +
                (rot.M33 - rotRuntime.M33) * (rot.M33 - rotRuntime.M33);
            if (d < bestDelta) { bestDelta = d; closestRot = rot; closestYpr = new Vector3(yaw, pitch, roll); }
        }
        _o.WriteLine($"closest grid sample: ypr={closestYpr} delta={MathF.Sqrt(bestDelta):F4}");

        var orderG = new int[n]; var visG = new bool[n];
        int countG = sorter.Sort(closestRot, orderG, visG, 0f, 0f);
        var orderGArr = new int[countG]; Array.Copy(orderG, orderGArr, countG);
        _o.WriteLine($"BSP at closest grid: order=[{string.Join(",", orderGArr)}] vis=[{string.Join("", System.Linq.Enumerable.Select(visG, b=>b?'1':'0'))}]");

        // Apply grid-derived order at runtime θ.
        var vGridApplied = Combobulate.Tests.Sorting.PainterCorrectness.FindWorstViolation(geom, orderGArr, countG, rotRuntime);
        _o.WriteLine($"  grid-order applied at runtime θ: {vGridApplied}");

        // Try other sorters at runtime θ.
        foreach (var alg in new[] { SortAlgorithm.Newell, SortAlgorithm.Topological })
        {
            var s = FaceSorterFactory.Create(alg, geom);
            var ob = new int[n]; var vb = new bool[n];
            int c = s.Sort(rotRuntime, ob, vb, 0f, 0f);
            var oa = new int[c]; Array.Copy(ob, oa, c);
            var v = Combobulate.Tests.Sorting.PainterCorrectness.FindWorstViolation(geom, oa, c, rotRuntime);
            _o.WriteLine($"  {alg}: order=[{string.Join(",", oa)}] violation={v}");
        }
    }

    [Fact]
    public void Investigate_DepthDeltaDistribution()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "book.obj");
        var geom = ObjGeometry.Build(ObjParser.Parse(File.ReadAllText(path)).Model);
        var sorter = FaceSorterFactory.Create(SortAlgorithm.Bsp, geom);
        int n = geom.Quads.Length;
        var orderBuf = new int[n]; var visBuf = new bool[n];
        var rng = new Random(0xBA6_ED);
        const float D2R = MathF.PI / 180f;
        var deltas = new System.Collections.Generic.List<float>();
        int total = 5000, violations = 0;
        for (int i = 0; i < total; i++)
        {
            float yaw = (float)((rng.NextDouble() * 360.0) - 180.0);
            float pitch = (float)((rng.NextDouble() * 360.0) - 180.0);
            float roll = (float)((rng.NextDouble() * 360.0) - 180.0);
            var rot = Matrix4x4.CreateFromYawPitchRoll(yaw * D2R, pitch * D2R, roll * D2R);
            int c = sorter.Sort(rot, orderBuf, visBuf, 0f, 0f);
            if (c < 2) continue;
            var oa = new int[c]; Array.Copy(orderBuf, oa, c);
            var v = Combobulate.Tests.Sorting.PainterCorrectness.FindWorstViolation(geom, oa, c, rot);
            if (v.HasValue)
            {
                violations++;
                deltas.Add(v.Value.DepthDelta);
            }
        }
        deltas.Sort();
        _o.WriteLine($"BSP violations: {violations}/{total}");
        if (deltas.Count > 0)
        {
            _o.WriteLine($"  depthDelta min={deltas[0]:F6} max={deltas[^1]:F6}");
            _o.WriteLine($"  p50={deltas[deltas.Count/2]:F6} p90={deltas[deltas.Count*9/10]:F6} p99={deltas[deltas.Count*99/100]:F6}");
            int below001 = deltas.FindAll(d => d < 0.001f).Count;
            int below01 = deltas.FindAll(d => d < 0.01f).Count;
            int below05 = deltas.FindAll(d => d < 0.05f).Count;
            _o.WriteLine($"  <0.001: {below001}, <0.01: {below01}, <0.05: {below05}");
        }
    }
}
