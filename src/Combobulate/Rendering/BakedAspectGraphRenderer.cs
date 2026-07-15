using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
#if WINAPPSDK
using Microsoft.UI.Composition;
using DispatcherQueueNS = Microsoft.UI.Dispatching;
#else
using Windows.UI.Composition;
using DispatcherQueueNS = Windows.System;
#endif
using Combobulate.Caching;
using Combobulate.Sorting;
using Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork;
using CompositionExpressions;

namespace Combobulate.Rendering;

/// <summary>
/// Analytical "aspect-graph" renderer. The caller supplies a typed
/// <see cref="Matrix4x4Node"/> describing the model transform plus a list
/// of <see cref="TransformAnimationAxis"/> entries identifying every live
/// scalar input the AST depends on. The renderer:
///
/// <list type="bullet">
///   <item>Runs the bake compute (sweeping the axes, evaluating the AST,
///         and running the painter sorter at every sample) on a background
///         thread so the UI stays responsive even when the input space is
///         large (3-axis rotation can mean thousands of evaluations).</item>
///   <item>Materialises one <see cref="ContainerVisual"/> per painter
///         cell on the UI thread once compute completes, with sprites
///         already in painter order. Each container's <c>Opacity</c> is
///         driven by an <see cref="ExpressionAnimation"/> that tests
///         whether the live axis values fall inside the cell's box (per
///         axis, AND-ed, periodic axes wrapped in-expression).</item>
/// </list>
///
/// <para>While compute is in flight the renderer keeps the previously-
/// baked trees visible (so the user keeps seeing a valid render),
/// scheduling at most one bake at a time and discarding stale results.</para>
///
/// <para>After bake, the runtime is pure compositor work: zero CPU per
/// frame. By construction, revolutions of any periodic axis are bit-
/// identical because the same K cells are referenced via the same
/// expression every revolution.</para>
/// </summary>
internal sealed class BakedAspectGraphRenderer : IDisposable
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _parent;

    private ContainerVisual[]? _trees;
    /// <summary>
    /// For each entry of <see cref="_trees"/>, the cached-quad indices of
    /// the visible sprites in painter order (matches the order in which
    /// they were inserted as children of the tree). Used by
    /// <see cref="UpdateBindings"/> to map a child position back to its
    /// source quad so a hot brush swap can find the right entry in
    /// <see cref="ResolvedQuadMaterials"/> without touching the bake.
    /// </summary>
    private int[][]? _treeVisibleQuadIndices;
    private List<SpriteVisual>[]? _spritesByQuad;
    /// <summary>
    /// Geometry the current trees were built against; needed by
    /// <see cref="UpdateBindings"/> when reusing existing trees so we
    /// can validate the new bindings have the same quad count.
    /// </summary>
    private ObjGeometry? _treesGeometry;
    // Staging trees from the in-flight bake. They are inserted into
    // _parent.Children at Opacity=0 as ChunkBuild progresses; on Dispose
    // (or on stale-generation bail-out) we walk this and remove them so
    // they don't accumulate as ghost geometry in _root when the bake is
    // cancelled mid-materialise (e.g. mode-toggle off-and-on).
    private System.Collections.Generic.List<ContainerVisual>? _stagingTrees;
    private bool _disposed;

    // Phase-0 baked-matrix property set: the renderer evaluates the user's
    // entire transform AST exactly once per frame on the compositor and
    // writes the resulting Matrix4x4 here. Per-cell predicates then
    // reference this single matrix's subchannels rather than rebuilding
    // the user AST per event — essential to keep per-cell expression
    // strings small enough for the compositor parser at higher cell
    // counts.
    private CompositionPropertySet? _bakedMatrixProps;
    private ExpressionAnimation? _bakedMatrixAnim;
    private const string BakedMatrixPropertyName = "M";

    // Last successful bake's signatures (preserved for diagnostic
    // reporting: live sign vector vs. baked sign vectors).
    private SignatureBake.Signature[]? _lastBakeSignatures;
    private ObjGeometry? _lastBakeGeometry;

    // Cached bake inputs (so MaybeRebake / completion callbacks can compare).
    private Matrix4x4Node? _transformNode;
    private TransformAnimationAxis[]? _axes;
    private ObjGeometry? _bakedGeometry;
    private ResolvedQuadMaterials? _bakedBindings;
    private float _bakedScale, _bakedHostW, _bakedHostH;
    private float _bakedCullMarginCos, _bakedCameraDistance;
    private SortAlgorithm _bakedAlgorithm;

    // Background-thread bake coordination. _bakeGeneration is bumped by
    // the UI thread on every fresh bake request; the background task
    // checks the generation when it completes and discards its result if
    // the world has moved on.
    private int _bakeGeneration;
    private CancellationTokenSource? _bakeCts;

    /// <summary>
    /// Set whenever a bake request is in flight; UI-thread <c>Update</c>
    /// reads this to decide whether to skip starting another bake.
    /// </summary>
    public bool BakeInFlight => _bakeCts != null;

    public int CellCount => _trees?.Length ?? 0;

    public BakedAspectGraphRenderer(Compositor compositor, ContainerVisual parent)
    {
        _compositor = compositor;
        _parent = parent;
    }

    /// <summary>
    /// Begin a fresh bake. The compute runs on the thread pool;
    /// materialisation happens on the UI thread when compute completes,
    /// scheduled via <see cref="DispatcherQueue.GetForCurrentThread"/>.
    /// Returns immediately. If a previous bake is still in flight, it is
    /// cancelled.
    /// </summary>
    public void RequestBake(
        Matrix4x4Node transformNode,
        TransformAnimationAxis[] axes,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale,
        float hostW,
        float hostH,
        float cullMarginCos,
        float cameraDistance,
        SortAlgorithm sortAlgorithm)
    {
        if (axes is null || axes.Length == 0)
            throw new ArgumentException("At least one axis required.", nameof(axes));

        _transformNode = transformNode;
        _axes = axes;
        _bakedGeometry = geometry;
        _bakedBindings = bindings;
        _bakedScale = scale;
        _bakedHostW = hostW;
        _bakedHostH = hostH;
        _bakedCullMarginCos = cullMarginCos;
        _bakedCameraDistance = cameraDistance;
        _bakedAlgorithm = sortAlgorithm;

        // Cancel any in-flight bake; bump generation so the previous one's
        // completion callback (if it raced past cancellation) sees a stale
        // generation and bails out.
        _bakeCts?.Cancel();
        _bakeCts = new CancellationTokenSource();
        var ct = _bakeCts.Token;
        int myGeneration = ++_bakeGeneration;

        // Capture the UI dispatcher so we can hop back for materialisation.
        var ui = DispatcherQueueNS.DispatcherQueue.GetForCurrentThread();

        _ = Task.Run(() =>
        {
            try
            {
                ComputedBake computed = ComputeBake(transformNode, axes, geometry,
                    cullMarginCos, cameraDistance, sortAlgorithm, ct);

                if (ct.IsCancellationRequested) return;

                // Hop back to UI thread to materialise. The materialise
                // step itself is chunked across multiple UI ticks so the
                // dispatcher doesn't block; it disposes the _bakeCts in
                // its final tick (see ChunkBuild below).
                ui.TryEnqueue(() =>
                {
                    if (myGeneration != _bakeGeneration) return;
                    if (ct.IsCancellationRequested) return;
                    Materialise(computed, geometry, bindings, scale, hostW, hostH, axes, ui, myGeneration);
                });
            }
            catch (OperationCanceledException) { /* normal */ }
            catch (Exception ex)
            {
                // Don't take the app down on a bake error, but surface the
                // problem so the user/dev can see it during development.
                System.Diagnostics.Debug.WriteLine(
                    $"[BakedAspectGraphRenderer] Bake failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                try
                {
                    var dir = System.IO.Path.Combine(
                        Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                        "debug-artifacts");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(dir, "baked-aspect-graph.log"),
                        $"[{DateTime.Now:HH:mm:ss.fff}] BAKE EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
                }
                catch { }
            }
        }, ct);
    }

    /// <summary>
    /// One unique painter signature lifted to the renderer. Replaces the
    /// previous axis-rectangle <c>CellSig</c>: cells in θ-space are now
    /// implicitly defined by the sign vectors of <see cref="EventFunctions"/>
    /// rather than axis-aligned bounds.
    /// </summary>
    private struct CellSig
    {
        public SignatureBake.Signature Sig;
    }

    private struct ComputedBake { public CellSig[] Cells; }

    /// <summary>Background-thread compute: enumerate signatures via
    /// <see cref="SignatureBake"/>. No Composition deps.</summary>
    private static ComputedBake ComputeBake(
        Matrix4x4Node transformNode,
        TransformAnimationAxis[] axes,
        ObjGeometry geometry,
        float cullMarginCos,
        float cameraDistance,
        SortAlgorithm sortAlgorithm,
        CancellationToken ct)
    {
        // BakedAspectGraph delegates per-sample painter ordering to the
        // configured SortAlgorithm (BSP/Newell/Topological) so it gets
        // the same order the SpritePainter path would. Pair signs that
        // vary across rotation samples become rotation-dependent
        // predicates at runtime.
        var sigs = SignatureBake.Bake(transformNode, axes, geometry, sortAlgorithm, cameraDistance, cullMarginCos, ct);
        var cellSigs = new CellSig[sigs.Length];
        for (int i = 0; i < sigs.Length; i++) cellSigs[i] = new CellSig { Sig = sigs[i] };
        return new ComputedBake { Cells = cellSigs };
    }
    /// <summary>
    /// UI-thread phase: materialise cells into Composition trees, spread
    /// across multiple dispatcher ticks so the UI thread never blocks for
    /// more than ~one frame's worth of work. Old trees stay visible until
    /// the new set is fully built; final tick atomically swaps.
    ///
    /// <para>The chunk budget (cells per tick) is chosen so each tick
    /// fits in a typical 16ms frame. Materialisation is dominated by
    /// SpriteVisual creation + StartAnimation; profiling shows ~30–50
    /// cells/ms on a desktop, so a budget of 32 cells per tick keeps each
    /// tick under 1ms even on slower machines.</para>
    /// </summary>
    private const int MaterialiseChunkSize = 32;

    /// <summary>
    /// Conservative anti-seam outset, in host pixels, applied to every quad
    /// sprite on all four sides. Adjacent sub-quads are independent
    /// SpriteVisuals; where they abut, each anti-aliases its own shared edge
    /// to ~50% coverage so the composited union is only ~75% opaque and the
    /// background bleeds through as a hairline seam. Growing each sprite
    /// outward by this many pixels makes neighbours overlap so the union is
    /// fully opaque. The brush UV crop is expanded by the same fraction (see
    /// MaterialiseChunk) so interior texels stay exactly registered.
    ///
    /// Set by the owning <see cref="Combobulate"/> control from its
    /// <see cref="Combobulate.FaceEdgeOutsetPx"/> property before each bake, so
    /// this shares one default (<see cref="FaceEdgeOutset.DefaultPx"/>) and one
    /// override path with the SpritePainter renderer. Snapshotted once per bake.
    /// </summary>
    public float EdgeOutsetPx { get; set; } = FaceEdgeOutset.DefaultPx;

    private void Materialise(
        ComputedBake computed,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale, float hostW, float hostH,
        TransformAnimationAxis[] axes,
        DispatcherQueueNS.DispatcherQueue ui,
        int generation)
    {
        try
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path, "debug-artifacts");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "baked-aspect-graph.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] Materialise begin: {computed.Cells.Length} cells\n");
            }
            catch { }
            EnsureBakedMatrixSource();
            var newTrees = new ContainerVisual[computed.Cells.Length];
            var newOpacityExprs = new Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.ScalarNode[computed.Cells.Length];
            var newTreeIndices = new int[computed.Cells.Length][];
            var newSpritesByQuad = CreateSpriteIndex(geometry.Quads.Length);
            ChunkBuild(computed, geometry, bindings, scale, hostW, hostH, axes,
                       newTrees, newOpacityExprs, newTreeIndices, newSpritesByQuad, startIndex: 0, ui, generation);
        }
        catch (Exception ex)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path, "debug-artifacts");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "baked-aspect-graph.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] Materialise FAIL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            }
            catch { }
        }
    }

    private void ChunkBuild(
        ComputedBake computed,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale, float hostW, float hostH,
        TransformAnimationAxis[] axes,
        ContainerVisual[] newTrees,
        Microsoft.Toolkit.Uwp.UI.Animations.ExpressionsFork.ScalarNode[] newOpacityExprs,
        int[][] newTreeIndices,
        List<SpriteVisual>[] newSpritesByQuad,
        int startIndex,
        DispatcherQueueNS.DispatcherQueue ui,
        int generation)
    {
        if (_disposed || generation != _bakeGeneration)
        {
            // Stale or disposed: evict any staging visuals we already
            // parented so they don't become ghost geometry. _trees stays
            // intact (it's the previously-baked, currently-visible set).
            EvictStaging();
            return;
        }

        if (startIndex == 0) _stagingTrees = new System.Collections.Generic.List<ContainerVisual>(computed.Cells.Length);

        int end = Math.Min(startIndex + MaterialiseChunkSize, computed.Cells.Length);
        for (int i = startIndex; i < end; i++)
        {
            var c = computed.Cells[i];
            var tree = _compositor.CreateContainerVisual();
            tree.Opacity = 0; // hidden until swap
            newTreeIndices[i] = BuildTreeContent(tree, geometry, bindings, scale, hostW, hostH, c.Sig.Order, c.Sig.Visibility, newSpritesByQuad);
            // Build (but don't start) the opacity expression — we'll start
            // them all in the swap step so the new tree set lights up
            // atomically and the old one disappears in the same compositor
            // commit. The predicate is the conjunction of signed
            // event-function inequalities baked from this cell's
            // signature; cells are mutually exclusive by construction.
            var predicate = PredicateCompiler.BuildPredicate(GetBakedMatrixReference(), geometry, c.Sig, _bakedCullMarginCos);
            newOpacityExprs[i] = ExpressionFunctions.Conditional(predicate, (ScalarNode)1f, (ScalarNode)0f);
            _parent.Children.InsertAtTop(tree);
            newTrees[i] = tree;
            _stagingTrees!.Add(tree);
        }

        if (end < computed.Cells.Length)
        {
            // More to do — schedule the next chunk.
            ui.TryEnqueue(() => ChunkBuild(computed, geometry, bindings, scale, hostW, hostH, axes,
                                           newTrees, newOpacityExprs, newTreeIndices, newSpritesByQuad, end, ui, generation));
        }
        else
        {
            // Final tick: swap. Stop old animations, drop old trees,
            // then start new opacity animations. The compositor commits
            // these state changes as a single batch on the next vsync.
            if (_disposed || generation != _bakeGeneration) { EvictStaging(); return; }
            DisposeTrees();
            _trees = newTrees;
            _treeVisibleQuadIndices = newTreeIndices;
            _spritesByQuad = newSpritesByQuad;
            _treesGeometry = geometry;
            _stagingTrees = null;
            // Stash signatures + geometry so diagnostics can compare the
            // current live sign vector against what was baked.
            var sigs = new SignatureBake.Signature[computed.Cells.Length];
            for (int k = 0; k < computed.Cells.Length; k++) sigs[k] = computed.Cells[k].Sig;
            _lastBakeSignatures = sigs;
            _lastBakeGeometry = geometry;
            int started = 0;
            string? firstError = null;
            int minLen = int.MaxValue, maxLen = 0;
            long sumLen = 0;
            string? firstFailExpr = null;
            for (int i = 0; i < newTrees.Length; i++)
            {
                // Measure the expression's serialized length so we know whether
                // BAG's per-cell predicate fits Composition's hard cap (the
                // "expression string is too long" ArgumentException). For
                // subdivided high-quad geometry the predicate exceeds the cap
                // and StartAnimation throws for every cell; the model stays
                // blank because every cell's Opacity stays at the initial 0.
                int exprLen = 0;
                string? exprText = null;
                try { exprText = newOpacityExprs[i]?.ToString(); exprLen = exprText?.Length ?? 0; }
                catch { }
                if (exprLen > 0)
                {
                    if (exprLen < minLen) minLen = exprLen;
                    if (exprLen > maxLen) maxLen = exprLen;
                    sumLen += exprLen;
                }
                try
                {
                    newTrees[i].StartAnimation("Opacity", newOpacityExprs[i]);
                    started++;
                }
                catch (Exception ex)
                {
                    if (firstError == null) { firstError = $"cell {i}: {ex.GetType().Name}: {ex.Message} [exprLen={exprLen}]"; firstFailExpr = exprText; }
                }
            }
            int avgLen = newTrees.Length > 0 ? (int)(sumLen / newTrees.Length) : 0;
            _bakeCts?.Dispose();
            _bakeCts = null;

            // Auto-dump diagnostic snapshot for offline triage.
            try
            {
                var dir = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "debug-artifacts");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "baked-aspect-state.txt");
                System.IO.File.WriteAllText(path,
                    $"[{DateTime.Now:HH:mm:ss.fff}] post-bake snapshot — animations started: {started}/{newTrees.Length}; firstError={firstError ?? "none"}\n"
                    + $"  predicate expr lengths: min={minLen} max={maxLen} avg={avgLen}\n"
                    + (firstFailExpr != null ? $"  failing expr (cell 0 head, 500 chars): {firstFailExpr.Substring(0, Math.Min(500, firstFailExpr.Length))}\n" : "")
                    + GetDiagnosticReport(axes, null));
            }
            catch { }
        }
    }

    private static ScalarNode BuildAxisInCellExpression_OBSOLETE(
        TransformAnimationAxis[] axes, float[] lo, float[] hi)
    {
        // Phase 0 replaced this with PredicateCompiler.BuildPredicate.
        // Kept as a no-op stub to avoid churn elsewhere; not called.
        _ = axes; _ = lo; _ = hi;
        return (ScalarNode)0f;
    }

    private int[] BuildTreeContent(
        ContainerVisual tree,
        ObjGeometry geometry,
        ResolvedQuadMaterials bindings,
        float scale, float hostW, float hostH,
        int[] order, bool[] visibility,
        List<SpriteVisual>[] spritesByQuad)
    {
        var origin = new Vector3(hostW / 2f, hostH / 2f, 0);
        var quads = geometry.Quads;
        var sprites = new SpriteVisual[quads.Length];
        float outsetPx = EdgeOutsetPx;   // snapshot once per bake (env-overridable)
        for (int q = 0; q < quads.Length; q++)
        {
            var sprite = _compositor.CreateSpriteVisual();
            var cq = quads[q].WithCanonicalAxisAlignedUv();
            var v0 = cq.V0 * scale + origin;
            var v1 = cq.V1 * scale + origin;
            var v3 = cq.V3 * scale + origin;
            var xAxis = v1 - v0;
            var yAxis = v3 - v0;
            // ROOT CAUSE (root-caused 2026-05-30 on zaca / WinAppSDK 1.8 /
            // Win11 26100 via SurfaceCapProbe tinySprite mode): SpriteVisuals
            // with Size=(1,1) and a TransformMatrix that scales the content
            // up by N× sample their brush at ~1×1 native resolution, then
            // magnify to N×N. When the brush's source surface is larger than
            // ~768 px on either axis the composition sampler returns blank
            // pixels (covers render as flat grey + only the lit highlight).
            // Fix: set the sprite Size to the actual cell dimensions in
            // pixels and keep TransformMatrix as a pure rotation + translate
            // using the cell's unit basis vectors. This preserves the on-
            // screen geometry while letting the compositor allocate the
            // correct number of sampling tiles for the brush.
            var lenX = xAxis.Length();
            var lenY = yAxis.Length();
            var nx = lenX > 0f ? xAxis / lenX : Vector3.UnitX;
            var ny = lenY > 0f ? yAxis / lenY : Vector3.UnitY;

            // ── Anti-seam conservative outset ────────────────────────────────
            // Grow every quad sprite by EdgeOutsetPx on all four sides so
            // neighbouring sub-quads OVERLAP instead of meeting at a shared
            // anti-aliased edge (two abutting AA edges only sum to ~75%
            // coverage, letting the background bleed through as a hairline
            // seam — dominant at oblique cover/spine folds). The UV crop is
            // expanded by the SAME fraction so interior texels stay exactly
            // registered and the overlap samples the neighbour's continuous
            // texels (same-face → invisible) or edge-clamps (cross-face → a
            // ~1px overhang, far better than a background gap).
            //
            // Triangle faces get the SAME treatment (see the triangle branch
            // below): every non-quad die (d4/d8/d12/d20) is a triangle mesh, so
            // without it every triangle edge shows the hairline seam. Their
            // GeometricClip path is grown in lock-step (it is rebuilt from the
            // inflated lenX/lenY a few lines down), so the clip masks to the
            // grown triangle and neighbours overlap exactly like quads do.
            Vector2 euv0 = cq.Uv0, euv1 = cq.Uv1, euv2 = cq.Uv2, euv3 = cq.Uv3;
            if (!cq.IsTriangle && lenX > 0.5f && lenY > 0.5f)
            {
                // Apply the outset to EVERY quad, including the thin (3-6px)
                // face-boundary strips that form the cover/spine and
                // cover/pages FOLDS — the earlier `lenX > outsetPx` guard
                // skipped exactly those strips, so the dominant fold seams
                // never got bridged (and a larger pad skipped even more).
                float fu = outsetPx / lenX;   // UV fraction of one pad along U
                float fv = outsetPx / lenY;   // UV fraction of one pad along V
                var duX = cq.Uv1 - cq.Uv0;        // U-axis UV delta across lenX
                var duY = cq.Uv3 - cq.Uv0;        // V-axis UV delta across lenY
                euv0 = cq.Uv0 - fu * duX - fv * duY;
                euv1 = cq.Uv1 + fu * duX - fv * duY;
                euv3 = cq.Uv3 - fu * duX + fv * duY;
                euv2 = cq.Uv2 + fu * duX + fv * duY;
                // Clamp the expanded crop to the face's [0,1] surface domain so
                // the padded geometry never samples OFF the whole-face surface
                // (which returns transparent → a background fringe worse than
                // the seam). Interior sub-quad pads stay inside [0,1] and are
                // unaffected; boundary-strip pads clamp to the face edge texel
                // (book colour), extending it a couple of px across the fold.
                euv0 = Vector2.Clamp(euv0, Vector2.Zero, Vector2.One);
                euv1 = Vector2.Clamp(euv1, Vector2.Zero, Vector2.One);
                euv2 = Vector2.Clamp(euv2, Vector2.Zero, Vector2.One);
                euv3 = Vector2.Clamp(euv3, Vector2.Zero, Vector2.One);
                v0 = v0 - outsetPx * nx - outsetPx * ny;
                lenX += 2f * outsetPx;
                lenY += 2f * outsetPx;
            }
            else if (cq.IsTriangle && lenX > 0.5f && lenY > 0.5f)
            {
                // ── Triangle anti-seam outset ────────────────────────────────
                // A triangle sprite renders a right triangle inscribed in its
                // rectangular sprite: local corners A(0,0)=V0, B(lenX,0)=V1,
                // C(0,lenY)=V2 (see the clip note below). Push all THREE edges
                // outward by outsetPx in sprite-local space (the same space the
                // quad outset uses), mirroring the quad treatment:
                //   • edge AB (y=0)  → y = -outsetPx
                //   • edge AC (x=0)  → x = -outsetPx
                //   • hypotenuse BC  → outward by outsetPx along its normal
                // The two legs stay axis-aligned, so the inflated triangle is
                // STILL a right triangle with the right angle at A'=(-d,-d).
                // That means the sprite origin simply shifts by -d on both local
                // axes (identical to the quad) and the legs grow to legX/legY,
                // so the shared unit-triangle clip can be reused at the new size.
                float d = outsetPx;
                float invLenX = 1f / lenX;
                float invLenY = 1f / lenY;
                // Local-space perpendicular distance factor of the hypotenuse
                // x/lenX + y/lenY = 1, whose unit normal is (1/lenX, 1/lenY)/g.
                float g = MathF.Sqrt(invLenX * invLenX + invLenY * invLenY);

                // UVs are a 3-point affine over the sprite plane:
                //   uv(px,py) = Uv0 + (px/lenX)·du + (py/lenY)·dv
                // Evaluate it at the three INFLATED corners so the interior
                // texels stay exactly registered and the grown rim extrapolates
                // (same-face → invisible) / edge-clamps (cross-face → ~1px
                // overhang), exactly as the quad path does.
                var du = cq.Uv1 - cq.Uv0;         // U-axis UV delta across lenX
                var dv = cq.Uv2 - cq.Uv0;         // V-axis UV delta across lenY
                float aX = d * invLenX;           // -d along local x, in du units
                float aY = d * invLenY;           // -d along local y, in dv units
                float hB = d * (g + invLenY);     // B''s push past lenX (hypotenuse+leg)
                float hC = d * (g + invLenX);     // C''s push past lenY (hypotenuse+leg)
                euv0 = cq.Uv0 - aX * du - aY * dv;                 // A'(-d,-d)
                euv1 = cq.Uv0 + (1f + hB) * du - aY * dv;         // B'
                euv2 = cq.Uv0 - aX * du + (1f + hC) * dv;         // C'
                // Clamp to the [0,1] surface domain (see the quad note above).
                euv0 = Vector2.Clamp(euv0, Vector2.Zero, Vector2.One);
                euv1 = Vector2.Clamp(euv1, Vector2.Zero, Vector2.One);
                euv2 = Vector2.Clamp(euv2, Vector2.Zero, Vector2.One);

                // Shift the sprite origin to A' and grow the legs. legX/legY are
                // the new-local coordinates of B'/C' (the +d converts the origin
                // shift; the lenX·(g+1/lenY)·d term is the hypotenuse push).
                v0 = v0 - d * nx - d * ny;
                lenX = lenX + d + d * lenX * (g + invLenY);
                lenY = lenY + d + d * lenY * (g + invLenX);
            }
            sprite.Size = new Vector2(lenX > 0f ? lenX : 1f, lenY > 0f ? lenY : 1f);
            // Guard against degenerate cross (sliver triangles where xAxis ∥ yAxis):
            // Vector3.Normalize divides by Length() which underflows to 0 for
            // very thin slivers, producing NaN basis vectors that break the
            // entire TransformMatrix. Fall back to a unit Z so the (already
            // sub-pixel) sprite renders harmlessly off-axis instead of NaN.
            var crossVec = Vector3.Cross(xAxis, yAxis);
            var crossLen = crossVec.Length();
            var zAxis = crossLen > 1e-6f ? crossVec / crossLen : Vector3.UnitZ;
            sprite.TransformMatrix = new Matrix4x4(
                nx.X, nx.Y, nx.Z, 0,
                ny.X, ny.Y, ny.Z, 0,
                zAxis.X, zAxis.Y, zAxis.Z, 0,
                v0.X, v0.Y, v0.Z, 1);
            // Triangle fragments (V3==V2) render their (V0,V1,V2) area as a
            // right-triangle inscribed in the rectangular sprite: xAxis=V1-V0,
            // yAxis=V2-V0 → sprite spans (0..lenX, 0..lenY) in local space and
            // the triangle's three corners land at (0,0), (lenX,0), (0,lenY).
            // A GeometricClip referencing the shared unit-triangle path (scaled
            // by Matrix3x2.CreateScale(lenX,lenY)) masks the other half of the
            // rectangle. Quads keep sprite.Clip == null. See TriangleClipFactory.
            if (cq.IsTriangle && lenX > 0f && lenY > 0f)
            {
                sprite.Clip = TriangleClipFactory.CreateTriangleClip(_compositor, lenX, lenY);
            }
            sprite.Brush = bindings.Bindings[q].Brush;
            // CompositionBrush.TransformMatrix translations are in sprite
            // pixels: rebuild the matrix now that sprite.Size is known so
            // negative-diagonal UV layouts (typical for subdivided
            // triangles) sample within [0, spriteSize] instead of off-
            // surface. Pass the outset-EXPANDED UVs so the padded sprite's
            // interior stays registered. See BrushTransformMath / MaterialResolver.
            MaterialResolver.UpdateBrushTransformForSprite(
                bindings.Bindings[q].Brush, euv0, euv1, euv2, euv3, cq.IsTriangle,
                bindings.Bindings[q].Material, sprite.Size);
            sprite.IsVisible = visibility[q];
            sprites[q] = sprite;
        }
        // Build the painter-order list of *visible* quads and parent them
        // in that order. Return the index list so UpdateBindings can map
        // children back to source quads later without re-deriving.
        var visibleOrder = new System.Collections.Generic.List<int>(order.Length);
        for (int i = 0; i < order.Length; i++)
        {
            int qi = order[i];
            if (!visibility[qi]) continue;
            tree.Children.InsertAtTop(sprites[qi]);
            spritesByQuad[qi].Add(sprites[qi]);
            visibleOrder.Add(qi);
        }
        return visibleOrder.ToArray();
    }

    private static List<SpriteVisual>[] CreateSpriteIndex(int quadCount)
    {
        var index = new List<SpriteVisual>[quadCount];
        for (int i = 0; i < index.Length; i++)
            index[i] = new List<SpriteVisual>();
        return index;
    }

    /// <summary>
    /// Hot brush-swap path. When the host's <c>Materials</c> dependency
    /// property changes but everything else (geometry, scale, host size,
    /// sort algorithm, transform AST, axes) stays the same, the bake's
    /// signature set + cell predicates + sprite tree topology are still
    /// valid — only the per-quad <see cref="CompositionBrush"/> needs to
    /// change. This walks each existing cell's child list in lock-step
    /// with its cached visible-quad-index mapping and reassigns brushes
    /// from <paramref name="newBindings"/> in place. Cost is O(cells ×
    /// visible-faces) brush writes, no allocation, no compositor expression
    /// reinstall.
    ///
    /// <para>Returns true if the swap was applied. Returns false when the
    /// renderer has no current trees (caller should fall back to a full
    /// bake) or when the new bindings' quad count differs from what the
    /// trees were built against (caller should likewise full-rebake; this
    /// only happens if the geometry changed, which already invalidates
    /// the trees).</para>
    /// </summary>
    public bool UpdateBindings(ResolvedQuadMaterials newBindings)
    {
        if (_trees is null || _treeVisibleQuadIndices is null || _treesGeometry is null) return false;
        var quads = _treesGeometry.Quads;
        if (newBindings.Bindings.Length != quads.Length) return false;
        for (int t = 0; t < _trees.Length; t++)
        {
            var tree = _trees[t];
            var visibleQuads = _treeVisibleQuadIndices[t];
            var children = tree.Children;
            int childIdx = 0;
            // VisualCollection enumerates in render (z) order, bottom-to-top.
            // InsertAtTop adds to the top, so the FIRST inserted ends up at
            // the bottom and is enumerated first. We added to visibleQuads
            // in the same loop that called InsertAtTop, so child index k
            // pairs directly with visibleQuads[k].
            int n = visibleQuads.Length;
            foreach (var child in children)
            {
                if (child is SpriteVisual sprite && childIdx < n)
                {
                    int qi = visibleQuads[childIdx];
                    var newBrush = newBindings.Bindings[qi].Brush;
                    if (!ReferenceEquals(sprite.Brush, newBrush))
                        sprite.Brush = newBrush;
                }
                childIdx++;
            }
        }
        return true;
    }

    public bool UpdateBindingsForQuads(ResolvedQuadMaterials newBindings, IReadOnlyList<int> quadIndices)
    {
        if (_trees is null || _spritesByQuad is null || _treesGeometry is null) return false;
        var quads = _treesGeometry.Quads;
        if (newBindings.Bindings.Length != quads.Length) return false;

        for (int i = 0; i < quadIndices.Count; i++)
        {
            int qi = quadIndices[i];
            if ((uint)qi >= (uint)_spritesByQuad.Length) continue;
            var newBrush = newBindings.Bindings[qi].Brush;
            var sprites = _spritesByQuad[qi];
            for (int s = 0; s < sprites.Count; s++)
            {
                var sprite = sprites[s];
                if (!ReferenceEquals(sprite.Brush, newBrush))
                    sprite.Brush = newBrush;
            }
        }

        return true;
    }

    /// <summary>
    /// Diagnostic: produce a human-readable report of the current bake
    /// state — number of cells, current axis live values, which cell
    /// (if any) currently encloses those values, the order/visibility of
    /// every cell whose static <c>Opacity</c> property is non-zero, and
    /// the count of <c>_parent.Children</c> (so leaks past <c>_trees</c>
    /// can be detected).
    /// </summary>
    public string GetDiagnosticReport(
        TransformAnimationAxis[] axes,
        Matrix4x4Node? transformNode)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"BakedAspectGraphRenderer:");
        sb.AppendLine($"  cells (_trees.Length): {_trees?.Length ?? -1}");
        sb.AppendLine($"  parent.Children.Count: {_parent.Children.Count}");
        sb.AppendLine($"  bakeInFlight: {BakeInFlight}");
        sb.AppendLine($"  generation: {_bakeGeneration}");

        // Live axis values via the existing LiveValueProvider (or fall back).
        var liveVals = new float[axes.Length];
        for (int i = 0; i < axes.Length; i++)
        {
            try { liveVals[i] = axes[i].Scalar.Evaluate(); }
            catch (Exception ex) { liveVals[i] = float.NaN;
                sb.AppendLine($"  axis[{i}].Evaluate() THREW: {ex.GetType().Name}: {ex.Message}"); }
            sb.AppendLine($"  axis[{i}] live={liveVals[i]:F2} range=[{axes[i].Min},{axes[i].Min + axes[i].Length}) periodic={axes[i].Periodic} samples={axes[i].Samples}");
        }

        if (transformNode is not null)
        {
            try
            {
                var m = transformNode.Evaluate();
                sb.AppendLine($"  transform.Evaluate() = M11={m.M11:F3} M12={m.M12:F3} M13={m.M13:F3}");
                sb.AppendLine($"                         M21={m.M21:F3} M22={m.M22:F3} M23={m.M23:F3}");
                sb.AppendLine($"                         M31={m.M31:F3} M32={m.M32:F3} M33={m.M33:F3}");
                sb.AppendLine($"                         M41={m.M41:F3} M42={m.M42:F3} M43={m.M43:F3}");
            }
            catch (Exception ex) { sb.AppendLine($"  transform.Evaluate() THREW: {ex.GetType().Name}: {ex.Message}"); }
        }

        if (_trees == null) return sb.ToString();
        // Note on bakedMatrix.live: TryGetMatrix4x4 returns the static
        // seed value, NOT the animated value. The compositor evaluates
        // the animation per frame on its own thread; the property's CPU
        // mirror only updates when the animation is stopped or
        // explicitly snapshotted. So we don't read it here. To verify
        // the live transform we evaluate the typed AST directly above
        // (transform.Evaluate()).
        System.Numerics.Matrix4x4 liveM;
        bool gotLive = false;
        if (_transformNode is not null)
        {
            try { liveM = _transformNode.Evaluate(); gotLive = true; }
            catch { liveM = System.Numerics.Matrix4x4.Identity; }
        }
        else liveM = System.Numerics.Matrix4x4.Identity;

        // Compute the live sign vector against the live matrix and compare
        // against every baked signature. This is the single most useful
        // diagnostic: if the live key matches no baked signature, the bake
        // is incomplete and that's why the visual disappears.
        if (gotLive && _lastBakeSignatures != null && _lastBakeGeometry != null)
        {
            var quads = _lastBakeGeometry.Quads;
            int nq = quads.Length;
            var liveFace = new sbyte[nq];
            var liveZ = new float[nq];
            for (int q = 0; q < nq; q++)
            {
                float nz = EventFunctions.EvalDirectionZ(liveM, quads[q].Normal);
                liveFace[q] = nz > 0f ? (sbyte)+1 : (sbyte)-1;
                liveZ[q] = EventFunctions.EvalPointZ(liveM, quads[q].Centroid);
            }
            var livePair = new sbyte[nq, nq];
            for (int i = 0; i < nq; i++)
            {
                if (liveFace[i] < 0) continue;
                for (int j = i + 1; j < nq; j++)
                {
                    if (liveFace[j] < 0) continue;
                    float diff = liveZ[j] - liveZ[i];
                    sbyte s = diff > 0f ? (sbyte)+1 : (sbyte)-1;
                    livePair[i, j] = s;
                    livePair[j, i] = (sbyte)-s;
                }
            }
            // Build live key matching SignatureBake's BuildKey format.
            var keyB = new System.Text.StringBuilder(nq + nq * (nq - 1) / 2 + 4);
            for (int i = 0; i < nq; i++) keyB.Append(liveFace[i] > 0 ? '+' : '-');
            keyB.Append('|');
            for (int i = 0; i < nq; i++)
                for (int j = i + 1; j < nq; j++)
                    keyB.Append(livePair[i, j] switch { 1 => '+', -1 => '-', _ => '0' });
            string liveKey = keyB.ToString();

            sb.AppendLine($"  liveKey = {liveKey}");
            // The bake encodes pair bits as '0' when the pair is INVARIANT across
            // all sampled poses (PredicateCompiler skips those at runtime — they
            // contribute no constraint). Hamming distance must skip those bake-'0'
            // positions, otherwise it counts hundreds of invariant pairs as
            // "mismatches" and the closest-sig diagnostic becomes meaningless.
            int matchIdx = -1;
            int bestEffective = int.MaxValue;
            int bestIdx = -1;
            int bestFaceMismatch = -1;
            int bestPairMismatch = -1;
            for (int s = 0; s < _lastBakeSignatures.Length; s++)
            {
                var bk = _lastBakeSignatures[s].Key;
                int faceMismatch = 0;
                int pairMismatch = 0;
                int pipe = bk.IndexOf('|');
                if (pipe < 0) continue;
                for (int c = 0; c < pipe && c < liveKey.Length; c++)
                    if (bk[c] != liveKey[c]) faceMismatch++;
                for (int c = pipe + 1; c < bk.Length && c < liveKey.Length; c++)
                {
                    if (bk[c] == '0') continue;
                    if (bk[c] != liveKey[c]) pairMismatch++;
                }
                int effective = faceMismatch + pairMismatch;
                if (faceMismatch == 0 && pairMismatch == 0) { matchIdx = s; break; }
                if (effective < bestEffective)
                {
                    bestEffective = effective;
                    bestIdx = s;
                    bestFaceMismatch = faceMismatch;
                    bestPairMismatch = pairMismatch;
                }
            }
            if (matchIdx >= 0)
            {
                var ms = _lastBakeSignatures[matchIdx];
                sb.AppendLine($"  liveKey MATCHES baked sig[{matchIdx}] order=[{string.Join(",", ms.Order)}] vis=[{string.Join("", System.Linq.Enumerable.Select(ms.Visibility, b => b ? '1' : '0'))}]");
            }
            else
            {
                sb.AppendLine($"  liveKey MATCHES NONE — closest sig[{bestIdx}] faceMismatch={bestFaceMismatch} pairMismatch={bestPairMismatch} (effective={bestEffective}): {_lastBakeSignatures[bestIdx].Key}");
                // Diff the two keys to show which events disagree.
                var ck = _lastBakeSignatures[bestIdx].Key;
                int diffStart = -1;
                for (int c = 0; c < liveKey.Length && c < ck.Length; c++)
                {
                    if (liveKey[c] != ck[c] && ck[c] != '0')
                    {
                        if (diffStart < 0) diffStart = c;
                    }
                }
                if (diffStart >= 0) sb.AppendLine($"  first effective diff at column {diffStart}");
            }
            // Per-sig predicate-fire summary: count sigs whose face bits match
            // AND whose varying-pair bits all match the live pose. That's what
            // the runtime ExpressionAnimation predicate effectively requires.
            int faceOnlyMatch = 0;
            int fullPredicateMatch = 0;
            var matches = new System.Collections.Generic.List<int>();
            for (int s = 0; s < _lastBakeSignatures.Length; s++)
            {
                var bk = _lastBakeSignatures[s].Key;
                int pipe = bk.IndexOf('|');
                if (pipe < 0) continue;
                bool faceMatch = true;
                for (int c = 0; c < pipe && c < liveKey.Length; c++)
                    if (bk[c] != liveKey[c]) { faceMatch = false; break; }
                if (!faceMatch) continue;
                faceOnlyMatch++;
                bool varyingMatch = true;
                for (int c = pipe + 1; c < bk.Length && c < liveKey.Length; c++)
                {
                    if (bk[c] == '0') continue;
                    if (bk[c] != liveKey[c]) { varyingMatch = false; break; }
                }
                if (varyingMatch) { fullPredicateMatch++; matches.Add(s); }
            }
            sb.AppendLine($"  sigs with matching face bits: {faceOnlyMatch}; with matching face+varying-pair bits: {fullPredicateMatch} (these should be active)");
            if (matches.Count > 0)
                sb.AppendLine($"  expected-active sig indices: {string.Join(",", matches)}");
        }
        sb.AppendLine($"  --- _parent.Children scalar Opacity values ---");
        sb.AppendLine($"  NOTE: ContainerVisual.Opacity returns the LAST SET value (CPU mirror),");
        sb.AppendLine($"        NOT the live GPU-animated value. When an ExpressionAnimation drives");
        sb.AppendLine($"        Opacity, this mirror stays at the initial value (0) even when the");
        sb.AppendLine($"        compositor is rendering the cell at Opacity=1. So 'total Opacity > 0' here");
        sb.AppendLine($"        will almost always read 0 in BAG mode — that does NOT mean nothing renders.");
        sb.AppendLine($"        Use 'expected-active sig indices' above plus a visual capture to confirm.");
        int idx = 0;
        int nonZero = 0;
        foreach (var child in _parent.Children)
        {
            if (child is ContainerVisual cv)
            {
                float op = cv.Opacity;
                if (op > 0.0001f)
                {
                    nonZero++;
                    sb.AppendLine($"    child[{idx}] CPU-mirror Opacity={op:F4} children={cv.Children.Count}");
                }
            }
            idx++;
        }
        sb.AppendLine($"  total children with CPU-mirror Opacity > 0: {nonZero} (see NOTE above)");
        return sb.ToString();
    }

    public void Dispose()
    {
        _disposed = true;
        _bakeCts?.Cancel();
        _bakeCts?.Dispose();
        _bakeCts = null;
        // Bump generation so any deferred chunk-build callbacks scheduled
        // before Dispose see a stale value and bail before parenting more
        // visuals into _parent.
        _bakeGeneration++;
        EvictStaging();
        DisposeTrees();
        if (_bakedMatrixAnim != null)
        {
            try { _bakedMatrixProps?.StopAnimation(BakedMatrixPropertyName); } catch { }
            _bakedMatrixAnim.Dispose();
            _bakedMatrixAnim = null;
        }
        _bakedMatrixProps?.Dispose();
        _bakedMatrixProps = null;
    }

    /// <summary>
    /// Lazily creates the per-renderer CompositionPropertySet that holds
    /// the live transform matrix as a single Matrix4x4 property "M",
    /// driven by a single ExpressionAnimation evaluating the user's
    /// transform AST. All per-cell predicates reference this property,
    /// so the user-AST size never multiplies per cell.
    /// </summary>
    private void EnsureBakedMatrixSource()
    {
        if (_transformNode is null) return;
        try
        {
            if (_bakedMatrixProps == null)
            {
                _bakedMatrixProps = _compositor.CreatePropertySet();
                _bakedMatrixProps.InsertMatrix4x4(BakedMatrixPropertyName, System.Numerics.Matrix4x4.Identity);
            }
            try { _bakedMatrixProps.StopAnimation(BakedMatrixPropertyName); } catch { }
            _bakedMatrixAnim = null;
            // The user's _transformNode is also referenced by Combobulate's
            // _root.TransformMatrix animation. The ExpressionsFork caches
            // a single ExpressionAnimation instance on the typed node and
            // reuses it across StartAnimation calls. A single
            // CompositionAnimation can only target one object/property,
            // so when the legacy code path latches the cached animation
            // onto _root.TransformMatrix, our subsequent
            // _bakedMatrixProps.StartAnimation receives the SAME instance
            // and ends up no-op-ed (or steals it back from _root). Either
            // way the live matrix here stays at identity.
            //
            // Force a fresh animation by clearing the cached one via
            // reflection (the property is internal). This forces the
            // toolkit's StartAnimation extension to allocate a new
            // ExpressionAnimation for our property.
            var animProp = typeof(ExpressionNode).GetProperty(
                "ExpressionAnimation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            animProp?.SetValue(_transformNode, null);
            _bakedMatrixProps.StartAnimation(BakedMatrixPropertyName, _transformNode!);
        }
        catch (Exception ex)
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path, "debug-artifacts");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "baked-aspect-graph.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] EnsureBakedMatrixSource FAIL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            }
            catch { }
            throw;
        }
    }

    /// <summary>Returns the typed Matrix4x4Node referencing <c>props.M</c>.</summary>
    private Matrix4x4Node GetBakedMatrixReference()
    {
        if (_bakedMatrixProps == null)
            throw new InvalidOperationException("Baked matrix property set not initialised.");
        return _bakedMatrixProps.GetReference().GetMatrix4x4Property(BakedMatrixPropertyName);
    }

    private void EvictStaging()
    {
        if (_stagingTrees == null) return;
        foreach (var t in _stagingTrees)
        {
            try
            {
                if (t.Parent != null) _parent.Children.Remove(t);
                t.Dispose();
            }
            catch { }
        }
        _stagingTrees = null;
    }

    private void DisposeTrees()
    {
        if (_trees == null) return;
        foreach (var t in _trees)
        {
            t.StopAnimation("Opacity");
            t.Children.RemoveAll();
            if (t.Parent != null) _parent.Children.Remove(t);
            t.Dispose();
        }
        _trees = null;
        _treeVisibleQuadIndices = null;
        _spritesByQuad = null;
        _treesGeometry = null;
    }
}
