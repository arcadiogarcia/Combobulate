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
    /// signatures are now identified by face-visibility ALONE (pair
    /// painter-orderings are constant per-pair under pure rotation, so
    /// they're encoded in <see cref="SignatureBake.Signature.Order"/> at
    /// bake time and don't need runtime testing). The predicate is the
    /// conjunction of N face-front comparisons; per-cell expression size
    /// is linear in face count regardless of model complexity, so even
    /// large models stay well within the compositor expression-string
    /// length limit.
    /// </summary>
    public static BooleanNode BuildPredicate(
        Matrix4x4Node bakedMatrix,
        ObjGeometry geometry,
        SignatureBake.Signature sig)
    {
        var quads = geometry.Quads;
        int n = quads.Length;
        BooleanNode? acc = null;

        // Face-front tests for every face (visible or hidden).
        // Tolerant convention: + sign → normalZ > -eps; - sign → normalZ < +eps.
        for (int q = 0; q < n; q++)
        {
            var normalZ = EventFunctions.TransformedDirectionZ(bakedMatrix, quads[q].Normal);
            BooleanNode test = sig.FaceSigns[q] > 0
                ? normalZ > (ScalarNode)(-PredicateEpsilon)
                : normalZ < (ScalarNode)(+PredicateEpsilon);
            acc = acc is null ? test : ExpressionFunctions.And(acc, test);
        }

        return acc!;
    }
}
