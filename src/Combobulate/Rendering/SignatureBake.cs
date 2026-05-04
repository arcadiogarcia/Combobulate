using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Combobulate.Caching;
using CompositionExpressions;
using Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork;

namespace Combobulate.Rendering;

/// <summary>
/// Phase 0 BakedAspectGraph bake: enumerates the unique painter
/// signatures the model assumes anywhere in the supplied parameter
/// space. Each signature is identified by a sign vector of the
/// <see cref="EventFunctions"/> events; cell boundaries in θ-space are
/// implicit (defined by the events' zero-crossings) and exact.
///
/// <para>Sort convention: <b>centroid-Z painter sort</b>. Faces are drawn
/// back-to-front by their transformed centroid's Z coordinate. This is
/// correct for convex models where face centroids' Z order matches their
/// painter order, including the cube and most book/box-like meshes. For
/// pathological cases where centroid-Z sort is incorrect (long thin
/// faces with overlapping projections, non-convex configurations) the
/// caller should choose a different <c>RenderingMode</c>.</para>
///
/// <para>Visibility convention: a face is visible iff
/// <c>(M · normal).z &gt; 0</c>, matching a camera that looks toward
/// <c>+Z</c> in the host coordinate system. Combobulate composes
/// <c>toOrigin * userRotation * fromOrigin</c> as the root
/// TransformMatrix so this convention is consistent regardless of host
/// size.</para>
/// </summary>
internal static class SignatureBake
{
    /// <summary>
    /// One unique painter-sort signature. The sign vectors fully identify
    /// it; <see cref="Order"/> + <see cref="Visibility"/> are the
    /// painter-sort outputs the renderer materialises into a sprite tree.
    /// </summary>
    public sealed class Signature
    {
        /// <summary>Sign of each face's front-event: +1 if front-facing, -1 if back-facing.</summary>
        public required sbyte[] FaceSigns { get; init; }

        /// <summary>
        /// Pair sign matrix; <c>PairSigns[i,j]</c> = sign of <c>z_j - z_i</c>
        /// when both faces are visible, else 0 (don't-care). Symmetric:
        /// <c>PairSigns[j,i] = -PairSigns[i,j]</c> when both visible. Only
        /// <c>i &lt; j</c> entries are authoritative.
        /// </summary>
        public required sbyte[,] PairSigns { get; init; }

        /// <summary>Painter-back-to-front order of visible face indices.</summary>
        public required int[] Order { get; init; }

        /// <summary>Per-face visibility flag (mirrors sign(<see cref="FaceSigns"/>) &gt; 0).</summary>
        public required bool[] Visibility { get; init; }

        /// <summary>Stable string key used for deduplication.</summary>
        public required string Key { get; init; }
    }

    /// <summary>
    /// Sweep the parameter space, enumerate unique signatures.
    /// </summary>
    public static Signature[] Bake(
        Matrix4x4Node transformNode,
        TransformAnimationAxis[] axes,
        ObjGeometry geometry,
        CancellationToken ct)
    {
        if (axes is null || axes.Length == 0)
            throw new ArgumentException("At least one axis required.", nameof(axes));

        var quads = geometry.Quads;
        int n = quads.Length;

        // Signature-discovery sweep density. Per-axis sample counts come
        // from the caller (axes[i].Samples). For 1-axis we use a finer
        // sweep so we don't miss thin signature regions; for higher
        // dimensions the caller's grid is fine because we're only
        // deduplicating, not building the cell geometry.
        var sampleCounts = new int[axes.Length];
        long total = 1;
        for (int i = 0; i < axes.Length; i++)
        {
            sampleCounts[i] = axes.Length == 1 ? Math.Max(axes[i].Samples * 30, 720) : axes[i].Samples;
            total = checked(total * sampleCounts[i]);
        }

        // LiveValueProvider override so transformNode.Evaluate() reads
        // from our sweep array. Same pattern the existing bake used.
        var sweep = new float[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            int idx = i;
            axes[i].Scalar.SetLiveValueProvider(() => sweep[idx]);
        }

        var signatures = new Dictionary<string, Signature>();
        var indices = new int[axes.Length];
        var faceSignsScratch = new sbyte[n];
        var centroidZScratch = new float[n];
        var pairSignsScratch = new sbyte[n, n];

        // Plane-side relations are STATIC (rotation-invariant): for pure
        // rotations the signed distance of c_i from face j's plane is
        // identical in model space and rotated space, because rotations
        // preserve dot products. So pair signs depend only on which
        // faces are present (visibility pattern), not on the angle.
        // Precompute the constant plane-side matrix once. The runtime
        // predicate then needs only face-front tests; pair tests collapse
        // to don't-care, drastically shrinking expression strings and
        // eliminating the centroid-Z mis-orderings that occur for
        // non-spherical objects (book pages slipping behind covers, etc).
        var planeSidePair = new sbyte[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                // Sign of (n_j · (c_i - c_j)): >0 means c_i lies on the
                // +n_j side of face j's plane, i.e. c_i is on j's
                // viewer-facing side when j is front-facing → i should
                // be drawn AFTER j (in front).
                float side = Vector3.Dot(
                    quads[j].Normal,
                    quads[i].Centroid - quads[j].Centroid);
                planeSidePair[i, j] = side > 0f ? (sbyte)+1 : (sbyte)-1;
            }
        }

        try
        {
            // Pass 1: regular grid sweep to catch the bulk of distinct signatures.
            for (long flat = 0; flat < total; flat++)
            {
                ct.ThrowIfCancellationRequested();

                long rem = flat;
                for (int i = axes.Length - 1; i >= 0; i--)
                {
                    indices[i] = (int)(rem % sampleCounts[i]);
                    rem /= sampleCounts[i];
                }
                for (int i = 0; i < axes.Length; i++)
                {
                    float step = axes[i].Length / sampleCounts[i];
                    sweep[i] = axes[i].Min + (indices[i] + 0.5f) * step;
                }
                ProcessSample();
            }

            // Pass 2: explicit axis-aligned sample points so degenerate
            // signatures (edge-on faces at θ exactly equal to multiples of
            // 90°) get enumerated. Without these, sliders sitting at their
            // default 0 produce no matching signature and the cube
            // disappears. We probe every combination of {0°, 90°, 180°,
            // 270°} on periodic axes and {−180°, −90°, 0°, 90°} on
            // non-periodic axes (clipped to range).
            float[] AxisAlignedValues(TransformAnimationAxis ax)
            {
                if (ax.Periodic)
                    return new[] { ax.Min, ax.Min + ax.Length * 0.25f, ax.Min + ax.Length * 0.5f, ax.Min + ax.Length * 0.75f };
                return new[] { ax.Min, ax.Min + ax.Length * 0.25f, ax.Min + ax.Length * 0.5f, ax.Min + ax.Length * 0.75f, ax.Min + ax.Length };
            }
            var aligned = new float[axes.Length][];
            long alignedTotal = 1;
            for (int i = 0; i < axes.Length; i++)
            {
                aligned[i] = AxisAlignedValues(axes[i]);
                alignedTotal *= aligned[i].Length;
            }
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

            // Pass 3: per-axis line sweeps with all OTHER axes at 0 (or
            // their canonical "rest" value). This captures the thin
            // signature regions that arise when only one axis is non-zero
            // — e.g. a cube viewed at pitch=0, roll=0, yaw=20° has only
            // two faces visible (top + side), a signature that occupies
            // a measure-zero slab in 3D θ-space and is missed by the
            // generic grid. We sweep each axis at its native sample
            // density (or 360 samples, whichever is finer) while pinning
            // the others at 0. Then again with one OTHER axis at each
            // axis-aligned multiple of 90° while the third stays at 0.
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
                // Also sweep this axis with each OTHER axis at each axis-aligned value.
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

            void ProcessSample()
            {
                Matrix4x4 M = transformNode.Evaluate();

                // Compute face front-signs and centroid Z values.
                // Convention: nz > 0 → visible (+1); nz <= 0 → hidden (-1).
                // Edge-on faces (nz exactly 0) have zero projected area
                // so are correctly classified as hidden, and the boundary
                // belongs unambiguously to the hidden partition. The
                // PredicateCompiler mirrors this convention exactly.
                for (int q = 0; q < n; q++)
                {
                    float nz = EventFunctions.EvalDirectionZ(M, quads[q].Normal);
                    faceSignsScratch[q] = nz > 0f ? (sbyte)+1 : (sbyte)-1;
                    centroidZScratch[q] = EventFunctions.EvalPointZ(M, quads[q].Centroid);
                }

                // Pair signs: STATIC plane-side relations from the
                // model. Only mutually-visible pairs contribute; hidden
                // faces don't appear in the painter ordering. The pair
                // sign is the constant we precomputed in planeSidePair
                // — it does NOT depend on the rotation, only on whether
                // both faces are visible at this sample.
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++) pairSignsScratch[i, j] = 0;
                for (int i = 0; i < n; i++)
                {
                    if (faceSignsScratch[i] < 0) continue;
                    for (int j = i + 1; j < n; j++)
                    {
                        if (faceSignsScratch[j] < 0) continue;
                        pairSignsScratch[i, j] = planeSidePair[i, j];
                        pairSignsScratch[j, i] = planeSidePair[j, i];
                    }
                }

                EmitSignature();

                void EmitSignature()
                {
                    string key = BuildKey(faceSignsScratch, pairSignsScratch, n);
                    if (signatures.ContainsKey(key)) return;

                    var visibleIdxs = new List<int>(n);
                    for (int q = 0; q < n; q++)
                        if (faceSignsScratch[q] > 0) visibleIdxs.Add(q);
                    // Topological sort by plane-side: a should come
                    // BEFORE b (drawn first / further from viewer) iff a
                    // is on the back side of b's plane (n_b·(c_a-c_b) < 0)
                    // AND b is on the front side of a's plane. If both
                    // are on each other's back side (e.g. faces meeting
                    // at a shared edge with no projected overlap) the
                    // painter order doesn't matter — tie-break by face
                    // index for stability.
                    visibleIdxs.Sort((a, b) =>
                    {
                        sbyte ab = planeSidePair[a, b]; // +1: a on viewer-side of b → a in front of b
                        sbyte ba = planeSidePair[b, a]; // +1: b on viewer-side of a → b in front of a
                        if (ab < 0 && ba > 0) return -1; // a behind b
                        if (ab > 0 && ba < 0) return +1; // a in front of b
                        return a.CompareTo(b);
                    });

                    var visibility = new bool[n];
                    for (int q = 0; q < n; q++) visibility[q] = faceSignsScratch[q] > 0;

                    signatures[key] = new Signature
                    {
                        FaceSigns = (sbyte[])faceSignsScratch.Clone(),
                        PairSigns = (sbyte[,])pairSignsScratch.Clone(),
                        Order = visibleIdxs.ToArray(),
                        Visibility = visibility,
                        Key = key,
                    };
                }
            }
        }
        finally
        {
            for (int i = 0; i < axes.Length; i++)
                axes[i].Scalar.SetLiveValueProvider(null);
        }

        var result = new Signature[signatures.Count];
        signatures.Values.CopyTo(result, 0);

        // DIAG: log the bake result so we can verify dedup works.
        try
        {
            var dir = System.IO.Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                "debug-artifacts");
            System.IO.Directory.CreateDirectory(dir);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] SignatureBake done: total samples swept={total}, unique signatures={result.Length}");
            sb.AppendLine($"  axes: {string.Join(", ", System.Linq.Enumerable.Select(axes, a => $"min={a.Min} len={a.Length} samples={a.Samples} periodic={a.Periodic}"))}");
            int show = Math.Min(result.Length, 8);
            for (int i = 0; i < show; i++)
                sb.AppendLine($"  sig[{i}]: order=[{string.Join(",", result[i].Order)}] vis=[{string.Join(",", System.Linq.Enumerable.Select(result[i].Visibility, b => b ? '1' : '0'))}] key={result[i].Key}");
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, "signature-bake.log"), sb.ToString());
        }
        catch { }

        return result;
    }

    private static string BuildKey(sbyte[] faceSigns, sbyte[,] pairSigns, int n)
    {
        // Stable ordered serialisation: face signs followed by upper-triangle pair signs.
        var sb = new System.Text.StringBuilder(n + n * (n - 1) / 2 + 4);
        for (int i = 0; i < n; i++) sb.Append(faceSigns[i] > 0 ? '+' : '-');
        sb.Append('|');
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                sb.Append(pairSigns[i, j] switch { 1 => '+', -1 => '-', _ => '0' });
        return sb.ToString();
    }
}
