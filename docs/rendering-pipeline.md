# Rendering pipeline

This document describes how the [`Combobulate`](../src/Combobulate/Combobulate.cs)
control turns an [`ObjModel`](../src/Combobulate/Parsing/ObjModel.cs) into a set of
WinUI / UWP **Composition `SpriteVisual`** quads on screen, including which
algorithms run, when they run, and why.

The pipeline has four stages:

1. [Model load and parsing](#1-model-load-and-parsing)
2. [Rebuild trigger and per-quad transform construction](#2-rebuild-trigger-and-per-quad-transform-construction)
3. [Back-face culling](#3-back-face-culling)
4. [Topology-aware painter's sort](#4-topology-aware-painters-sort)

A final section covers the [root-level perspective transform](#5-root-level-perspective-transform)
that the per-quad sprites are drawn through.

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

> Changes to `EnablePerspective` only update the **root** transform, not the
> per-quad ones — see [§5](#5-root-level-perspective-transform).

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
   colored by [`ColorForIndex(i)`](../src/Combobulate/Combobulate.cs) (a
   golden-angle hue spacing for visual distinction across many quads).

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

## 4. Topology-aware painter's sort

The Composition API has **no z-buffer for `SpriteVisual` siblings** — children
are painted in their list order, and later siblings draw on top of earlier
ones. To render a 3D model correctly we must explicitly sort the surviving
quads so that the farthest is added first.

A naive painter's algorithm sorts by view-space centroid Z. **This is wrong**
for adjacent perpendicular faces with very different centroids: e.g. a book
whose covers sit at `z = ±0.1` but whose page-top edge has its centroid at
`y = +0.7`. Under any non-trivial pitch, the page-top's view-Z is dominated by
`0.7 · sin(pitch)`, making it appear "closer" than the front cover and
incorrectly painting on top of it. The screenshot
[`tap_preview_20260419_215003_143.png`](../docs/) (now obsolete) showed exactly
this artefact.

`Combobulate` therefore uses a **Newell-style topological partial order**
combined with a view-Z tiebreaker, implemented in
[`TopologicalPainterSort`](../src/Combobulate/Combobulate.cs).

### 4.1 Pairwise "behind" test

For every pair `(a, b)` of surviving (front-facing) quads, we ask: *must `a`
be drawn before `b`?* Yes, if every vertex of `a` lies on or behind the plane
defined by `b`'s centroid and outward normal, **and at least one vertex is
strictly behind**. In code, with `eps = 1e-4`:

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

Two design choices are worth flagging:

- **Why `<= +eps` instead of `< -eps` for the "all behind" half?** Adjacent
  perpendicular faces share an edge, and the shared-edge vertices have signed
  distance ≈ 0 with respect to *each other's* planes. A strict negative test
  would reject these pairs, leaving them unordered and falling back to the
  buggy view-Z tiebreaker. Allowing on-plane vertices keeps the constraint
  while still requiring at least one strictly behind to break ties.
- **Why use the model-space test, rotation-invariant?** Each quad that
  reached this stage already passed back-face culling, so its outward model
  normal points generally toward the camera in view space. The plane's
  negative side is therefore the "far side from the camera" regardless of
  rotation, and we can do the test once in model space rather than re-projecting
  every vertex.

### 4.2 Topological sort (Kahn's algorithm)

The pairwise tests build a directed graph where an edge `a → b` means *a
must precede b*. We then run **Kahn's algorithm** (BFS-style topological sort):

1. Compute `inDegree[b]` for each quad.
2. Repeatedly:
   a. Pick the unemitted quad with `inDegree == 0` and the **smallest
      view-Z** ("farthest first" tiebreaker among unconstrained candidates).
   b. Emit it, decrement the in-degree of every quad it points to.
3. If at some step no quad has `inDegree == 0`, the graph contains a cycle —
   typically caused by **interpenetrating geometry**. Fall back to plain
   view-Z order for the remaining quads.

Cost: O(N²) for the pairwise edge build plus O(N²) for Kahn's tiebreaker
selection, where N is the number of front-facing quads. For the small models
this control is designed for (cubes, books, simple prisms — typically <100
quads after culling) this is negligible.

### 4.3 Painting

The ordered list is appended via `_root.Children.InsertAtTop(sprite)` in
sequence. Because each `InsertAtTop` puts the new visual on top of all
existing siblings, iterating from farthest to nearest is the correct painter's
order.

## 5. Root-level perspective transform

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
  `M34 = -1/d` where `d = host width`. With the homogeneous w-divide this
  produces the standard pinhole foreshortening: world-space points with
  larger `Z` (closer to the viewer at +Z) get magnified, while points with
  smaller `Z` shrink.
- `fromOrigin` translates back so the centre lands at the host centre again.

The same `rotation` matrix is used for back-face culling and the view-Z
tiebreaker in the sort, so visibility decisions are consistent with what's
actually drawn.

> Toggling `EnablePerspective` calls `UpdateRootTransform()` only; it does
> **not** trigger a `Rebuild()`. Toggling rotation calls both because culling
> and ordering depend on it.

## Limitations

- **Convex, non-interpenetrating models give correct results.** For arbitrary
  meshes, the topological partial order may admit cycles, in which case the
  view-Z fallback is used and ordering is best-effort.
- **Quads only.** Triangulating before render would be straightforward but
  is not implemented.
- **No texturing or per-vertex lighting.** Each quad gets a flat colour from
  `ColorForIndex(i)`. Composition `SpriteVisual` can hold a brush of any
  kind, so adding image brushes per quad is a small change.
- **Per-quad transforms are affine.** Non-parallelogram quads are
  approximated; for fully general planar quads a homography would be needed.
- **Rotation must change on the UI thread.** See the next section.

## Composition-thread animations

Composition animations (`ExpressionAnimation`, `KeyFrameAnimation`,
`InputAnimation`, etc.) run on the **compositor thread** and update visual
properties between UI-thread frames. They are the standard way to get smooth,
60+ fps animation without UI-thread jank.

This control's design has a critical assumption that breaks under such
animations: **back-face culling and the painter's sort both run inside
`Rebuild()` using a snapshot of `RotationX/Y/Z` taken on the UI thread**. If
rotation is being animated by the compositor (e.g. via an `ExpressionAnimation`
driving `_root.RotationAngleInDegrees`), the UI thread is never told the value
changed, `Rebuild()` is never re-invoked, and the displayed image will exhibit
the same depth artefacts that the topological sort exists to prevent — visibly
wrong occlusion for as long as the animation runs.

### The supported workaround: `SetExternalRotation`

The control exposes a composition-native rotation API that addresses this
directly:

```csharp
public void SetExternalRotation(ExpressionAnimation rotationDegrees);
public void ClearExternalRotation();
public void RebuildForExternalRotation(Vector3 rotationDegrees);
```

`SetExternalRotation` binds an `ExpressionAnimation` (whose result is a
`Vector3` of degrees `(pitch, yaw, roll)`) to the composition root's
`TransformMatrix` via the engine. The visible 3D rotation then updates on
every composition frame without UI-thread involvement, exactly as the user
expects.

The painter sort and back-face cull *still* use the rotation snapshot from
the last UI-thread `Rebuild`. When the animated value drifts far enough that
the cached order is wrong, snapshot the current value on the UI thread and
call `RebuildForExternalRotation(currentDegrees)` to re-cull and re-sort
against it. The control deliberately does not read the animated value
itself — it cannot cross the thread boundary.

See [Composition-driven rotation](usage.md#composition-driven-rotation) for
a fuller walkthrough including property-set and time-driven examples.

### Other mitigation strategies

If `SetExternalRotation` doesn't fit your scenario:

### A. Tick the rebuild from `CompositionTarget.Rendering`

Subscribe to `CompositionTarget.Rendering` and re-run `Rebuild()` every
frame while an animation is in flight. The UI-thread cost is one full
re-creation of every `SpriteVisual` per frame — acceptable for the small
quad counts this control targets (cubes, books, simple prisms) but a hard
ceiling on model complexity. To make this affordable, `Rebuild()` would need
to be split into:

- A persistent quad cache keyed by `ObjQuad` index (sprite + brush + basis
  transform are all rotation-invariant).
- A per-frame "re-cull and re-sort" pass that toggles `IsVisible` and reorders
  siblings without disposing anything.

This is the smallest change and the most likely first step.

### B. Make culling rotation-independent and tolerate sort lag

If you can accept double-sided rendering, emit *both* faces of every quad at
load time (or have the model do so explicitly, as `samples/book.obj` already
does for its covers and spine). That removes the rotation dependency from
culling. The painter's sort still depends on rotation, but you can either:

- Run the sort on the UI thread at a low rate (e.g. 30 Hz via a
  `DispatcherTimer`) and accept brief mis-orderings, or
- Pick a **rotation-invariant ordering** that's correct for your specific
  model topology — e.g. for a closed convex hull, any consistent CCW outward
  ordering is correct because culling alone resolves visibility.

This works well for animations that don't change the topological ordering
mid-flight (small wobbles, hover effects, page-flip-style rotations confined
to one axis) but degrades for free-tumbling.

### C. Move to a true 3D pipeline

For arbitrary continuous animation with correct depth at every frame, the
right primitive is a real z-buffer. Options on Windows:

- **`SceneVisual`** (Microsoft.UI.Composition.Scenes / Windows.UI.Composition.Scenes):
  proper 3D meshes with depth testing, runs on the compositor thread, and
  composition animations *do* drive it correctly without UI-thread involvement.
  Closest semantic match but the API is a substantial step up from
  `SpriteVisual`.
- **Win2D / Direct2D on a `CanvasSwapChainPanel`** with manual depth handling.
- **Direct3D via `SwapChainPanel`** for full control.

Once a model is large enough to need composition-thread animation it is
probably also large enough to justify one of these.

> The current implementation is intentionally `SpriteVisual`-based because
> that gives the cheapest path from "OBJ on disk" to "thing on screen" using
> only the XAML composition surface, with no graphics interop. The
> composition-animation limitation is the price of that simplicity.
