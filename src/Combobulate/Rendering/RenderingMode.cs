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
    /// <para>Requires the host to wire up the transform animation via
    /// <see cref="Combobulate.SetTransformAnimation(Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.Matrix4x4Node, TransformAnimationAxis[])"/>.</para>
    /// </summary>
    BakedAspectGraph = 2,

    /// <summary>
    /// Composition-thread live back-face cull for <b>convex</b> solids. Uses the
    /// same single set of N <c>SpriteVisual</c>s as <see cref="SpritePainter"/>,
    /// but instead of the UI thread running the face sorter every frame, each
    /// face's <c>Opacity</c> is driven by an <c>ExpressionAnimation</c> that
    /// evaluates the exact <see cref="Sorting.GeometryPredicates"/> front-face
    /// test (orthographic or perspective, matching
    /// <see cref="Combobulate.EnablePerspective"/>) against the live rotation
    /// matrix on the <b>compositor thread</b>. A back-facing face animates its
    /// Opacity to 0; a front-facing face to 1 — evaluated on the rendering
    /// thread from the same matrix the GPU draws with, so there is zero CPU/GPU
    /// sync lag (no <see cref="Combobulate.CullMarginDegrees"/> band needed) and
    /// zero per-frame UI-thread work once installed.
    ///
    /// <para><b>Convex precondition.</b> No painter re-sort is performed: for a
    /// convex polyhedron the set of front-facing faces never overlap in the
    /// projected image, so their draw order is irrelevant. Using this mode with
    /// a non-convex mesh will show incorrect occlusion between mutually visible
    /// faces — use <see cref="SpritePainter"/> or <see cref="BakedAspectGraph"/>
    /// for concave meshes.</para>
    ///
    /// <para><b>Requires a matrix external rotation.</b> The Opacity expressions
    /// reference the rotation matrix installed via
    /// <see cref="Combobulate.SetExternalRotationMatrix"/>.
    /// When no matrix external rotation is active the renderer falls back to the
    /// <see cref="SpritePainter"/> CPU cull for that frame.</para>
    /// </summary>
    ConvexLiveCull = 3,
}
