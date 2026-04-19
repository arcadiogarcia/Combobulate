# Combobulate

A XAML controls library for UWP and WinUI 3 / Windows App SDK that renders
3D `.obj` files made of quads using the [Windows Composition][composition]
visual layer — no D3D, no SwapChainPanel, no shaders.

> **combobulate** *(humorous)* — to compose (oneself); to compose, organize,
> design, or arrange; to reverse the effect of discombobulation.
> *([Wiktionary](https://en.wiktionary.org/wiki/combobulate))*

Each quad becomes a `SpriteVisual` placed in 3D by a perspective
`TransformMatrix`, with back-face culling and a topology-aware painter's
algorithm to get the draw order right without a depth buffer.

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
                      ModelScale="200"
                      RotationX="-30"
                      RotationY="40"
                      RotationZ="0" />
   </Page>
   ```

3. Parse an `.obj` file and assign the resulting model:

   ```csharp
   using Combobulate.Parsing;

   var text = await FileIO.ReadTextAsync(
       await StorageFile.GetFileFromPathAsync(@"C:\path\to\book.obj"));
   var result = ObjParser.Parse(text);
   Viewer.Model = result.Model;        // partial model on parse error
   foreach (var err in result.Errors)  // optional: inspect parse diagnostics
       System.Diagnostics.Debug.WriteLine(err);
   ```

That's it — the control rebuilds its visual tree whenever `Model`,
`ModelScale`, or any rotation property changes.

### Supported `.obj` subset

- `v x y z` — vertex positions
- `f a b c d` — **quad** faces (4 indices, 1-based, positive or negative)
- Vertex normals/UVs in `f` entries (`a/b/c`) are accepted but ignored

Faces must be **quads** (triangles and n-gons are reported as parse errors).
Wind each face so that `(V1 - V0) × (V3 - V0)` points outward — that's the
direction the renderer treats as front-facing for back-face culling.

See [`samples/book.obj`](samples/book.obj) for a worked example.

### Control properties

| Property | Type | Notes |
| --- | --- | --- |
| `Model` | `ObjModel?` | Set from `ObjParser.Parse(...).Model` |
| `ModelScale` | `double` | Pixels per model unit (default `100`) |
| `EnablePerspective` | `bool` | Adds a perspective `M34` to the host transform |
| `RotationX` / `RotationY` / `RotationZ` | `double` | Euler angles in degrees |

## Releasing

Push a tag of the form `vX.Y.Z` to trigger the GitHub Actions workflow that
builds both targets, packs `Combobulate.nuspec`, publishes to NuGet.org, and
creates a GitHub Release.
