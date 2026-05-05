# Rendering pipeline

This document describes how the [`Combobulate`](../src/Combobulate/Combobulate.cs)
control turns an [`ObjModel`](../src/Combobulate/Parsing/ObjModel.cs) into a set of
WinUI / UWP **Composition `SpriteVisual`** quads on screen, including which
algorithms run, when they run, and why.

The pipeline has five stages:

1. [Model load and parsing](#1-model-load-and-parsing)
2. [Rebuild trigger and per-quad transform construction](#2-rebuild-trigger-and-per-quad-transform-construction)
3. [Back-face culling](#3-back-face-culling)
4. [Polygon ordering](#4-polygon-ordering)
5. [Composition realisation](#5-composition-realisation) (sprite painter or baked aspect graph)

A final section covers the [root-level perspective transform](#6-root-level-perspective-transform)
that the per-quad sprites are drawn through, and the
[composition-thread animation](#composition-thread-animations) story.

---

## 1. Model load and parsing

`Combobulate.Model` is an [`ObjModel`](../src/Combobulate/Parsing/ObjModel.cs)
populated by [`ObjParser`](../src/Combobulate/Parsing/ObjParser.cs). The parser
accepts standard Wavefront `.obj` syntax with the following constraints:

- **Faces must be quads.** Each `f` line is parsed into an
  [`ObjQuad`](../src/Combobulate/Parsing/ObjQuad.cs) with four
  [`ObjVertex`](../src/Combobulate/Parsing/ObjVertex.cs) entries (`V0`..`V3`).
  Triangles and n-gons are rejected at parse time — see
  [`ObjParserFaceTests`](../tests/Combobulate.Tests/ObjParserFaceTests.cs).
- **Winding order is meaningful.** A quad's outward normal is taken to be
  `normalize((V1 − V0) × (V3 − V0))`. Faces must be wound counter-clockwise as
  viewed from the outside of the surface; otherwise back-face culling will hide
  them.
- **Doubled faces are allowed.** A real-world surface that should be visible
  from both sides (e.g. a sheet, an open book cover) can be expressed as two
  faces sharing the same vertices but with opposite winding.

The parser populates `model.Positions` (a list of `Vector3`) and `model.Quads`
(a list of `ObjQuad`). Each `ObjVertex.PositionIndex` is a 0-based index into
`Positions`.

## 2. Rebuild trigger and per-quad transform construction

`Combobulate.Rebuild()` is the single function responsible for turning the
current `ObjModel` into a set of `SpriteVisual` children of a
`ContainerVisual` root. It is invoked whenever the rendered output may need to
change:

| Trigger                              | Reason for full rebuild                                     |
|--------------------------------------|-------------------------------------------------------------|
| `Model` dependency property changes  | Quads, positions, and counts may be entirely different.    |
| `ModelScale` changes                 | Per-quad transforms include the scale factor.              |
| `RotationX/Y/Z` changes              | Back-face culling and depth ordering both depend on rotation. |
| Host element `SizeChanged`           | Per-quad transforms include the host-centre origin.        |
| `Loaded` / template applied          | Initial attachment of the visual tree.                     |

> Changes to `EnablePerspective` or `PerspectiveDistance` only update the
> **root** transform, not the per-quad ones — see
> [§6](#6-root-level-perspective-transform). Changes to `SortAlgorithm`
> or `RenderingMode` re-run the cull and sort but reuse the per-quad
> transforms.

For each quad, `Rebuild()`:

1. Resolves four world-space corners `p0..p3` from the model's `Positions`.
2. Subtracts the model centroid (`ComputeCenter`) so the model is rendered
   centred at the host's middle, then multiplies by `ModelScale` and adds the
   host-centre origin to produce DIP-space corners `v0`, `v1`, `v3`.
3. Forms basis vectors `xAxis = v1 − v0`, `yAxis = v3 − v0` and a non-normalised
   `zAxis = xAxis × yAxis`. Degenerate (zero-area) quads are skipped.
4. Builds a 4×4 transform that maps the unit-square local point `(x, y, z, 1)`
   into world space using `(xAxis, yAxis, zAxisHat, v0)` as the column basis.
   This is exact for parallelogram quads and approximates non-parallelograms
   by reconstructing `V2` as `V0 + (V1 − V0) + (V3 − V0)`.
5. Creates a `SpriteVisual` of size `(1, 1)` with this transform and a brush
   resolved from the active material pack — a `CompositionSurfaceBrush`
   for textured materials, a solid colour brush for diffuse-only materials,
   or a per-quad golden-angle palette colour as the final fallback (see
   [Materials and textures](usage.md#materials-and-textures)).

## 3. Back-face culling

After computing each quad's basis but **before** appending its sprite, the
control rotates the model-space outward normal by the current rotation matrix
and discards the quad if `viewNormal.Z <= 0`.

Why `Z > 0` means front-facing:

- The root applies a perspective transform with `M34 = -1/d` (see
  [§5](#5-root-level-perspective-transform)), which places the **viewer at +Z**.
- A face whose outward normal points toward +Z after rotation therefore points
  toward the camera and should be drawn.
- `viewNormal.Z == 0` means the face is edge-on and contributes no visible
  pixels, so it is skipped.

This is a per-quad O(1) test; total cost is O(N) where N is the quad count.

## 4. Polygon ordering

The Composition API has **no z-buffer for `SpriteVisual` siblings** —
children are painted in their list order, and later siblings draw on top of
earlier ones. To render a 3D model correctly we must explicitly sort the
surviving (front-facing) quads back to front.

A naive painter's algorithm sorts by view-space centroid Z. **This is wrong**
for adjacent perpendicular faces with very different centroids: e.g. a book
whose covers sit at `z = ±0.1` but whose page-top edge has its centroid at
`y = +0.7`. Under any non-trivial pitch, the page-top's view-Z is dominated
by `0.7 · sin(pitch)`, making it appear "closer" than the front cover and
incorrectly painting on top of it. The control therefore offers three
ordering strategies, exposed via the `SortAlgorithm` dependency property
and the [`IFaceSorter`](../src/Combobulate/Sorting/IFaceSorter.cs) interface.

### 4.1 `Bsp` — default

[`BspSorter`](../src/Combobulate/Sorting/BspSorter.cs) builds a Binary Space
Partitioning tree once per `ObjGeometry`, off the triangulated mesh. The
build splits any polygon that straddles a chosen partition plane, so the
resulting tree is **cycle-free for any view direction**. Per-frame work is a
single front-to-back tree walk against the current rotation — O(n) where n
is the visible-face count, with no per-frame allocations once the tree
exists.

Because the tree is cached on `ObjGeometry`, every control sharing the
same geometry shares the same BSP. This is the recommended algorithm for
static rigid meshes.

### 4.2 `Newell` — split-on-cycle

[`NewellSorter`](../src/Combobulate/Sorting/NewellSorter.cs) implements the
classic five-test Newell cascade: for each candidate pair, run cheap
box-overlap and plane-side tests first, fall through to the more expensive
edge tests, and only when every test still says "could overlap" do we split
one polygon by the other's plane and re-sort the fragments. Splits are
emitted lazily as cycles are exposed, so the typical cost stays close to
O(n).

Unlike `Bsp`, `Newell` does no precomputation against the geometry, so it
handles deforming or animated meshes naturally. It is a good choice when
you expect the per-frame topology to change.

### 4.3 `Topological` — legacy

The original algorithm. For every pair `(a, b)` of front-facing quads, it
asks: *must `a` be drawn before `b`?* The answer is yes if every vertex of
`a` lies on or behind the plane defined by `b`'s centroid and outward
normal, with at least one vertex strictly behind. With `eps = 1e-4`:

```csharp
var d0 = Vector3.Dot(qa.V0 - qb.Centroid, qb.Normal);
var d1 = Vector3.Dot(qa.V1 - qb.Centroid, qb.Normal);
var d2 = Vector3.Dot(qa.V2 - qb.Centroid, qb.Normal);
var d3 = Vector3.Dot(qa.V3 - qb.Centroid, qb.Normal);
if (d0 <= eps && d1 <= eps && d2 <= eps && d3 <= eps &&
    (d0 < -eps || d1 < -eps || d2 < -eps || d3 < -eps))
{
    edge[a, b] = true;   // a must precede b
}
```

The pairwise tests build a directed graph that's then resolved with Kahn's
algorithm; cycles fall back to plain view-Z order for the remaining quads.
Cost is O(n²) for the edge build plus O(n²) for Kahn's selection. Like
`Bsp`, the model-space edge graph is rotation-invariant and is cached on
`ObjGeometry.Predecessors` (lazily, thread-safe), so per-frame cost is just
the Kahn loop restricted to the visible subset.

This algorithm is kept for backward compatibility and as a debugging
baseline; new code should prefer `Bsp` or `Newell`.

### 4.4 The `IFaceSorter` contract

All three sorters implement
[`IFaceSorter`](../src/Combobulate/Sorting/IFaceSorter.cs):

```csharp
int Sort(Matrix4x4 rotation, int[] orderBuf, bool[] visBuf,
         float cameraDistance, float cullMarginCos);
```

They take ownership of back-face culling as well as ordering: `visBuf` is
filled with the per-quad visibility mask (widened by `cullMarginCos` so
faces inside the cull cone but within `CullMarginDegrees` of the boundary
stay drawn), `orderBuf` is filled with the back-to-front list of visible
indices, and the return value is its length. The buffers are pre-sized to
the geometry's quad count and reused across frames, so steady-state
ordering is allocation-free.

## 5. Composition realisation

The sort produces an order list and a visibility mask; the
`RenderingMode` dependency property selects how that gets turned into
compositor state. Both modes share the cull, sort, and material
resolution above; they differ only in when sort decisions are made.

### 5.1 `SpritePainter` — default

One pooled `SpriteVisual` per quad, parented to a single `ContainerVisual`
root. Each frame:

- Cull writes new `IsVisible` flags only on the quads whose visibility
  actually changed.
- The order list is compared against the previous frame's; if they
  match, the sibling reorder is skipped entirely.
- Otherwise we walk the new list back-to-front and call
  `_root.Children.InsertAtTop(sprite)` to reorder.

Steady-state per `Rebuild` is therefore one transform-only cull pass plus a
sibling reorder only on frames where the painter order actually changed.
Sprite identities are pooled per geometry, so there are no per-frame
composition allocations.

### 5.2 `BakedAspectGraph`

The ultimate refinement. Conceptually:

1. Identify every scalar event whose sign determines the painter
   signature — a per-face front-facing event `(M·n).z` and, for each
   ordered front-facing pair, a depth event
   `(M·cⱼ).z - (M·cᵢ).z`. The signs of these events together uniquely
   determine the visible-face set and their order. See
   [`EventFunctions.cs`](../src/Combobulate/Rendering/EventFunctions.cs)
   and [`SignatureBake.cs`](../src/Combobulate/Rendering/SignatureBake.cs).
2. Sweep the chosen animation axis (`TransformAnimationAxis`) over its
   period. For a 1-D yaw axis, take 720 coarse samples (0.5°) to detect
   sign flips, then bisect each flip to within `1e-3` to find the exact
   breakpoint. The result is a list of cells, each a half-open interval
   `[lo, hi)` over which the painter signature is constant.
   See [`AspectGraphBake.cs`](../src/Combobulate/Rendering/AspectGraphBake.cs).
3. For each cell, build a `ContainerVisual` containing only the
   front-facing sprites at that signature, parented in the cell's painter
   order. The cell's `Opacity` is driven by an `ExpressionAnimation`
   that's `1` when the live yaw scalar lies inside `[lo, hi)` (with the
   axis's `Periodic` flag wrapping the test) and `0` otherwise.
4. Parent every cell under a single root container. Exactly one cell is
   opaque at any moment, and the compositor swaps between them on the
   GPU at the exact frame where the live scalar crosses a breakpoint.

Per-frame CPU cost is **zero** — there is no `Rebuild`, no cull, no
resort. The cost is paid up front in the bake (which runs on a background
thread, with the previous tree remaining visible until the new one is
ready) and in memory: O(cells × visible-faces) sprites, where the cell
count for a typical model under yaw spin is in the low tens.

[`BakedAspectGraphRenderer`](../src/Combobulate/Rendering/BakedAspectGraphRenderer.cs)
also implements a hot brush-swap path: when only the `Materials` DP
changes, the bake is reused and only the per-quad `CompositionBrush` is
reassigned in place — no teardown, no expression reinstall.

## 6. Root-level perspective transform

The container visual that holds the per-quad sprites carries its own transform
that combines (in this order, for column vectors):

```
toOrigin → rotation → perspective → fromOrigin
```

- `toOrigin` translates the host's centre to (0, 0, 0).
- `rotation` is `Matrix4x4.CreateFromYawPitchRoll(yaw=Y, pitch=X, roll=Z)`
  in radians. This applies roll, then pitch, then yaw (R · P · Y for row
  vectors).
- `perspective`, applied only when `EnablePerspective == true`, has
  `M34 = -1/d` where `d` is `PerspectiveDistance` if positive, otherwise
  the host width. With the homogeneous w-divide this produces the
  standard pinhole foreshortening: world-space points with larger `Z`
  (closer to the viewer at +Z) get magnified, while points with smaller
  `Z` shrink. Setting `PerspectiveDistance` explicitly decouples the
  projection's strength from the control's size.
- `fromOrigin` translates back so the centre lands at the host centre again.

The same `rotation` matrix is used for back-face culling and the view-Z
tiebreaker in the sort, so visibility decisions are consistent with what's
actually drawn.

> Toggling `EnablePerspective` calls `UpdateRootTransform()` only; it does
> **not** trigger a `Rebuild()`. Toggling rotation calls both because culling
> and ordering depend on it.

## Limitations

- **`Topological` may admit cycles on arbitrary meshes.** `Bsp` (the
  default) and `Newell` are exact for any rigid mesh; `Topological` is
  retained for backward compatibility and falls back to view-Z order
  when its graph contains a cycle.
- **Quads only.** Triangulating before render would be straightforward but
  is not implemented.
- **Per-vertex lighting is not applied.** Each quad is rendered with a
  flat colour or texture from its material; vertex normals are parsed
  from the OBJ but not used for shading.
- **Per-quad transforms are affine.** Non-parallelogram quads are
  approximated; for fully general planar quads a homography would be
  needed.
- **`SpritePainter` lags GPU rotation by one frame.** See
  [Composition-thread animations](#composition-thread-animations) for the
  three mitigation paths the library provides.

## Composition-thread animations

Composition animations (`ExpressionAnimation`, `KeyFrameAnimation`,
`InputAnimation`, etc.) run on the **compositor thread** and update visual
properties between UI-thread frames. They are the standard way to get
smooth, 60+ fps animation without UI-thread jank.

The original `SpritePainter` design takes a UI-thread snapshot of
`RotationX/Y/Z` inside `Rebuild()` and uses it for both back-face culling
and the painter sort. If a compositor animation is driving the visual
rotation, the UI thread is never told, the snapshot is never refreshed,
and the displayed image will exhibit the very depth artefacts the sort
exists to prevent. The library now offers three ways to address this,
ordered from cheapest to most thorough:

### A. `SetExternalRotation` + `EnableAutoRefresh`

Bind an `ExpressionAnimation` to the visual rotation, and on every
composition frame re-sample the same scalar source on the UI thread to
refresh the cull and sort:

```csharp
combobulate.SetExternalRotation(rotationExpression);
combobulate.EnableAutoRefresh((TimeSpan compositorTime) => SampleRotation(compositorTime));
```

The `Func<TimeSpan, Vector3>` overload receives the same compositor clock
that keyframe animations evaluate against, so the CPU-computed rotation
and the GPU-drawn rotation stay in lock-step — even across compositor
stalls. Steady-state cost is one cull pass plus a sibling reorder only
on frames where the painter order changes. See
[the usage guide](usage.md#auto-refresh) for the full pattern.

For scenarios where the rotation changes faster than the UI thread can
absorb (e.g. samples streamed in from another thread),
`RequestRebuildForExternalRotation(Vector3)` records the latest value and
coalesces multiple updates into a single rebuild on the UI thread.

### B. Widen the cull cone with `CullMarginDegrees`

Even with auto-refresh, a CPU-supplied rotation can lag the GPU-drawn
rotation by up to one compositor frame. At very high spin rates this
shows up as a face that the cull dropped (because at the snapshot yaw it
was just past the back-face boundary) but the compositor still draws
(because the live yaw is one frame ahead).

Setting `CullMarginDegrees` to a small positive value (the sample uses
`18°` while spinning) widens the cull cone in both directions, so faces
within that band of the boundary stay visible. The chosen sort algorithm
gets the same widened set, so its boundary decisions are stable across
the entire uncertainty window.

### C. Switch to `RenderingMode.BakedAspectGraph`

For continuous compositor-driven motion the most efficient mode is the
fully baked aspect graph (§5.2). The bake enumerates every painter-order
breakpoint along the animation axis up front, so per-frame CPU cost
drops to zero and the swap between cells is GPU-atomic. This is the
recommended choice for any "play-forever" animation where the animation
axis is known up front.

For scenarios that need a full 3D z-buffered pipeline (multi-axis
animation, complex meshes, arbitrary occlusion), the right primitive is
`SceneVisual` directly, or Win2D / Direct2D on a `CanvasSwapChainPanel`,
or `SwapChainPanel` driving Direct3D. The current sprite-based
implementation is intentionally `SpriteVisual`-based because that gives
the cheapest path from "OBJ on disk" to "thing on screen" using only the
XAML composition surface, with no graphics interop — and the three
mitigation paths above cover almost every case before that step is
needed.
