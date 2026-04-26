namespace Combobulate.Rendering;

/// <summary>
/// Selects the per-frame composition strategy used by the renderer.
/// </summary>
public enum RenderingMode
{
    /// <summary>
    /// Default. Single set of N <c>SpriteVisual</c>s; each frame the UI
    /// thread runs the configured <see cref="Sorting.IFaceSorter"/> against
    /// the current rotation and mutates <c>VisualCollection</c> via
    /// <c>Remove</c> + <c>InsertAtTop</c> to put faces in painter's order.
    /// Simple, but the sort sees a CPU-supplied rotation that may lag the
    /// GPU-painted rotation by up to one compositor frame at high spin
    /// speeds, which the <see cref="Combobulate.CullMarginDegrees"/> band
    /// papers over.
    /// </summary>
    SpritePainter = 0,

    /// <summary>
    /// Two parallel <c>ContainerVisual</c> trees, each holding a complete
    /// per-face sprite set in a different painter order. An
    /// <c>ExpressionAnimation</c> on each tree's <c>Opacity</c> reads the
    /// same rotation property the GPU is animating from, so the swap from
    /// "current order" to "next order" fires at the EXACT yaw the
    /// compositor is about to paint with — no CPU/GPU yaw drift, and no
    /// torn-mid-mutation paint between the two orderings. The UI thread's
    /// job becomes "have the next-order tree ready before the next BSP
    /// boundary yaw arrives", which is predictable in advance from the
    /// spin schedule. Costs 2× the sprite count (brushes are shared).
    ///
    /// <para>Requires the host to wire up the spin yaw source via
    /// <see cref="Combobulate.SetSpinYawSource"/>; without it, this mode
    /// falls back to single-tree behaviour for static rotations and never
    /// activates the dual-tree swap.</para>
    /// </summary>
    DualTreeAtomicSwap = 1,
}
