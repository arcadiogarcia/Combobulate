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
    /// Build the BooleanNode predicate for one signature given a baked-matrix
    /// reference (a CompositionPropertySet's Matrix4x4 property "M" that
    /// holds the live transform). The predicate references that single
    /// matrix subchannel-by-subchannel; per-event expression size stays
    /// constant regardless of how complex the user-supplied transform AST
    /// is. Side-effect-free; safe to call from any thread.
    /// </summary>
    public static BooleanNode BuildPredicate(
        Matrix4x4Node bakedMatrix,
        ObjGeometry geometry,
        SignatureBake.Signature sig)
    {
        var quads = geometry.Quads;
        int n = quads.Length;
        BooleanNode? acc = null;

        // Face-front tests for every face (visible or hidden in this signature).
        // Convention: + sign → normalZ > 0; - sign → normalZ <= 0. Faces edge-on
        // (normalZ exactly 0) are canonically hidden, matching geometry.
        for (int q = 0; q < n; q++)
        {
            var normalZ = EventFunctions.TransformedDirectionZ(bakedMatrix, quads[q].Normal);
            BooleanNode test = sig.FaceSigns[q] > 0
                ? normalZ > (ScalarNode)0f
                : normalZ <= (ScalarNode)0f;
            acc = acc is null ? test : ExpressionFunctions.And(acc, test);
        }

        // Depth-pair tests for mutually-visible pairs only.
        for (int i = 0; i < n; i++)
        {
            if (sig.FaceSigns[i] < 0) continue;
            for (int j = i + 1; j < n; j++)
            {
                if (sig.FaceSigns[j] < 0) continue;
                sbyte s = sig.PairSigns[i, j];
                if (s == 0) continue;
                var diff = quads[j].Centroid - quads[i].Centroid;
                var diffZ = EventFunctions.TransformedDirectionZ(bakedMatrix, diff);
                BooleanNode test = s > 0
                    ? diffZ > (ScalarNode)0f
                    : diffZ <= (ScalarNode)0f;
                acc = ExpressionFunctions.And(acc!, test);
            }
        }

        return acc!;
    }
}
