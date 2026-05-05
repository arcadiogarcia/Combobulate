# Combobulate — usage guide

This document covers everything beyond the minimal "drop a control, point
`Source` at a file" path shown in the [README](../README.md). Read top to
bottom or jump to the section you need.

- [Loading models](#loading-models)
  - [From an asset URI](#from-an-asset-uri)
  - [From an absolute file path](#from-an-absolute-file-path)
  - [From a parsed `ObjModel`](#from-a-parsed-objmodel)
  - [From a stream or arbitrary text](#from-a-stream-or-arbitrary-text)
  - [Sharing one model across many controls](#sharing-one-model-across-many-controls)
- [Materials and textures](#materials-and-textures)
  - [Auto-loading via `mtllib`](#auto-loading-via-mtllib)
  - [Custom material packs](#custom-material-packs)
  - [Texture sources](#texture-sources)
  - [Live-updating pixels](#live-updating-pixels)
  - [`MaterialMode`](#materialmode)
- [Control properties](#control-properties)
- [Sort algorithms](#sort-algorithms)
- [Rendering modes](#rendering-modes)
- [Composition-driven rotation](#composition-driven-rotation)
- [Animating with the baked aspect graph](#animating-with-the-baked-aspect-graph)
- [Supported `.obj` subset](#supported-obj-subset)
- [Supported `.mtl` subset](#supported-mtl-subset)
- [The cache, in one minute](#the-cache-in-one-minute)
- [Diagnostics and parse errors](#diagnostics-and-parse-errors)

## Loading models

The control accepts geometry in three forms, in priority order: `Source` →
`Model` → (nothing). Pick whichever fits how your app already manages files.

### From an asset URI

Mark the file as `Content` (`CopyToOutputDirectory=PreserveNewest`) and
reference it via `ms-appx:///`:

```xml
<c:Combobulate Source="ms-appx:///Assets/book.obj" />
```

The first control to reference that URI parses + caches the model; every
subsequent control reuses the cached `ObjGeometry`.

### From an absolute file path

```xml
<c:Combobulate x:Name="Viewer" />
```

```csharp
Viewer.Source = @"C:\models\book.obj";
```

`Source` records the file's directory so any `mtllib` inside the OBJ resolves
relative to it.

### From a parsed `ObjModel`

If you already have the file contents in memory, parse it yourself and
assign `Model` directly:

```csharp
using Combobulate.Parsing;

var result = ObjParser.Parse(text);
Viewer.Model = result.Model;          // partial model on parse error
foreach (var err in result.Errors)
    Debug.WriteLine(err);
```

Setting `Model` directly bypasses the keyed source cache (no `mtllib`
auto-load), but the per-instance geometry cache still kicks in — reusing the
same `ObjModel` reference across many controls only does the per-quad
model-space prep once.

### From a stream or arbitrary text

Read the bytes however you like, hand the text to `ObjParser.Parse`, then
assign `Model`:

```csharp
using var stream = await someFile.OpenStreamForReadAsync();
using var reader = new StreamReader(stream);
var result = ObjParser.Parse(await reader.ReadToEndAsync());
Viewer.Model = result.Model;
```

If you need `mtllib` resolution against a base directory you control, use
`ObjCache.GetOrLoadMtlForModel(model, baseDirectory)` and assign the
returned pack to `Materials` (see below).

### Sharing one model across many controls

Two patterns work:

1. **By `Source`** — every control with the same string value reuses the
   cached `ObjGeometry` automatically.
2. **By cache key** — register a model under a logical name and reference
   it by key:

   ```csharp
   ObjCache.Register("book", parsedModel);
   ```

   ```xml
   <c:Combobulate Source="book" />
   ```

   The lookup checks registered keys before treating the value as a path.

## Materials and textures

A face's appearance is determined by the active *material pack* — a
dictionary keyed by the OBJ's `usemtl` names. The control resolves a pack at
rebuild time in this order:

1. `Materials` dependency property (your custom pack), if non-null.
2. A pack previously registered against `Source` via
   `ObjCache.RegisterMaterials(sourceKey, ...)`.
3. Materials auto-loaded from any `mtllib` declarations in the OBJ.
4. The per-quad fallback colour baked into the geometry from the OBJ palette.

### Auto-loading via `mtllib`

If your OBJ contains `mtllib book.mtl` and you set `Source` to a file path
or `ms-appx:///` URI, the control finds `book.mtl` next to the OBJ, parses
it, resolves `map_Kd` paths relative to the same directory, and applies the
resulting pack — no code required.

### Custom material packs

Build a pack programmatically when you want full control over which faces
get which texture or colour:

```csharp
using Combobulate.Caching;
using Windows.UI;

var pack = new ObjMaterialPackBuilder()
    .Add("cover", new ObjMaterial
    {
        Name           = "cover",
        DiffuseTexture = ObjTextureSource.FromUri(new Uri("ms-appx:///Assets/cover.png")),
        UvScale        = new Vector2(1, 1),
        UvOffset       = new Vector2(0, 0),
        ClampUv        = true,
    })
    .Add("pages", new ObjMaterial
    {
        Name           = "pages",
        DiffuseTexture = ObjTextureSource.FromFile(@"C:\textures\pages.png"),
    })
    .Add("spine", new ObjMaterial
    {
        Name         = "spine",
        DiffuseColor = Color.FromArgb(255, 80, 30, 20),
    })
    .Build();

Viewer.Source    = "ms-appx:///Assets/book.obj";
Viewer.Materials = pack;     // overrides any mtllib in the OBJ
```

Pack keys are matched against the OBJ's `usemtl` names case-sensitively
(ordinal). Faces whose `usemtl` is missing from the pack fall through to the
pack's `Fallback` (set via `ObjMaterialPackBuilder.Fallback`) or, failing
that, the per-quad palette colour.

If you don't author the OBJ yourself, inspect the geometry to discover the
material names:

```csharp
var geometry = ObjCache.GetOrLoadGeometry(path);
foreach (var quad in geometry.Quads)
    Debug.WriteLine(quad.MaterialName);
```

### Texture sources

`ObjTextureSource` is the texture identity. Sources are cached process-wide
by `CacheKey`, so the same image is decoded once and the resulting surface
is reused across every control that brushes with it.

| Factory | Backed by | Cache key | Updateable |
| --- | --- | --- | --- |
| `FromUri(Uri)` | `LoadedImageSurface.StartLoadFromUri` | `uri:<absolute>` | no |
| `FromFile(string)` | absolute path → `LoadedImageSurface` | `uri:<absolute>` | no |
| `FromStream(factory, key)` | `LoadedImageSurface.StartLoadFromStream` | `stream:<key>` | yes |
| `FromBitmap(SoftwareBitmap)` | PNG-encoded bytes | `bmp:<n>` | yes |
| `FromSurface(ICompositionSurface)` | your own surface (Win2D, etc.) | `ext:<n>` | no |

### Live-updating pixels

Sources made with `FromBitmap` or `FromStream` accept
`Update(SoftwareBitmap)`. The control re-points the underlying
`CompositionSurfaceBrush.Surface` without rebuilding geometry, so animation
is flicker-free:

```csharp
var paintTex = ObjTextureSource.FromBitmap(MakeBitmap(0));   // 128×128 BGRA8 premultiplied
var pack     = new ObjMaterialPackBuilder()
                  .Add("front", new ObjMaterial { DiffuseTexture = paintTex })
                  .Build();
Viewer.Materials = pack;

// Later, on a tick:
paintTex.Update(MakeBitmap(tick++));
```

`FromUri`, `FromFile`, and `FromSurface` are read-only.

### `MaterialMode`

Controls how the active pack interacts with OBJ material data:

| Value | Behaviour |
| --- | --- |
| `Auto` (default) | Use textures + diffuse colours from the pack. |
| `UseDiffuse` | Strip textures from the pack; only the diffuse colour applies. |
| `UseFallback` | Ignore the pack entirely; use the per-quad palette colour from the OBJ. |

`UseFallback` is the "show me the OBJ as if no materials existed" mode.

## Control properties

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `Source` | `string?` | `null` | File path, `ms-appx:///` URI, or registered `ObjCache` key. |
| `Model` | `ObjModel?` | `null` | Direct model assignment. Bypasses keyed cache and `mtllib` auto-load. |
| `Materials` | `ObjMaterialPack?` | `null` | Overrides any auto-resolved material pack. |
| `MaterialMode` | `MaterialMode` | `Auto` | See [Materials and textures](#materials-and-textures). |
| `ModelScale` | `double` | `100` | Pixels per model unit. |
| `EnablePerspective` | `bool` | `true` | Applies a perspective `M34` to the host transform. |
| `PerspectiveDistance` | `double` | `0` | Focal distance in pixels for the perspective `M34 = -1/d`. When `0` the control falls back to the host width (the v0.x default), so the projection scales with the control. Set to a positive value to decouple foreshortening from control size. |
| `RotationX` / `RotationY` / `RotationZ` | `double` | `0` | Euler angles in degrees. |
| `SortAlgorithm` | `SortAlgorithm` | `Bsp` | Polygon back-to-front order. See [Sort algorithms](#sort-algorithms). |
| `CullMarginDegrees` | `double` | `0` | Widens the back-face cull cone by N° in each direction. Use during GPU-driven spin to absorb up to one frame of CPU/GPU rotation lag without flipping a face's `IsVisible`. The sample uses `18` while spinning. |
| `RenderingMode` | `RenderingMode` | `SpritePainter` | Per-frame composition strategy. See [Rendering modes](#rendering-modes). |

The control rebuilds its visual tree whenever any of these change.

## Sort algorithms

The Composition visual layer has no z-buffer for `SpriteVisual` siblings —
later children draw on top of earlier ones — so the control has to compute
an explicit back-to-front order each frame. `SortAlgorithm` selects which
one is used:

| Value | Cost | Correctness | When to use |
| --- | --- | --- | --- |
| `Bsp` *(default)* | O(n) per frame after a one-time O(n²) build | Exact for any rigid mesh | The recommended choice for static geometry. The BSP tree is built once per geometry off the cached `ObjGeometry`; queries become a single front-to-back walk. |
| `Newell` | ~O(n) typical, O(n²) worst | Exact, splits polygons on real cycles | Best for animated / deforming geometry where rebuilding a BSP every frame would be wasteful. Splits a face only when the cycle test actually fires. |
| `Topological` | O(n²) | Falls back to view-Z on cycles | The original algorithm. Kept for backward compatibility and as a debugging baseline; new code should prefer `Bsp` or `Newell`. |

All three algorithms share the same back-face cull and emit the same kind of
order list, so swapping them at runtime is safe.

```xml
<c:Combobulate Source="ms-appx:///Assets/book.obj"
               SortAlgorithm="Bsp" />
```

## Rendering modes

`RenderingMode` controls *how* the chosen sort order is realised on the
compositor. Both modes use the same `SortAlgorithm`, the same cull, and
the same materials; they differ only in when sort decisions are made.

| Value | Sprite count | CPU per frame | Best for |
| --- | --- | --- | --- |
| `SpritePainter` *(default)* | 1 per quad | One restricted sort + sibling reorder | Static or low-rate rotation. Simplest. |
| `BakedAspectGraph` | One `ContainerVisual` per sort cell | **Zero.** All cells exist up front; opacity expressions test the live animation scalar | Continuous spin, scroll-linked tumbles, anywhere the rotation is fully on the compositor and you want the UI thread out of the loop. See [next section](#animating-with-the-baked-aspect-graph). |

The mode can change at runtime; the control rebakes as needed.

## Composition-driven rotation

The `RotationX/Y/Z` dependency properties drive the same path internally, but
each set forces a re-marshal back to the UI thread and a full visual
rebuild. For animations that need to run independently of the UI thread —
keyframe spins, expression-driven hover effects, scroll-linked tumbles — the
controls also expose a composition-native API:

```csharp
public void SetExternalRotation(ExpressionAnimation rotationDegrees);
public void ClearExternalRotation();
public void RebuildForExternalRotation(Vector3 rotationDegrees);
public void RequestRebuildForExternalRotation(Vector3 rotationDegrees);
public void EnableAutoRefresh(Func<Vector3> rotationSampler);
public void EnableAutoRefresh(Func<TimeSpan, Vector3> rotationSampler);
public void DisableAutoRefresh();
```

`rotationDegrees` is any `ExpressionAnimation` whose result is a `Vector3`
where `X = pitch`, `Y = yaw`, `Z = roll` in degrees. The control wires it
into a composition expression bound to its root `TransformMatrix`; subsequent
changes to anything that expression references (property sets, other visuals,
time, …) flow entirely on the composition thread.

### Driving from a property set

The simplest pattern — push values from any thread without re-marshalling
to the UI thread:

```csharp
var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
var props = compositor.CreatePropertySet();
props.InsertVector3("R", Vector3.Zero);

var rot = compositor.CreateExpressionAnimation("p.R");
rot.SetReferenceParameter("p", props);

combobulate.SetExternalRotation(rot);

// Later, on any thread:
props.InsertVector3("R", new Vector3(pitch, yaw, roll));
```

### Driving from another visual / time / scroll

```csharp
// Spin yaw at 60°/sec, no UI thread ever again:
var spin = compositor.CreateExpressionAnimation(
    "Vector3(0, this.Target.GetGlobalTime() * 60, 0)");
combobulate.SetExternalRotation(spin);

// Or rotate based on a ScrollViewer's offset:
var scrollProps = ElementCompositionPreview
    .GetScrollViewerManipulationPropertySet(scrollViewer);
var rot = compositor.CreateExpressionAnimation(
    "Vector3(scroll.Translation.Y * 0.5, scroll.Translation.X * 0.5, 0)");
rot.SetReferenceParameter("scroll", scrollProps);
combobulate.SetExternalRotation(rot);
```

### Refreshing painter sort / mesh

While `SetExternalRotation` is active, the visible 3D rotation is correct on
every composition frame, but back-face culling and the painter sort are
*frozen* at whatever the last UI-thread `Rebuild` produced. For models
where that matters, snapshot the animated value on the UI thread and call
`RebuildForExternalRotation`:

```csharp
// E.g. on a periodic timer, or when the animation reaches a known keyframe:
var current = ReadCurrentRotationOnUiThread();   // app-supplied
combobulate.RebuildForExternalRotation(current);
```

The control cannot read the animated value itself — it lives on the
composition thread — so the caller must supply it. This is the manual
mitigation referenced in the [composition-thread animations
section](rendering-pipeline.md#composition-thread-animations) of the
rendering pipeline doc.

### Coalescing high-frequency rebuild requests

If rotation samples arrive faster than the UI thread can absorb (e.g.
streamed from a non-UI thread or fired from many overlapping events),
`RequestRebuildForExternalRotation` records the latest value and schedules
a single rebuild on the UI thread; further calls before that rebuild runs
update the pending value but do not enqueue additional work. It is
thread-safe and non-blocking.

```csharp
// Safe to call from any thread, at any rate:
combobulate.RequestRebuildForExternalRotation(latestRotation);
```

### Auto-refresh

When the rotation expression is driven by time or another always-changing
input, `EnableAutoRefresh` does the per-frame snapshot for you. The sampler
runs on the UI thread once per composition frame and its return value is
fed straight into `RebuildForExternalRotation`:

```csharp
// Caller still owns the property set the expression reads from; we just
// re-sample the same value on the UI thread for cull / paint-order:
combobulate.EnableAutoRefresh(() => latestRotationOnUiThread);

// later:
combobulate.DisableAutoRefresh();
```

**Prefer the `Func<TimeSpan, Vector3>` overload** whenever rotation derives
from a `ScalarKeyFrameAnimation` or any other compositor-clock animation.
The sampler then receives `RenderingEventArgs.RenderingTime` — the same
clock the compositor uses to evaluate keyframes — and the CPU-side rotation
you compute from it stays in lock-step with what the GPU is drawing.
Using wall-clock time (`DateTime.UtcNow`, `Stopwatch`) drifts whenever the
compositor stalls and the drift compounds over long-running spins:

```csharp
combobulate.EnableAutoRefresh((TimeSpan compositorTime) =>
{
    var pitch = (float)PitchSlider.Value;
    var yaw   = (float)YawSlider.Value;
    var roll  = (float)RollSlider.Value;

    // Match the GPU keyframe animation's wrap math exactly: take elapsed
    // time mod the spin period BEFORE scaling to degrees, so float ULP
    // doesn't grow with how long the spin has been running.
    double elapsedMs = compositorTime.TotalMilliseconds - _spinStartMs;
    double periodMs  = SpinSecondsPerTurn * 1000.0;
    double phaseMs   = elapsedMs - Math.Floor(elapsedMs / periodMs) * periodMs;
    yaw += (float)(phaseMs / periodMs * 360.0);

    return new Vector3(pitch, yaw, roll);
});
```

Steady-state cost per tick (`Combobulate` only) is one back-face cull pass
plus, on frames where the painter order changes, the sibling reorder. Per-quad
sprites are pooled across rebuilds so there are no per-tick composition
allocations.

`ClearExternalRotation` detaches the expression and reverts to the
DP-driven path.

## Animating with the baked aspect graph

For continuous compositor-driven motion the most efficient option is
`RenderingMode = BakedAspectGraph`. Once baked, the painter order is
*entirely* expressed as a set of opacity-gated `ContainerVisual` cells —
the UI thread is out of the loop and per-frame CPU cost is zero.

The baked renderer needs to know **the full transform expression** that
the compositor will use to position the model, plus **which scalars in
that expression are live** (animated). You hand both to
`SetTransformAnimation`:

```csharp
public void SetTransformAnimation(
    Matrix4x4Node transformNode,
    TransformAnimationAxis[] axes);

public void SetTransformAnimation(
    Matrix4x4Node transformNode,
    ScalarNode    primaryAxis,
    float         primaryAxisPeriod = 360f);

public void SetTransformAnimation(Matrix4x4Node transformNode);
```

- `transformNode` — a typed `Matrix4x4Node` expression describing the
  model''s full transform: orientation, any per-axis basis composition,
  etc. Combobulate compiles this with `ToExpressionString()` and binds
  it to the visual root''s `TransformMatrix` for the duration of the
  bake, so the GPU paints exactly what the bake analysed.
- `axes` — one `TransformAnimationAxis` per *live* scalar input the
  bake should sweep. Each axis carries the scalar leaf (`ScalarNode`),
  the value range (`Min`, `Length`), a `Periodic` flag, and a sample
  count for grid bakes. `TransformAnimationAxis.FullCircleDeg(scalar)`
  is the convenience for a 0°—360° periodic angle.
- The single-argument overload auto-discovers every animatable scalar
  leaf in `transformNode` and treats each as a full-circle periodic
  axis. The single-axis overload accepts one explicit `ScalarNode`
  with a custom period.

The bake then sweeps the configured axes, evaluates the transform at
every probe point, runs the painter sort on the resulting matrix, and
emits one `ContainerVisual` per stable cell. Each cell''s `Opacity`
expression compares the live scalars against the cell bounds, so the
compositor itself decides which cell is visible on every frame.

### A complete spin example

```csharp
var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;

// 1. A property set with the live yaw scalar driving the spin.
var props = compositor.CreatePropertySet();
props.InsertScalar("Pitch", 0f);
props.InsertScalar("Yaw",   0f);
props.InsertScalar("Roll",  0f);

// 2. Build a typed expression for the model rotation. The leaves below
//    are ScalarNode references into `props`; the bake will sweep them.
var pitch = ExpressionFunctions.GetReferenceProperty<ScalarNode>(props, "Pitch") * (MathF.PI / 180f);
var yaw   = ExpressionFunctions.GetReferenceProperty<ScalarNode>(props, "Yaw")   * (MathF.PI / 180f);
var roll  = ExpressionFunctions.GetReferenceProperty<ScalarNode>(props, "Roll")  * (MathF.PI / 180f);
Matrix4x4Node transform = ExpressionFunctions.CreateRotationMatrixFromYawPitchRoll(yaw, pitch, roll);

// 3. Hand the transform + the periodic yaw axis to Combobulate.
combobulate.RenderingMode = RenderingMode.BakedAspectGraph;
combobulate.SetTransformAnimation(
    transform,
    TransformAnimationAxis.FullCircleDeg(yaw));

// 4. Drive the live yaw scalar from a keyframe animation.
var kfa = compositor.CreateScalarKeyFrameAnimation();
kfa.InsertExpressionKeyFrame(0.0f, "0");
kfa.InsertExpressionKeyFrame(1.0f, "360");
kfa.Duration          = TimeSpan.FromSeconds(6);
kfa.IterationBehavior = AnimationIterationBehavior.Forever;
props.StartAnimation("Yaw", kfa);
```

A few things worth knowing:

- The bake runs on a background thread. While it''s in progress the
  previous tree (or, on first attach, no tree) remains visible; you can
  start the spin immediately and the new cells fade in atomically when
  ready.
- Changing `Materials` while baked **does not** trigger a rebake — only
  the per-quad brushes are swapped in place.
- Changing the value of a *secondary* scalar that contributes to the
  transform (e.g. moving the pitch slider while yaw is the swept axis)
  shifts every cell boundary, and Combobulate detects this and re-bakes
  automatically. To force a rebake explicitly — e.g. after mutating
  geometry — call `ForceRebakeAspectGraph()`.
- When the renderer is in `BakedAspectGraph` you don''t need
  `EnableAutoRefresh` or `RebuildForExternalRotation` for cull/sort —
  the cell expressions handle both.
- `CullMarginDegrees` is still respected; the bake widens its breakpoint
  search by the same margin so cells overlap at the boundaries and the
  swap is glitch-free under sub-frame GPU/CPU lag.

## Supported `.obj` subset

- `v x y z [w]` — vertex positions
- `vt u v [w]` — texture coordinates (used when materials supply textures)
- `vn x y z` — vertex normals (parsed, but shading uses the per-face
  geometric normal computed from winding)
- `f a b c d` — **quad** faces (4 indices, 1-based, positive or negative)
- `f a/ta b/tb c/tc d/td` and `f a/ta/na …` — quads with texture and/or
  normal indices
- `usemtl <name>` — selects the active material for subsequent faces
- `mtllib <file> [<file>…]` — relative paths resolved against the OBJ
- `o`, `g`, `s` — recorded on each face but otherwise informational
- `l`, `p`, `vp`, curves, surfaces, and unknown keywords are ignored
  silently

Faces must be **quads** — triangles and n-gons are reported as parse errors.
Wind each face so `(V1 - V0) × (V3 - V0)` points outward; that's the
direction the renderer treats as front-facing for back-face culling.

See [`samples/book.obj`](../samples/book.obj) for a worked example.

## Supported `.mtl` subset

- `newmtl <name>` — start a material definition
- `Kd r g b` — diffuse colour (linear RGB 0..1)
- `Ka`, `Ks`, `Ke`, `Ns`, `Ni`, `d`, `Tr`, `illum` — parsed and stored, but
  only diffuse is used for rendering today
- `map_Kd <path>` — diffuse texture; relative paths resolve against the
  `.mtl` file's directory
- `map_Ka`, `map_Ks`, `map_d`, `map_Bump` / `bump` / `map_Kn` — parsed but
  not yet rendered
- Texture options on a `map_*` line: `-s u v`, `-o u v`, `-clamp on|off`,
  `-mm base gain`; unknown options are preserved in `ExtraOptions`
- Unknown keywords are preserved in `ExtraKeywords` rather than failing the
  parse — the parser is lenient by design

## The cache, in one minute

`Combobulate.Caching.ObjCache` is process-wide and split into a few keyed
stores:

- **Source models** — `Source` values map to parsed `ObjModel` + per-instance
  `ObjGeometry`. Files are invalidated by timestamp + length.
- **Material packs** — registered explicitly via
  `RegisterMaterials(sourceKey, pack)` or auto-loaded via
  `GetOrLoadMtlForModel(model, baseDirectory)`. Same timestamp/length
  invalidation as models.
- **Texture surfaces** — keyed by `ObjTextureSource.CacheKey`; one
  `ICompositionSurface` per identity, shared across every brush.
- **Brush bindings** — per-(geometry, pack) `CompositionSurfaceBrush`
  instances are kept in `ConditionalWeakTable`s so they're collected when
  the geometry or pack is.

Useful APIs:

```csharp
ObjCache.Register(key, model);                         // logical alias
ObjCache.GetOrAdd(key, () => parseModelOnce());        // lazy register
ObjCache.RegisterMaterials(sourceKey, pack);
ObjCache.TryGetMaterials(sourceKey, out var pack);
ObjCache.GetOrLoadMtlFile(absolutePath, baseDir);
ObjCache.GetOrLoadMtlForModel(model, baseDir, onError);
ObjCache.InvalidateMaterials(sourceKey);
ObjCache.InvalidateMtlFile(absolutePath);
```

## Diagnostics and parse errors

`ObjParser.Parse` and `MtlParser.Parse` return a result type containing both
the (possibly partial) model and a list of structured errors. Errors never
throw — they accumulate so you can show them in your UI or log them:

```csharp
var result = ObjParser.Parse(text);
foreach (var err in result.Errors)
    Debug.WriteLine($"{err.Line}: {err.Kind} — {err.Message}");
Viewer.Model = result.Model;     // still useful even with errors
```

For `mtllib` auto-load errors, pass an `onError` callback to
`ObjCache.GetOrLoadMtlForModel`.
