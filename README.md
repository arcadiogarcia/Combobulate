# Combobulate

A XAML controls library for UWP and WinUI 3 / Windows App SDK that renders
3D `.obj` files made of quads using the [Windows Composition][composition]
visual layer — no D3D, no SwapChainPanel, no shaders.

> **combobulate** *(humorous)* — to compose (oneself); to compose, organize,
> design, or arrange; to reverse the effect of discombobulation.
> *([Wiktionary](https://en.wiktionary.org/wiki/combobulate))*

Each quad becomes a `SpriteVisual` placed in 3D by a perspective
`TransformMatrix`. Back-face culling and a polygon sort keep the draw
order right without a depth buffer, and a fully analytical *baked aspect
graph* renderer can sustain GPU-driven animation with **zero CPU work
per frame**.

[composition]: https://learn.microsoft.com/windows/uwp/composition/visual-layer

## Packages

NuGet: `Combobulate`

## Targets

- UWP — `uap10.0.19041`
- WinUI 3 / Windows App SDK — `net10.0-windows10.0.19041.0`

## Repo Layout

```
src/
  Combobulate/                 UWP class library (canonical source)
  Combobulate.WinAppSdk/       WinUI 3 head — links sources from Combobulate
  Combobulate.Sample.Uwp/      UWP sample app
  Combobulate.Sample.WinUI3/   WinUI 3 sample app
samples/
  book.obj                     Example quad-based model
```

## Getting Started

1. Add the `Combobulate` NuGet package to your UWP or WinUI 3 app.
2. Place a `Combobulate` control in your XAML:

   ```xml
   <Page xmlns:c="using:Combobulate">
       <c:Combobulate x:Name="Viewer"
                      Source="ms-appx:///Assets/book.obj"
                      ModelScale="200"
                      RotationX="-30"
                      RotationY="40"
                      RotationZ="0" />
   </Page>
   ```

   `Source` accepts an `ms-appx:///` URI, an absolute file path, or an
   `ObjCache` key — geometry, materials (via sibling `mtllib`), and texture
   surfaces are all loaded and cached process-wide.

3. Or parse an `.obj` yourself and assign the resulting model directly:

   ```csharp
   using Combobulate.Parsing;

   var text = await FileIO.ReadTextAsync(
       await StorageFile.GetFileFromPathAsync(@"C:\path\to\book.obj"));
   var result = ObjParser.Parse(text);
   Viewer.Model = result.Model;        // partial model on parse error
   foreach (var err in result.Errors)  // optional: inspect parse diagnostics
       System.Diagnostics.Debug.WriteLine(err);
   ```

That's it — the control rebuilds its visual tree whenever any of its
inputs change.

### Highlights

- **Materials.** Auto-loaded from `mtllib` or supplied via `ObjMaterialPack`,
  with live texture updates from `SoftwareBitmap` or streams.
- **Three sort algorithms.** `Bsp` (default, exact, O(n)), `Newell`
  (split-on-cycle, deformable-friendly), and `Topological` (legacy, fast).
- **Composition-driven rotation.** `SetExternalRotation(ExpressionAnimation)`
  binds rotation directly to the compositor; pair with `EnableAutoRefresh`
  to keep cull and sort in lock-step with the compositor clock.
- **Baked aspect graph.** Pre-computes every painter-order breakpoint
  along an animation axis and emits one `ContainerVisual` per cell, each
  gated by an opacity expression on a live scalar. Smooth GPU spin
  without per-frame CPU cost. See
  [docs/rendering-pipeline.md](docs/rendering-pipeline.md).

For everything beyond "drop a control, point `Source` at a file" see the
**[usage guide](docs/usage.md)** and the
**[rendering pipeline](docs/rendering-pipeline.md)** doc.

### Supported `.obj` subset

- `v x y z [w]` — vertex positions
- `vt u v [w]` — texture coordinates
- `vn x y z` — vertex normals (parsed; not used for shading today)
- `f a b c d` — **quad** faces; `a/ta/na` style references accepted
- `usemtl <name>`, `mtllib <file>` — material binding and library load
- `o`, `g`, `s` — preserved; `l`, `p`, `vp`, curves/surfaces are ignored

Faces must be **quads** (triangles and n-gons are reported as parse errors).
Wind each face so that `(V1 - V0) × (V3 - V0)` points outward — that's the
direction the renderer treats as front-facing for back-face culling.

See [`samples/book.obj`](samples/book.obj) for a worked example.

### Control properties at a glance

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `Source` | `string?` | `null` | Path, `ms-appx:///` URI, or `ObjCache` key |
| `Model` | `ObjModel?` | `null` | Direct model assignment |
| `Materials` | `ObjMaterialPack?` | `null` | Overrides any auto-loaded `mtllib` |
| `MaterialMode` | `MaterialMode` | `Auto` | `Auto` / `UseDiffuse` / `UseFallback` |
| `ModelScale` | `double` | `100` | Pixels per model unit |
| `EnablePerspective` | `bool` | `true` | Apply perspective `M34` to the root |
| `PerspectiveDistance` | `double` | `0` | Focal distance in px (0 = host width) |
| `RotationX/Y/Z` | `double` | `0` | Euler angles in degrees |
| `SortAlgorithm` | `SortAlgorithm` | `Bsp` | `Bsp` / `Newell` / `Topological` |
| `CullMarginDegrees` | `double` | `0` | Widen back-face cull cone for GPU lag |
| `RenderingMode` | `RenderingMode` | `SpritePainter` | `SpritePainter` / `BakedAspectGraph` |

Full descriptions and examples are in the [usage guide](docs/usage.md).

## Releasing

Push a tag of the form `vX.Y.Z` to trigger the GitHub Actions workflow that
builds both targets, packs `Combobulate.nuspec`, publishes to NuGet.org, and
creates a GitHub Release.
