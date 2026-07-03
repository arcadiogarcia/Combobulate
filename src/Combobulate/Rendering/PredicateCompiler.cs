using System.Numerics;
using Combobulate.Caching;
using Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork;

namespace Combobulate.Rendering;

/// <summary>
/// Compiles a <see cref="SignatureBake.Signature"/> into a typed
/// <see cref="BooleanNode"/> predicate that the compositor evaluates
/// every frame to decide whether the signature's painter order applies.
///
/// <para>The predicate is the conjunction of:</para>
/// <list type="bullet">
/// <item>One face-front comparison per face — <c>(M·n_f).z &gt; 0</c> if
/// the signature has the face front-facing, else <c>&lt; 0</c>. All N
/// face tests are always present so a signature with face f back-facing
/// can never match a θ where face f is front-facing.</item>
/// <item>One depth-pair comparison per face pair where <i>both</i> are
/// front-facing in this signature — <c>(M·c_j).z - (M·c_i).z &gt; 0</c>
/// or <c>&lt; 0</c>. Pairs involving a back-facing face are omitted (their
/// pair signs are don't-care because hidden faces don't appear in the
/// painter ordering).</item>
/// </list>
///
/// <para>Together: the predicate is true at θ iff θ produces this
/// signature's exact sign pattern. Two distinct signatures are mutually
/// exclusive (their face-sign vectors differ, or their visible-pair-sign
/// vectors differ on at least one mutually-visible pair).</para>
/// </summary>
internal static class PredicateCompiler
{
    /// <summary>
    /// Tolerance used for predicate inequalities. Compositor and CPU
    /// arithmetic disagree on signs of near-zero quantities; using
    /// strict <c>&gt; 0</c> would leave gaps where no cell matches. With
    /// <c>&gt; -<see cref="PredicateEpsilon"/></c> (and the symmetric
    /// <c>&lt; +epsilon</c> for - signs) overlap bands cover the
    /// disagreement; multiple cells may light simultaneously near
    /// boundaries, but their painter orders only differ on near-zero
    /// pairs (sub-pixel depth difference), so the visual result is
    /// indistinguishable.
    /// </summary>
    public const float PredicateEpsilon = 1e-3f;

    /// <summary>
    /// Build the BooleanNode predicate for one signature. Phase-0
    /// signatures are identified by face-visibility plus the signs of
    /// any rotation-dependent pair orderings. Constant pair orderings
    /// are encoded statically in <see cref="SignatureBake.Signature.Order"/>
    /// and don't need a runtime test.
    /// </summary>
    /// <param name="cullMarginCos">
    /// Must match the value passed to <see cref="SignatureBake.Bake"/>. Widens the
    /// face-front tolerance so the runtime predicate's notion of "visible" matches
    /// the bake-time sampler's notion. Without this match, signatures generated for
    /// near-edge faces never satisfy their predicate at runtime and the cell never
    /// "lights up" — every tree opacity stays 0 and the model renders as a dark blob.
    /// Pass 0 to preserve the original strict behaviour.
    /// </param>
    public static BooleanNode BuildPredicate(
        Matrix4x4Node bakedMatrix,
        ObjGeometry geometry,
        SignatureBake.Signature sig,
        float cullMarginCos = 0f)
    {
        var quads = geometry.Quads;
        int n = quads.Length;
        BooleanNode? acc = null;

        // Deduplicate identical terms. Subdivision splits each coplanar face
        // into many sub-quads that all share the SAME normal (and, within a
        // coplanar group, produce the SAME face-front comparison) and often the
        // SAME centroid-difference vector for a pair test. Because the predicate
        // is a pure conjunction, an identical term is idempotent (A && A == A),
        // so emitting it once is exactly equivalent and dramatically shorter.
        // Without this, a 52-sub-quad book produces a predicate that overflows
        // Composition's expression-string length cap and StartAnimation throws
        // "The expression string is too long" for every cell — leaving the whole
        // model invisible (only its drop shadow renders). Keying on the quantised
        // vector + sign collapses the duplicates that subdivision introduces.
        var seen = new System.Collections.Generic.HashSet<(int kind, long x, long y, long z, int sign)>();
        static long Quant(float f) => (long)System.MathF.Round(f * 4096f);

        // Face-front tests for every face (visible or hidden).
        //
        // Bake-time classification uses GeometryPredicates.IsFrontFacing(normalZ, cullMarginCos)
        // which is `normalZ + cullMarginCos > CosineEpsilon`, i.e. front-facing iff
        // `normalZ > CosineEpsilon - cullMarginCos` (≈ `normalZ > -cullMarginCos` for tiny
        // CosineEpsilon). The runtime predicate must agree with that threshold, otherwise
        // signatures generated for faces in the widened cone never match and their cell
        // tree never becomes opaque. We additionally apply PredicateEpsilon on both sides
        // to absorb CPU/GPU sign disagreement at the boundary (see the PredicateEpsilon
        // comment above), preserving the original tolerance behaviour when cullMarginCos = 0.
        float posThreshold = -(cullMarginCos + PredicateEpsilon);   // visible: normalZ > posThreshold
        float negThreshold = -cullMarginCos + PredicateEpsilon;     // hidden:  normalZ < negThreshold
        for (int q = 0; q < n; q++)
        {
            int fsign = sig.FaceSigns[q] > 0 ? 1 : -1;
            var fn = quads[q].Normal;
            if (!seen.Add((0, Quant(fn.X), Quant(fn.Y), Quant(fn.Z), fsign)))
                continue; // identical face-front comparison already emitted
            var normalZ = EventFunctions.TransformedDirectionZ(bakedMatrix, quads[q].Normal);
            BooleanNode test = sig.FaceSigns[q] > 0
                ? normalZ > (ScalarNode)posThreshold
                : normalZ < (ScalarNode)negThreshold;
            acc = acc is null ? test : ExpressionFunctions.And(acc, test);
        }

        // Pair-order tests for VARYING pairs only (PairSigns[i,j] != 0
        // means the pair's painter-order flips somewhere in the swept
        // parameter space; the bake's reduction left these alone in the
        // signature and dropped constant pairs to 0).
        // Sign convention: PairSigns[i,j] = +1 iff face j is drawn AFTER
        // face i (j in front). Test: dot(M·(c_j − c_i), z_view) > 0
        // means c_j is further along +Z than c_i, i.e. j is in front
        // under the viewer-looking-at-+Z convention.
        for (int i = 0; i < n; i++)
        {
            if (sig.FaceSigns[i] < 0) continue;
            for (int j = i + 1; j < n; j++)
            {
                if (sig.FaceSigns[j] < 0) continue;
                sbyte s = sig.PairSigns[i, j];
                if (s == 0) continue;
                var diff = quads[j].Centroid - quads[i].Centroid;
                int psign = s > 0 ? 1 : -1;
                if (!seen.Add((1, Quant(diff.X), Quant(diff.Y), Quant(diff.Z), psign)))
                    continue; // identical pair-order comparison already emitted
                var diffZ = EventFunctions.TransformedDirectionZ(bakedMatrix, diff);
                BooleanNode test = s > 0
                    ? diffZ > (ScalarNode)(-PredicateEpsilon)
                    : diffZ < (ScalarNode)(+PredicateEpsilon);
                acc = ExpressionFunctions.And(acc!, test);
            }
        }

        return acc!;
    }
}
