using System.Numerics;
using Combobulate.Caching;
using Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork;

namespace Combobulate.Rendering;

/// <summary>
/// Phase 0 (signature-based BakedAspectGraph): builds the small set of
/// scalar event functions whose <i>signs</i> uniquely determine the
/// painter signature of the model under any rotation in the supplied
/// <see cref="Matrix4x4Node"/> AST.
///
/// <para>Two kinds of events:</para>
/// <list type="bullet">
/// <item>
/// <b>Front-facing event per face</b>: <c>(M · normal).z</c>. The sign
/// determines whether the face's outward normal projects toward or away
/// from the viewer, i.e. whether the face is visible.
/// </item>
/// <item>
/// <b>Depth-pair event per ordered pair</b>: <c>(M · centroid_j).z -
/// (M · centroid_i).z</c>. The sign determines which face's centroid is
/// further from the viewer along Z, i.e. the centroid-Z painter ordering
/// between the pair.
/// </item>
/// </list>
///
/// <para>The painter signature for a configuration θ is the sign vector
/// of <i>all</i> face events plus the sign vector of all pair events
/// where <i>both</i> faces are front-facing. The runtime compositor
/// predicate for a baked signature is the conjunction of those signed
/// inequalities. For a cube under arbitrary rotation that is 6 face
/// events + at most 15 pair events = 21 scalar comparisons per
/// signature, evaluated entirely on the compositor thread. Cell
/// boundaries are now <i>exact</i> — there is no grid sampling artifact
/// because every θ has exactly one sign vector and that vector is what
/// the compositor evaluates.</para>
///
/// <para>The matrix-vector multiplication is expressed via
/// <see cref="Matrix4x4Node"/> subchannel access (Channel13/23/33/43)
/// matching <see cref="System.Numerics.Vector3.Transform(Vector3, Matrix4x4)"/>'s
/// row-major / column-vector convention used elsewhere in Combobulate.</para>
/// </summary>
internal static class EventFunctions
{
    /// <summary>
    /// <c>(M · point).z</c> as a typed <see cref="ScalarNode"/>:
    /// <c>v.x*M13 + v.y*M23 + v.z*M33 + M43</c>.
    /// </summary>
    public static ScalarNode TransformedPointZ(Matrix4x4Node M, Vector3 v)
    {
        // ExpressionsFork already supports ScalarNode +/- ScalarNode and
        // ScalarNode * float. Materialise the matrix subchannels once and
        // use them as ScalarNode leaves; the AST shares them when many
        // events are built from the same M.
        var m13 = M.Channel13;
        var m23 = M.Channel23;
        var m33 = M.Channel33;
        var m43 = M.Channel43;
        return m13 * v.X + m23 * v.Y + m33 * v.Z + m43;
    }

    /// <summary>
    /// <c>(M · direction).z</c> as a typed <see cref="ScalarNode"/>: no
    /// translation contribution. <c>n.x*M13 + n.y*M23 + n.z*M33</c>.
    /// </summary>
    public static ScalarNode TransformedDirectionZ(Matrix4x4Node M, Vector3 n)
    {
        var m13 = M.Channel13;
        var m23 = M.Channel23;
        var m33 = M.Channel33;
        return m13 * n.X + m23 * n.Y + m33 * n.Z;
    }

    /// <summary>
    /// CPU evaluation of <c>(M · point).z</c>. Used during the bake's
    /// signature-discovery sweep so we can compute event signs on the
    /// CPU after evaluating the transform AST once per sample.
    /// </summary>
    public static float EvalPointZ(in Matrix4x4 M, Vector3 v)
        => v.X * M.M13 + v.Y * M.M23 + v.Z * M.M33 + M.M43;

    /// <summary>CPU evaluation of <c>(M · direction).z</c>.</summary>
    public static float EvalDirectionZ(in Matrix4x4 M, Vector3 n)
        => n.X * M.M13 + n.Y * M.M23 + n.Z * M.M33;
}
