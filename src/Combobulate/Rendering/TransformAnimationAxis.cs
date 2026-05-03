using System;
using Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork;

namespace Combobulate.Rendering;

/// <summary>
/// Describes one live scalar input axis of a transform animation: which
/// <see cref="ScalarNode"/> to substitute during bake, what numeric range
/// it sweeps over, and whether that range wraps periodically.
///
/// <para>Used by <see cref="Combobulate.SetTransformAnimation(Matrix4x4Node, TransformAnimationAxis[])"/>
/// to declare the input space the analytical aspect-graph should bake.
/// For 1-axis cases (e.g. a single periodic yaw) the bake is precise
/// (bisection). For 2+ axes the bake samples a regular grid; you can
/// trade fidelity for memory by adjusting <see cref="Samples"/>.</para>
/// </summary>
public sealed class TransformAnimationAxis
{
    /// <summary>The expression-tree leaf this axis corresponds to.</summary>
    public ScalarNode Scalar { get; }

    /// <summary>Lower bound (inclusive) of the swept range.</summary>
    public float Min { get; }

    /// <summary>Length of the swept range. Upper bound (exclusive) is
    /// <c>Min + Length</c>. Must be positive.</summary>
    public float Length { get; }

    /// <summary>If true, the live scalar is wrapped mod
    /// <see cref="Length"/> before being compared against cell bounds, so
    /// values past the swept range fold back to the start.</summary>
    public bool Periodic { get; }

    /// <summary>For multi-axis bakes, how many grid samples this axis
    /// gets. Ignored for 1-axis bakes (those use bisection on a fixed
    /// 720-sample coarse sweep).</summary>
    public int Samples { get; }

    public TransformAnimationAxis(
        ScalarNode scalar,
        float min,
        float length,
        bool periodic,
        int samples = 12)
    {
        if (scalar is null) throw new ArgumentNullException(nameof(scalar));
        if (length <= 0f) throw new ArgumentOutOfRangeException(nameof(length));
        if (samples < 4) throw new ArgumentOutOfRangeException(nameof(samples));
        Scalar = scalar;
        Min = min;
        Length = length;
        Periodic = periodic;
        Samples = samples;
    }

    /// <summary>Convenience: construct a 0..360° periodic yaw/pitch/roll
    /// axis from the supplied scalar.</summary>
    public static TransformAnimationAxis FullCircleDeg(ScalarNode scalar, int samples = 12)
        => new TransformAnimationAxis(scalar, min: 0f, length: 360f, periodic: true, samples: samples);
}
