using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Combobulate.Caching;
using Combobulate.Sorting;
using CompositionExpressions;
using Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork;

namespace Combobulate.Rendering;

/// <summary>
/// Phase-0 BakedAspectGraph bake. Sweeps the parameter space, runs the
/// configured <see cref="IFaceSorter"/> at each sample, and dedupes the
/// resulting (visibility + per-pair painter-order) tuples into
/// signatures.
///
/// <para>Painter order: delegated to the host's <see cref="SortAlgorithm"/>
/// choice (BSP / Newell / Topological) so the bake produces the same
/// orderings the SpritePainter path would on every frame. The bake
/// extracts a per-pair sign: <c>PairSigns[i,j] = +1</c> if at this
/// sample face j is drawn AFTER face i (j is in front of i), else
/// <c>-1</c>; <c>0</c> if either face is hidden.</para>
///
/// <para>The bake then computes which pairs have rotation-dependent
/// signs (vary across samples) vs constant signs. Constant pairs need
/// no runtime test — their order is already fixed by the per-sample
/// <c>Order</c>. Varying pairs become tolerant predicate tests in the
/// compositor expression so the runtime can pick the right cell when
/// the live rotation flips a pair's order.</para>
/// </summary>
internal static class SignatureBake
{
    /// <summary>One unique painter-sort signature.</summary>
    public sealed class Signature
    {
        /// <summary>Sign of each face's front-event: +1 if front-facing, -1 if back-facing.</summary>
        public required sbyte[] FaceSigns { get; init; }

        /// <summary>
        /// Pair sign matrix: <c>+1</c> if face j is in front of face i at
        /// this signature's representative sample, <c>-1</c> if behind,
        /// <c>0</c> if either is hidden OR the pair's sign is constant
        /// across the sweep (encoded statically in <see cref="Order"/>;
        /// no runtime test needed).
        /// </summary>
        public required sbyte[,] PairSigns { get; init; }

        /// <summary>Painter back-to-front order of visible face indices.</summary>
        public required int[] Order { get; init; }

        /// <summary>Per-face visibility flag (<c>FaceSigns &gt; 0</c>).</summary>
        public required bool[] Visibility { get; init; }

        /// <summary>Stable string key for deduplication.</summary>
        public required string Key { get; init; }
    }

    /// <summary>
    /// Process-wide signature cache. Painter-sort signatures depend only
    /// on the input geometry + sort algorithm + camera-distance + cull-margin
    /// — not on the AST, axes' ScalarNode identities, or the consumer's
    /// host control. So multiple Combobulate instances rendering the same
    /// model (e.g. a grid of book thumbnails sharing one cached
    /// <see cref="ObjGeometry"/>) can reuse a single bake's signature
    /// table; only materialisation of the per-cell sprite trees is
    /// per-instance work.
    ///
    /// <para>Keyed by reference-identity on the geometry, plus the
    /// algorithm and float parameters. Geometry caching lives in
    /// <see cref="ObjCache"/>; same parsed model always yields the same
    /// <see cref="ObjGeometry"/> instance, so reference-equality is the
    /// right invariant here.</para>
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<CacheKey, Signature[]> s_cache = new();
    private static readonly object s_cacheLock = new();

    private readonly record struct CacheKey(ObjGeometry Geom, SortAlgorithm Sort, int CameraDistanceBits, int CullMarginCosBits);

    /// <summary>Number of distinct cached signature tables; for diagnostics.</summary>
    public static int CacheCount { get { lock (s_cacheLock) return s_cache.Count; } }

    /// <summary>Drop all cached signature tables. For tests and explicit
    /// invalidation when a sorter implementation changes.</summary>
    public static void ClearCache()
    {
        lock (s_cacheLock) s_cache.Clear();
    }

    /// <summary>Run the bake.</summary>
    public static Signature[] Bake(
        Matrix4x4Node transformNode,
        TransformAnimationAxis[] axes,
        ObjGeometry geometry,
        SortAlgorithm sortAlgorithm,
        float cameraDistance,
        float cullMarginCos,
        CancellationToken ct)
    {
        var key = new CacheKey(geometry, sortAlgorithm,
            BitConverter.SingleToInt32Bits(cameraDistance),
            BitConverter.SingleToInt32Bits(cullMarginCos));
        lock (s_cacheLock)
        {
            if (s_cache.TryGetValue(key, out var cached)) return cached;
        }

        var fresh = BakeInternal(transformNode, axes, geometry, sortAlgorithm, cameraDistance, cullMarginCos, ct);

        lock (s_cacheLock)
        {
            // Two threads may race a miss; the second writer's insert is
            // a harmless overwrite (signatures are deterministic for a
            // given geometry).
            s_cache[key] = fresh;
        }
        return fresh;
    }

    private static Signature[] BakeInternal(
        Matrix4x4Node transformNode,
        TransformAnimationAxis[] axes,
        ObjGeometry geometry,
        SortAlgorithm sortAlgorithm,
        float cameraDistance,
        float cullMarginCos,
        CancellationToken ct)
    {
        if (axes is null || axes.Length == 0)
            throw new ArgumentException("At least one axis required.", nameof(axes));

        var quads = geometry.Quads;
        int n = quads.Length;
        var sorter = FaceSorterFactory.Create(sortAlgorithm, geometry);

        var sampleCounts = new int[axes.Length];
        long total = 1;
        for (int i = 0; i < axes.Length; i++)
        {
            sampleCounts[i] = axes.Length == 1 ? Math.Max(axes[i].Samples * 30, 720) : axes[i].Samples;
            total = checked(total * sampleCounts[i]);
        }

        var sweep = new float[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            int idx = i;
            axes[i].Scalar.SetLiveValueProvider(() => sweep[idx]);
        }

        var pairFirstSign = new sbyte[n, n];
        var pairVaries = new bool[n, n];

        var orderBuf = new int[n];
        var visBuf = new bool[n];
        var orderInverse = new int[n];
        var rawPerSample = new List<RawSample>();

        try
        {
            void ProcessSample()
            {
                Matrix4x4 M = transformNode.Evaluate();
                int visibleCount = sorter.Sort(M, orderBuf, visBuf, cameraDistance, cullMarginCos);

                for (int q = 0; q < n; q++) orderInverse[q] = -1;
                for (int k = 0; k < visibleCount; k++) orderInverse[orderBuf[k]] = k;

                var faceSigns = new sbyte[n];
                for (int q = 0; q < n; q++) faceSigns[q] = visBuf[q] ? (sbyte)+1 : (sbyte)-1;

                var pairSigns = new sbyte[n, n];
                for (int i = 0; i < n; i++)
                {
                    if (faceSigns[i] < 0) continue;
                    for (int j = i + 1; j < n; j++)
                    {
                        if (faceSigns[j] < 0) continue;
                        sbyte s = orderInverse[j] > orderInverse[i] ? (sbyte)+1 : (sbyte)-1;
                        pairSigns[i, j] = s;
                        pairSigns[j, i] = (sbyte)-s;
                        if (pairFirstSign[i, j] == 0) pairFirstSign[i, j] = s;
                        else if (pairFirstSign[i, j] != s) pairVaries[i, j] = true;
                    }
                }

                var visibility = new bool[n];
                for (int q = 0; q < n; q++) visibility[q] = faceSigns[q] > 0;

                var order = new int[visibleCount];
                Array.Copy(orderBuf, order, visibleCount);

                rawPerSample.Add(new RawSample
                {
                    FaceSigns = faceSigns,
                    PairSigns = pairSigns,
                    Order = order,
                    Visibility = visibility,
                });
            }

            // Regular grid sweep.
            for (long flat = 0; flat < total; flat++)
            {
                ct.ThrowIfCancellationRequested();
                long rem = flat;
                for (int i = axes.Length - 1; i >= 0; i--)
                {
                    int idx = (int)(rem % sampleCounts[i]);
                    rem /= sampleCounts[i];
                    float step = axes[i].Length / sampleCounts[i];
                    sweep[i] = axes[i].Min + (idx + 0.5f) * step;
                }
                ProcessSample();
            }

            // Axis-aligned probes.
            float[] AxisAligned(TransformAnimationAxis ax)
            {
                if (ax.Periodic)
                    return new[] { ax.Min, ax.Min + ax.Length * 0.25f, ax.Min + ax.Length * 0.5f, ax.Min + ax.Length * 0.75f };
                return new[] { ax.Min, ax.Min + ax.Length * 0.25f, ax.Min + ax.Length * 0.5f, ax.Min + ax.Length * 0.75f, ax.Min + ax.Length };
            }
            var aligned = new float[axes.Length][];
            long alignedTotal = 1;
            for (int i = 0; i < axes.Length; i++) { aligned[i] = AxisAligned(axes[i]); alignedTotal *= aligned[i].Length; }
            for (long flat = 0; flat < alignedTotal; flat++)
            {
                ct.ThrowIfCancellationRequested();
                long rem = flat;
                for (int i = axes.Length - 1; i >= 0; i--)
                {
                    int idx = (int)(rem % aligned[i].Length);
                    rem /= aligned[i].Length;
                    sweep[i] = aligned[i][idx];
                }
                ProcessSample();
            }

            // Per-axis line sweeps with other axes pinned.
            for (int axisIdx = 0; axisIdx < axes.Length; axisIdx++)
            {
                int sweepCount = Math.Max(axes[axisIdx].Samples * 4, 360);
                for (int s = 0; s < sweepCount; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int j = 0; j < axes.Length; j++) sweep[j] = 0f;
                    sweep[axisIdx] = axes[axisIdx].Min + (s + 0.5f) * axes[axisIdx].Length / sweepCount;
                    ProcessSample();
                }
                for (int otherIdx = 0; otherIdx < axes.Length; otherIdx++)
                {
                    if (otherIdx == axisIdx) continue;
                    foreach (var otherVal in aligned[otherIdx])
                    {
                        for (int s = 0; s < sweepCount; s++)
                        {
                            ct.ThrowIfCancellationRequested();
                            for (int j = 0; j < axes.Length; j++) sweep[j] = 0f;
                            sweep[axisIdx] = axes[axisIdx].Min + (s + 0.5f) * axes[axisIdx].Length / sweepCount;
                            sweep[otherIdx] = otherVal;
                            ProcessSample();
                        }
                    }
                }
            }
        }
        finally
        {
            for (int i = 0; i < axes.Length; i++)
                axes[i].Scalar.SetLiveValueProvider(null);
        }

        // Reduction: dedupe by (visibility, varying-pair-signs).
        var sigDict = new Dictionary<string, Signature>();
        for (int r = 0; r < rawPerSample.Count; r++)
        {
            var raw = rawPerSample[r];
            var keyB = new System.Text.StringBuilder(n + n * (n - 1) / 2 + 4);
            for (int i = 0; i < n; i++) keyB.Append(raw.FaceSigns[i] > 0 ? '+' : '-');
            keyB.Append('|');
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (!pairVaries[i, j]) keyB.Append('0');
                    else keyB.Append(raw.PairSigns[i, j] switch { 1 => '+', -1 => '-', _ => '0' });
                }
            string key = keyB.ToString();
            if (sigDict.ContainsKey(key)) continue;

            var runtimePairs = new sbyte[n, n];
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (pairVaries[i, j])
                    {
                        runtimePairs[i, j] = raw.PairSigns[i, j];
                        runtimePairs[j, i] = (sbyte)-raw.PairSigns[i, j];
                    }
                }

            sigDict[key] = new Signature
            {
                FaceSigns = raw.FaceSigns,
                PairSigns = runtimePairs,
                Order = raw.Order,
                Visibility = raw.Visibility,
                Key = key,
            };
        }

        var result = new Signature[sigDict.Count];
        sigDict.Values.CopyTo(result, 0);

        try
        {
            int varying = 0;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++) if (pairVaries[i, j]) varying++;
            var dir = System.IO.Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path, "debug-artifacts");
            System.IO.Directory.CreateDirectory(dir);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] SignatureBake done: signatures={result.Length}, varyingPairs={varying}/{n * (n - 1) / 2}, sortAlgorithm={sortAlgorithm}");
            int show = Math.Min(result.Length, 8);
            for (int i = 0; i < show; i++)
                sb.AppendLine($"  sig[{i}]: order=[{string.Join(",", result[i].Order)}] vis=[{string.Join("", System.Linq.Enumerable.Select(result[i].Visibility, b => b ? '1' : '0'))}] key={result[i].Key}");
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, "signature-bake.log"), sb.ToString());
        }
        catch { }

        return result;
    }

    private struct RawSample
    {
        public sbyte[] FaceSigns;
        public sbyte[,] PairSigns;
        public int[] Order;
        public bool[] Visibility;
    }
}
