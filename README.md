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
2. Drop a `.obj` file into your app (e.g. `Assets/book.obj`, marked as
   `Content` so it ships with the package).
3. Place a `Combobulate` control in your XAML and point `Source` at the file:

   ```xml
   <Page xmlns:c="using:Combobulate">
       <c:Combobulate Source="ms-appx:///Assets/book.obj"
                      ModelScale="200"
                      RotationX="-30"
                      RotationY="40" />
   </Page>
   ```

That's it — no code-behind required. The control parses the `.obj` on demand,
caches it process-wide, and auto-loads any `mtllib` referenced by the file
(textures resolved relative to the `.obj`).

For everything else — loading from arbitrary paths or streams, supplying
custom textures per face, live-updating pixels, the full property reference,
the supported `.obj` / `.mtl` subset, and the caching model — see
[**docs/usage.md**](docs/usage.md).

## Releasing

Push a tag of the form `vX.Y.Z` to trigger the GitHub Actions workflow that
builds both targets, packs `Combobulate.nuspec`, publishes to NuGet.org, and
creates a GitHub Release.
