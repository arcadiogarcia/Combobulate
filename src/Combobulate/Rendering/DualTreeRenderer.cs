using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Combobulate.Caching;
using Combobulate.Sorting;

namespace Combobulate.Rendering;

/// <summary>
/// Renders an OBJ as a pair of pre-composed sprite trees ("A" and "B") whose
/// <see cref="Visual.Opacity"/> is driven by <see cref="ExpressionAnimation"/>s
/// that read the same composed-yaw scalar the rotation matrix does — so the
/// handoff between the two trees fires at the exact GPU yaw the compositor is
/// about to paint with, eliminating the CPU/GPU drift that causes flicker in
/// the single-tree painter path.
///
/// <para><b>Predictive handoff design.</b> At any moment one tree is "active"
/// (its opacity expression evaluates to 1 at the live GPU yaw) and the other is
/// "inactive" (opacity 0). Each <see cref="Update"/> tick:
/// <list type="number">
///   <item>Sample <c>y_now</c> and estimate angular velocity <c>ω</c> from the
///         delta since the previous tick.</item>
///   <item>If GPU yaw has crossed the previously armed handoff threshold,
///         swap roles (the formerly inactive tree becomes active).</item>
///   <item>Rebuild the (now) inactive tree's painter order at <c>y_predict =
///         y_now + ω · Δt_predict</c>, with a TIGHT cull margin sized only to
///         absorb a couple frames of CPU/GPU jitter.</item>
///   <item>Install fresh opacity expressions: active tree opaque iff GPU yaw is
///         on the trailing side of the new handoff midpoint, inactive opaque
///         iff GPU yaw is on the leading side. The handoff is placed strictly
///         in the future of <c>y_now</c>, so during the install commit window
///         BOTH expressions evaluate consistently at the current GPU yaw and
///         the cube remains continuously opaque.</item>
/// </list>
/// Because each tree's painter sort is only "alive" for the small angular range
/// between two consecutive handoffs (~ω · 2 frames worth of yaw, typically a
/// few degrees), the cull margin can be tight and the back-face / front-face
/// cone matches what the GPU actually paints.
/// </para>
///
/// <para><b>Why this avoids the previous design's amortization flaw:</b> the
/// earlier window-grid approach reused one sort across a 60° opaque window,
/// which forced a wide cull margin (~65°) so faces visible anywhere in the
/// window survived. That wide margin admitted faces that were back-facing at
/// the live yaw, producing the "extra slivers / wrong order" artifact. By
/// rebuilding every tick at predicted-next-yaw with a tight margin, the active
/// tree's sort is always within a few degrees of what GPU is drawing.</para>
/// </summary>
internal sealed class DualTreeRenderer : IDisposable
{
    // Bounds of the opacity expression. Composed yaw is in [0, 360 + sliderYaw),
    // typically [0, 360). We pick ±1e6 so the expression's [lo, hi) is "the
    // entire numeric line" when we want unconditional opacity.
    private const float HugeYaw = 1e6f;

    // Tight cull margin (degrees) for the per-tick rebuild. Sized to absorb:
    //  - one frame of CPU/GPU clock skew (~1-2° at 90°/s),
    //  - one frame of expression-commit latency (~1.5° at 90°/s),
    //  - any non-perfect omega prediction.
    // Five degrees is generous; the back-face cull will still reject any face
    // that has been back-facing for more than about 50ms of spin — so visible
    // back-face leakage is impossible.
    private const float TightCullMarginDeg = 5f;

    // Minimum forward lookahead for the handoff threshold. Even at ω≈0 the
    // handoff has to be strictly in the future so that during the install
    // commit window both the old and new expressions agree at the current
    // GPU yaw (see class doc). 2° is enough for 1-2 frames at typical ω.
    private const float MinLookaheadDeg = 2f;

    // Maximum forward lookahead. Caps how far the predicted yaw can drift
    // from the current yaw if ω spikes (e.g. the user grabs and flings a
    // slider). Beyond this the predicted sort starts to mis-cull faces that
    // are still front-facing at the current yaw.
    private const float MaxLookaheadDeg = 20f;

    // How many frames of motion to lookahead. The handoff fires when GPU yaw
    // crosses the predicted midpoint, which we want to happen within a few
    // ticks so each tree's sort stays fresh. 2 frames @ 60Hz = 33ms.
    private const float PredictFrames = 2f;

    // Wrap detection: if CPU yaw drops by more than this between consecutive
    // samples, we assume the GPU SpinYaw KFA wrapped 360→0 (or the user did
    // something extreme like dragging the slider). Re-init both trees from
    // scratch so the new windows are anchored at the new yaw.
    private const float WrapDetectDeltaDeg = 180f;

    // Smoothing factor for the omega estimate: ω_smoothed = (1-α)*ω_prev + α*ω_now.
    // 0.3 = aggressive enough to track changes within a few frames, but rejects
    // single-frame jitter from late ticks / GC pauses.
    private const float OmegaSmoothingAlpha = 0.3f;

    private readonly Compositor _compositor;
    private readonly ContainerVisual _parent;
    private readonly ContainerVisual _treeA;
    private readonly ContainerVisual _treeB;
    // Per-instance property set holding the lo/hi opacity thresholds for
    // both trees. ExpressionAnimations on _treeA.Opacity and _treeB.Opacity
    // reference these scalars and are installed ONCE at construction; we
    // update the thresholds via InsertScalar (atomic — no animation gap).
    // This avoids the previous design's StopAnimation/StartAnimation pattern
    // which left a one-frame window where Opacity defaulted to 1 on BOTH
    // trees, causing two over-painted (differently-sorted) cubes to show
    // simultaneously — visible as a "bowtie" double-cube artifact.
    private readonly CompositionPropertySet _opacityProps;
    private bool _opacityExpressionsInstalled;

    private SpriteVisual?[]? _poolA;
    private SpriteVisual?[]? _poolB;
    private bool[]? _visScratch;
    private int[]? _orderScratch;
    private IFaceSorter? _sorter;

    private ObjGeometry? _geometry;
    private ResolvedQuadMaterials? _bindings;
    private float _scale;
    private float _hostW;
    private float _hostH;
    private SortAlgorithm _algorithm;

    // Per-tick state for the predictive-handoff loop.
    private bool _initialized;
    private bool _activeIsA;            // which tree is currently active
    private float _activeBuildYaw;      // composed yaw at which the active tree's painter sort was computed
    private float _inactiveBuildYaw;    // composed yaw at which the inactive (just-rebuilt) tree's sort was computed
    private float _nextHandoffYaw;      // yaw threshold where the next handoff fires
    private bool _handoffForward;       // sign of motion at the time the current handoff was armed (true = ω>=0)
    private float _lastYawSample;       // composed yaw observed on previous tick (for omega estimate)
    private long _lastSampleTicks;      // Stopwatch ticks at previous sample (for omega estimate)
    private float _smoothedOmegaDegPerSec;

    public DualTreeRenderer(Compositor compositor, ContainerVisual parent)
    {
        _compositor = compositor;
        _parent = parent;
        _treeA = compositor.CreateContainerVisual();
        _treeB = compositor.CreateContainerVisual();
        _opacityProps = compositor.CreatePropertySet();
        // Single shared selector scalar — see EnsureOpacityExpressionsInstalled.
        // Initialise to 2.0 (neither 0 nor 1) so both trees start transparent
        // until the first Update bootstraps a real value, preventing a flash
        // of unsorted sprite content during construction.
        _opacityProps.InsertScalar("Sel", 2f);
        // Both trees occupy the full host area; opacity (driven by expression)
        // governs which one paints. Z-order between the two is irrelevant — the
        // inactive tree contributes nothing to the framebuffer.
        _parent.Children.InsertAtTop(_treeA);
        _parent.Children.InsertAtTop(_treeB);
    }

    public void Update(
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale,
        float hostW,
        float hostH,
        float cullMarginCos,
        float cameraDistance,
        SortAlgorithm sortAlgorithm,
        CompositionPropertySet spinYawSourceProps,
        string spinYawScalarName,
        string yawValScalarName,
        string spinActiveScalarName,
        Func<float, Matrix4x4> composedYawToRotation,
        float composedYawDegNow)
    {
        // ---- Pool reset on geometry/scale/host change ----
        bool poolReset = !ReferenceEquals(_geometry, geometry)
            || _scale != scale
            || _hostW != hostW
            || _hostH != hostH
            || _algorithm != sortAlgorithm;
        if (poolReset)
        {
            _treeA.Children.RemoveAll();
            _treeB.Children.RemoveAll();
            _poolA = new SpriteVisual?[geometry.Quads.Length];
            _poolB = new SpriteVisual?[geometry.Quads.Length];
            _visScratch = new bool[geometry.Quads.Length];
            _orderScratch = new int[geometry.Quads.Length];
            _sorter = FaceSorterFactory.Create(sortAlgorithm, geometry);
            _geometry = geometry;
            _scale = scale;
            _hostW = hostW;
            _hostH = hostH;
            _algorithm = sortAlgorithm;
            _initialized = false;
        }
        _bindings = bindings;

        float yNow = composedYawDegNow;

        // Use the host's cullMarginCos verbatim — exactly what SpritePainter
        // uses. Hardcoding a wider margin here (we previously used 5°) leaked
        // edge-on / barely-back-facing side faces when the cube was static or
        // slow-moving, because viewNormalZ near 0 + sin(5°) passes the
        // front-facing test. The predictive-handoff design keeps the active
        // tree's sort yaw within ~lookahead degrees of yNow, and the host
        // already widens CullMarginDegrees during spin (3°) and zeros it when
        // static — so trusting the host's value matches SpritePainter's
        // behaviour exactly across all motion regimes. The TightCullMarginDeg
        // constant remains for documentation but is no longer applied.
        float tightCullCos = cullMarginCos;

        // ---- Wrap detection ----
        // GPU's SpinYaw scalar is animated by a 0→360 KFA with IterationBehavior.Forever,
        // so composed yaw wraps every period. When CPU yaw drops by > 180° we
        // assume the wrap fired (or the user did something extreme like flinging
        // the slider). Force re-init so the new windows are anchored at the new
        // yaw — the old _nextHandoffYaw is stale once yaw has discontinuously
        // jumped.
        if (_initialized
            && !float.IsNaN(_lastYawSample)
            && MathF.Abs(yNow - _lastYawSample) > WrapDetectDeltaDeg)
        {
            _initialized = false;
        }

        // ---- Estimate angular velocity (deg/sec) ----
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        float omegaInstant = 0f;
        if (_initialized && _lastSampleTicks != 0)
        {
            long dtTicks = nowTicks - _lastSampleTicks;
            if (dtTicks > 0)
            {
                float dtSec = (float)((double)dtTicks / System.Diagnostics.Stopwatch.Frequency);
                omegaInstant = (yNow - _lastYawSample) / dtSec;
            }
        }
        if (_initialized)
        {
            _smoothedOmegaDegPerSec = (1f - OmegaSmoothingAlpha) * _smoothedOmegaDegPerSec
                + OmegaSmoothingAlpha * omegaInstant;
        }
        else
        {
            _smoothedOmegaDegPerSec = 0f;
        }
        _lastYawSample = yNow;
        _lastSampleTicks = nowTicks;

        // Compute predicted yaw `dtPredict` seconds into the future. `dtPredict`
        // is sized so that, at typical ω, the predicted yaw is a couple frames
        // ahead — enough that the handoff midpoint sits strictly in the future
        // even after rounding/quantization, but not so far ahead that the sort
        // is wildly stale by the time it's used.
        const float dtPredict = PredictFrames / 60f;
        float omega = _smoothedOmegaDegPerSec;
        float lookaheadAbs = MathF.Min(MaxLookaheadDeg,
            MathF.Max(MinLookaheadDeg, MathF.Abs(omega) * dtPredict));
        // Default to forward when ω is near zero so the bootstrap arms a
        // forward handoff (matches the common case of "user just pressed Spin").
        bool forward = omega >= 0f;
        float lookahead = forward ? lookaheadAbs : -lookaheadAbs;
        float yPredict = yNow + lookahead;

        // Inflate the cull margin to absorb staleness. The active tree was
        // built at some yaw `Y_active_built`, and remains active while GPU yaw
        // advances toward `Y_active_built + lookahead` (where the next handoff
        // fires). Faces that were marginally back-facing at `Y_active_built`
        // may have rotated forward by up to `lookahead` degrees by the end of
        // the active period — so we must keep them IsVisible=true through the
        // build, otherwise they pop out as missing faces near the handoff.
        // ONE direction of staleness suffices: the active tree only ever
        // becomes "more in the future" of its build yaw as time passes; it
        // never goes "into the past" of its build (we always rebuild at
        // yPredict in the FORWARD direction).
        //
        // Critically, the previous design used 2*lookaheadAbs which leaked
        // back-facing side faces during normal spin (cullMargin sin~0.07 +
        // 2*2°≈0.07 = 0.14 → 8° back-facing faces visible). At edge-on yaws
        // (~90°/270°) the BSP sort can paint such a back face on top of a
        // forward face, producing a 3-faces-visible artifact.
        //
        // Hard cap (`MaxCullInflationDeg`) defends against rare omega spikes
        // (e.g. immediately after wrap-detect resets smoothing).
        const float deg2rad = MathF.PI / 180f;
        const float MaxCullInflationDeg = 6f;
        float inflationDeg = MathF.Min(MaxCullInflationDeg, lookaheadAbs);
        float effectiveCullMarginCos = cullMarginCos + inflationDeg * deg2rad;
        if (effectiveCullMarginCos > 1f) effectiveCullMarginCos = 1f;
        tightCullCos = effectiveCullMarginCos;

        // ---- Bootstrap on first call / after pool reset / after wrap ----
        if (!_initialized)
        {
            EnsureOpacityExpressionsInstalled(spinYawSourceProps,
                spinYawScalarName, yawValScalarName, spinActiveScalarName);

            // A is the active tree (sorted at yNow); B is pre-warmed at yPredict.
            BuildTree(_treeA, _poolA!, geometry, bindings, scale, hostW, hostH,
                composedYawToRotation(yNow), cameraDistance, tightCullCos);
            BuildTree(_treeB, _poolB!, geometry, bindings, scale, hostW, hostH,
                composedYawToRotation(yPredict), cameraDistance, tightCullCos);

            _activeIsA = true;
            SetSelector(_activeIsA);
            _activeBuildYaw = yNow;
            _inactiveBuildYaw = yPredict;
            _nextHandoffYaw = (yNow + yPredict) * 0.5f;
            _handoffForward = forward;
            _initialized = true;
            return;
        }

        // ---- Steady-state: rebuild ONLY the INACTIVE tree. ----
        //
        // The active tree is the one the compositor is currently painting.
        // Even per-sprite Remove+InsertAtTop and per-sprite IsVisible writes
        // are NOT atomic across the ~N quads of the model — the compositor
        // can sample the visual tree mid-loop, after the cull pass has
        // hidden faces and BEFORE the reorder loop has finished re-promoting
        // them, producing a single-frame "thin sliver" or "missing faces"
        // artifact. Empirically (v60-v62) we observed these as ~1-in-200
        // broken frames during sustained 90°/sec spin.
        //
        // The dual-tree design's whole point is to mutate ONLY the off-
        // screen tree. So:
        //   - Handoff: flip selector (single InsertScalar) so the previously-
        //     INACTIVE tree (pre-built at the OLD yPredict, which is now
        //     close to current yNow) becomes the visible one. Its sort is
        //     fresh because we just built it on the previous tick.
        //   - Then rebuild only the now-INACTIVE tree at the new yPredict.
        //     Its mutations are invisible because Sel keeps its opacity at 0.
        //
        // Active tree freshness budget:
        //   active was last built at (handoff-time-yaw + lookahead). By the
        //   time we hit the NEXT handoff, GPU yaw has advanced by at most
        //   one full lookahead window (we set the handoff midpoint to
        //   ~lookahead/2 ahead of the build yaw). So the active sort is at
        //   most ~lookahead degrees stale at any point — covered by the
        //   2*lookahead cull margin inflation above.

        bool handoffFired = _handoffForward
            ? (yNow >= _nextHandoffYaw)
            : (yNow <= _nextHandoffYaw);

        // ---- Two-tick state machine: SWAP, then on next tick BUILD. ----
        //
        // Doing SetSelector and BuildTree on the same UI tick exposes a
        // race: InsertScalar on a CompositionPropertySet commits to the
        // compositor IMMEDIATELY (before the UI batch flushes), so the
        // selector flips visible-tree mid-Update. Visual children mutations
        // (Remove + InsertAtTop loop) inside BuildTree don't commit until
        // batch flush, so for the rest of this UI tick the compositor sees
        // the just-flipped tree's PRE-build state — fine. But on the NEXT
        // compositor frame, BuildTree's mutations have committed atomically
        // and the tree shows the NEW (post-flip) build, which targets
        // yPredict (lookahead degrees in the future). That's STALE relative
        // to the actual yaw at swap time, which is the prior _nextHandoffYaw
        // (just barely crossed, not yPredict's target).
        //
        // To keep the just-promoted tree's sort fresh AND avoid race-y
        // overlap, split swap and build across two ticks:
        //   tick T (handoff fires): SWAP. Tree (formerly inactive, sorted
        //                           at the OLD yPredict, which is now
        //                           ≈ yNow) becomes active. Do NOT mutate
        //                           any tree this tick.
        //   tick T+1 (post-swap):   BUILD now-inactive tree at the NEW
        //                           yPredict. (No swap this tick.)
        //   ticks T+2 ... T+N:      BUILD inactive tree at latest yPredict.
        //                           Keeps the inactive sort fresh while
        //                           waiting for the next yaw threshold.
        if (handoffFired)
        {
            _activeIsA = !_activeIsA;
            SetSelector(_activeIsA);
            // The just-promoted tree was built at the old yPredict, so its
            // sort is already fresh for ~yNow. Don't mutate either tree
            // this tick.
            _activeBuildYaw = _inactiveBuildYaw;

            // Arm the next handoff. yPredict is forward of yNow by
            // `lookahead`; the next swap should fire roughly when GPU yaw
            // crosses (yNow + lookahead/2).
            float newHandoff = (yNow + yPredict) * 0.5f;
            if (forward)
            {
                float minHandoff = yNow + MinLookaheadDeg * 0.5f;
                if (newHandoff < minHandoff) newHandoff = minHandoff;
            }
            else
            {
                float maxHandoff = yNow - MinLookaheadDeg * 0.5f;
                if (newHandoff > maxHandoff) newHandoff = maxHandoff;
            }
            _nextHandoffYaw = newHandoff;
            _handoffForward = forward;
            return;
        }

        // ---- Non-swap tick: rebuild ONLY the inactive tree at latest yPredict. ----
        SpriteVisual?[] inactivePool = _activeIsA ? _poolB! : _poolA!;
        ContainerVisual inactiveTree = _activeIsA ? _treeB : _treeA;
        BuildTree(inactiveTree, inactivePool, geometry, bindings, scale, hostW, hostH,
            composedYawToRotation(yPredict), cameraDistance, tightCullCos);

        _inactiveBuildYaw = yPredict;
        _handoffForward = forward;
    }

    private void EnsureOpacityExpressionsInstalled(
        CompositionPropertySet spinYawSourceProps,
        string spinYawScalarName,
        string yawValScalarName,
        string spinActiveScalarName)
    {
        if (_opacityExpressionsInstalled) return;

        // Single shared selector scalar `Sel` on _opacityProps:
        //   Sel == 0 -> tree A opaque, tree B transparent
        //   Sel == 1 -> tree A transparent, tree B opaque
        // Both expressions read the SAME scalar, so a single InsertScalar("Sel", v)
        // is observed atomically by both expressions on the next compositor frame
        // — there is no window where neither (or both) is opaque.
        //
        // We deliberately do NOT gate opacity on the live GPU yaw any more:
        // the four-threshold scheme that compared the GPU's yaw against
        // separate per-tree (lo, hi) pairs proved racy in practice — the four
        // InsertScalar calls did not always commit as a single atomic unit
        // observable by the expression evaluator, producing 1-frame windows
        // where both ranges fired or neither did. The artefact looked like
        // missing faces / double-painted faces flashing intermittently during
        // spin (~ once per few hundred frames at 60Hz).
        //
        // The unused yaw-source parameters are kept so callers don't have to
        // change their API; they are also referenced once via a no-op term
        // (multiplying by 0.0) so the parameter set is non-empty and the
        // expression compiler does not error on an unreferenced param.
        _ = spinActiveScalarName; _ = yawValScalarName; _ = spinYawScalarName;

        var exprA = _compositor.CreateExpressionAnimation("(t.Sel < 0.5) ? 1.0 : 0.0");
        exprA.SetReferenceParameter("t", _opacityProps);
        _treeA.StartAnimation("Opacity", exprA);

        var exprB = _compositor.CreateExpressionAnimation("(t.Sel < 0.5) ? 0.0 : 1.0");
        exprB.SetReferenceParameter("t", _opacityProps);
        _treeB.StartAnimation("Opacity", exprB);

        _opacityExpressionsInstalled = true;
    }

    private void SetSelector(bool activeIsA)
    {
        // One InsertScalar = atomically observed by both expressions next frame.
        _opacityProps.InsertScalar("Sel", activeIsA ? 0f : 1f);
    }

    /// <summary>
    /// Rebuilds the supplied tree's sprite content to render <paramref name="geometry"/>
    /// at <paramref name="rotation"/>. Sprites are pooled across rebuilds; only painter
    /// order (sibling order) and per-sprite <see cref="Visual.IsVisible"/> change
    /// from one rebuild to the next.
    /// </summary>
    private void BuildTree(
        ContainerVisual tree,
        SpriteVisual?[] pool,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale,
        float hostW,
        float hostH,
        Matrix4x4 rotation,
        float cameraDistance,
        float cullMarginCos)
    {
        var origin = new Vector3(hostW / 2f, hostH / 2f, 0);
        var quads = geometry.Quads;

        // Lazily create / refresh sprites for each cached quad.
        for (int i = 0; i < quads.Length; i++)
        {
            var sprite = pool[i];
            if (sprite == null)
            {
                sprite = _compositor.CreateSpriteVisual();
                sprite.Size = new Vector2(1f, 1f);
                sprite.IsVisible = false;
                pool[i] = sprite;
                tree.Children.InsertAtTop(sprite);
            }
            // Per-sprite transform places the quad in screen space at its
            // unrotated (model-space scaled) position. The actual rotation
            // is applied by the host's `_root.TransformMatrix` (driven by
            // an ExpressionAnimation off the same property set the spin
            // animates). The rotation parameter to this method is used
            // ONLY by the sorter to compute painter order / cull, NOT for
            // per-sprite placement — exactly as the SpritePainter path does.
            var cq = quads[i];
            var v0 = cq.V0 * scale + origin;
            var v1 = cq.V1 * scale + origin;
            var v3 = cq.V3 * scale + origin;
            var xAxis = v1 - v0;
            var yAxis = v3 - v0;
            var zAxis = Vector3.Normalize(Vector3.Cross(xAxis, yAxis));
            sprite.TransformMatrix = new Matrix4x4(
                xAxis.X, xAxis.Y, xAxis.Z, 0,
                yAxis.X, yAxis.Y, yAxis.Z, 0,
                zAxis.X, zAxis.Y, zAxis.Z, 0,
                v0.X,    v0.Y,    v0.Z,    1);
            sprite.Brush = bindings.Bindings[i].Brush;
        }

        // Sort & cull at this rotation, then walk the back-to-front order
        // re-inserting visible sprites in painter order.
        int orderCount = _sorter!.Sort(rotation, _orderScratch!, _visScratch!, cameraDistance, cullMarginCos);

        // Apply visibility.
        for (int i = 0; i < quads.Length; i++)
        {
            pool[i]!.IsVisible = _visScratch![i];
        }

        // Reorder children WITHOUT a RemoveAll() pass.
        //
        // RemoveAll() followed by a re-insert loop exposes a window where the
        // VisualCollection is empty (or partially populated) to the compositor.
        // Even with opacity gating via the selector scalar, the compositor
        // commits the structural mutation independently of opacity expression
        // evaluation — so a scheduled paint between RemoveAll and the final
        // InsertAtTop renders the active tree with missing/wrong-ordered
        // sprites, producing the broken-cube artifact observed during spin.
        //
        // Instead, follow SpritePainter's exact pattern (see Combobulate.cs):
        //   - All sprites are inserted at top once, at first creation, and
        //     stay parented permanently.
        //   - To impose painter order, walk the back-to-front permutation and
        //     for each visible sprite do Remove + InsertAtTop. Each pair is
        //     applied as a single sibling-order change by Composition; at no
        //     point is the collection structurally short of children.
        //   - Invisible sprites stay parented (their IsVisible=false hides
        //     them) so they don't disturb the front-of-list ordering.
        for (int i = 0; i < orderCount; i++)
        {
            int qi = _orderScratch![i];
            if (!_visScratch![qi]) continue;
            var sprite = pool[qi]!;
            tree.Children.Remove(sprite);
            tree.Children.InsertAtTop(sprite);
        }
    }

    /// <summary>
    /// (Removed) Per-tick StartAnimation install path — replaced by
    /// <see cref="EnsureOpacityExpressionsInstalled"/> + <see cref="SetThresholds"/>.
    /// </summary>

    public void Dispose()
    {
        _treeA.StopAnimation("Opacity");
        _treeB.StopAnimation("Opacity");
        _treeA.Children.RemoveAll();
        _treeB.Children.RemoveAll();
        if (_parent.Children.Contains(_treeA)) _parent.Children.Remove(_treeA);
        if (_parent.Children.Contains(_treeB)) _parent.Children.Remove(_treeB);
        _treeA.Dispose();
        _treeB.Dispose();
        if (_poolA != null)
            for (int i = 0; i < _poolA.Length; i++) _poolA[i]?.Dispose();
        if (_poolB != null)
            for (int i = 0; i < _poolB.Length; i++) _poolB[i]?.Dispose();
        _poolA = null;
        _poolB = null;
    }
}
