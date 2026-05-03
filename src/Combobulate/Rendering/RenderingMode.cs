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

    /// <summary>
    /// Analytical, fully pre-baked path for compositor-driven yaw spins.
    /// At setup time Combobulate sweeps the configured face sorter across
    /// the yaw period, finds every breakpoint where the painter ordering
    /// or visibility mask changes (via bisection on the sorter signature),
    /// and pre-builds one <c>ContainerVisual</c> per constant-order cell
    /// with its sprites in that cell's painter order. Each container's
    /// Opacity is driven by an ExpressionAnimation that compares the live
    /// yaw scalar against the cell bounds. Once installed, the runtime
    /// does ZERO work on the UI thread per frame: the compositor evaluates
    /// the opacity expressions on the rendering thread, and at the moment
    /// yaw crosses a breakpoint the previous cell's opacity goes to 0 and
    /// the next cell's goes to 1 — observed atomically by the rasteriser.
    /// Revolution N is bit-identical to revolution 1 by construction (no
    /// CPU-side state survives between frames).
    ///
    /// <para>Memory: K cells × N quads sprites total (cube ~8×6=48,
    /// book ~30×32≈960). Bake cost: K painter sorts plus a few thousand
    /// bisection probes; on the order of milliseconds for low-poly meshes.</para>
    ///
    /// <para>Requires the host to wire up the spin yaw source via
    /// <see cref="Combobulate.SetSpinYawSource"/>.</para>
    /// </summary>
    BakedAspectGraph = 2,
}
