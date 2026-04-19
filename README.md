# Combobulate

A XAML controls library for UWP and WinUI 3 / Windows App SDK.

Currently exposes a single (intentionally blank) `Combobulate` control as a starting point.

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
```

## Usage

```xml
<Page xmlns:c="using:Combobulate">
    <c:Combobulate />
</Page>
```

## Releasing

Push a tag of the form `vX.Y.Z` to trigger the GitHub Actions workflow that
builds both targets, packs `Combobulate.nuspec`, publishes to NuGet.org, and
creates a GitHub Release.
