using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Combobulate.Parsing;

namespace Combobulate.Caching;

/// <summary>
/// Process-wide cache of parsed OBJ models and their derived <see cref="ObjGeometry"/>.
///
/// <para>
/// Two complementary caches:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       A <em>keyed</em> cache (file paths and arbitrary string keys) so the same OBJ
///       text isn't parsed twice when many controls request the same source.
///     </description>
///   </item>
///   <item>
///     <description>
///       A <em>per-instance</em> weak cache keyed on the <see cref="ObjModel"/> reference.
///       Any caller that hands the same <c>ObjModel</c> to multiple controls gets a single
///       shared <see cref="ObjGeometry"/> for free, with no manual key management — and
///       the entry is collected once the model itself is unreachable.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// File-path entries record the file's <c>LastWriteTimeUtc</c> at load time. A subsequent
/// <see cref="GetOrLoadFile(string)"/> that finds a newer timestamp transparently re-parses.
/// All operations are thread-safe.
/// </para>
/// </summary>
public static class ObjCache
{
    private sealed class FileEntry
    {
        public ObjGeometry Geometry = null!;
        public DateTime LastWriteTimeUtc;
        public long Length;
    }

    private static readonly ConcurrentDictionary<string, ObjGeometry> _keyed =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, FileEntry> _files =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class MtlFileEntry
    {
        public ObjMaterialPack Pack = null!;
        public DateTime LastWriteTimeUtc;
        public long Length;
    }

    private static readonly ConcurrentDictionary<string, ObjMaterialPack> _materialKeys =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, MtlFileEntry> _materialFiles =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConditionalWeakTable<ObjModel, ObjGeometry> _byModel = new();

    /// <summary>
    /// Returns the cached <see cref="ObjGeometry"/> for <paramref name="model"/>,
    /// building it on first use. Subsequent calls with the same instance return the
    /// same object. The entry is dropped automatically when the model is collected.
    /// </summary>
    public static ObjGeometry ForModel(ObjModel model)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        return _byModel.GetValue(model, ObjGeometry.Build);
    }

    /// <summary>
    /// Returns geometry for a keyed app-provided model, parsing/building only on first call.
    /// Use this when an OBJ comes from in-memory text, an embedded resource, or any other
    /// non-file source that you can identify with a stable string key.
    /// </summary>
    public static ObjGeometry GetOrAdd(string key, Func<ObjModel> modelFactory)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (modelFactory == null) throw new ArgumentNullException(nameof(modelFactory));

        return _keyed.GetOrAdd(key, _ =>
        {
            var model = modelFactory()
                ?? throw new InvalidOperationException($"modelFactory returned null for key '{key}'.");
            return ForModel(model);
        });
    }

    /// <summary>
    /// Parses <paramref name="objText"/> on first call and caches the result under <paramref name="key"/>.
    /// </summary>
    public static ObjGeometry GetOrAddText(string key, string objText)
    {
        if (objText == null) throw new ArgumentNullException(nameof(objText));
        return GetOrAdd(key, () => ObjParser.Parse(objText).Model);
    }

    /// <summary>
    /// Registers (or replaces) a keyed entry for an already-built model. Useful when the
    /// model is constructed programmatically and needs to be addressed by name from XAML
    /// via <c>Combobulate.Source</c>.
    /// </summary>
    public static ObjGeometry Register(string key, ObjModel model)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (model == null) throw new ArgumentNullException(nameof(model));

        var geometry = ForModel(model);
        _keyed[key] = geometry;
        return geometry;
    }

    /// <summary>Looks up a previously registered keyed entry. Returns null if absent.</summary>
    public static ObjGeometry? TryGet(string key)
    {
        if (key == null) return null;
        return _keyed.TryGetValue(key, out var g) ? g : null;
    }

    /// <summary>
    /// Returns geometry for an OBJ file at <paramref name="path"/>, parsing on first call.
    /// Re-parses transparently if the file's <c>LastWriteTimeUtc</c> or length has changed
    /// since the cached entry was created.
    /// </summary>
    public static ObjGeometry GetOrLoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("OBJ file not found.", fullPath);

        var stamp = info.LastWriteTimeUtc;
        var length = info.Length;

        if (_files.TryGetValue(fullPath, out var existing) &&
            existing.LastWriteTimeUtc == stamp &&
            existing.Length == length)
        {
            return existing.Geometry;
        }

        // Parse outside the dictionary lambda to keep contention low.
        var text = File.ReadAllText(fullPath);
        var model = ObjParser.Parse(text).Model;
        var geometry = ForModel(model);

        _files[fullPath] = new FileEntry
        {
            Geometry = geometry,
            LastWriteTimeUtc = stamp,
            Length = length,
        };
        return geometry;
    }

    /// <summary>
    /// Unified resolver used by <c>Combobulate.Source</c>. If <paramref name="source"/> matches
    /// a registered key, that entry wins; otherwise the value is treated as a file path.
    /// </summary>
    public static ObjGeometry Resolve(string source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        if (_keyed.TryGetValue(source, out var keyed)) return keyed;
        return GetOrLoadFile(source);
    }

    /// <summary>Removes a keyed entry. Does not affect the per-model weak cache.</summary>
    public static bool InvalidateKey(string key) => _keyed.TryRemove(key, out _);

    /// <summary>Removes a file-path entry, forcing the next request to re-read from disk.</summary>
    public static bool InvalidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return _files.TryRemove(Path.GetFullPath(path), out _);
    }

    /// <summary>Drops every entry from both keyed and file caches.</summary>
    public static void Clear()
    {
        _keyed.Clear();
        _files.Clear();
        _materialKeys.Clear();
        _materialFiles.Clear();
        MaterialResolver.ClearTextures();
    }

    /// <summary>Snapshot of currently cached keys (for diagnostics).</summary>
    public static IReadOnlyCollection<string> Keys => (IReadOnlyCollection<string>)_keyed.Keys;

    /// <summary>Snapshot of currently cached file paths (for diagnostics).</summary>
    public static IReadOnlyCollection<string> Files => (IReadOnlyCollection<string>)_files.Keys;

    // ---------------------------------------------------------------
    // Material APIs
    // ---------------------------------------------------------------

    /// <summary>Registers (or replaces) a material pack under <paramref name="key"/>.
    /// Use the same key as <c>Combobulate.Source</c> to have it auto-applied.</summary>
    public static void RegisterMaterials(string key, ObjMaterialPack pack)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (pack == null) throw new ArgumentNullException(nameof(pack));
        _materialKeys[key] = pack;
    }

    /// <summary>Looks up a previously registered material pack by key.</summary>
    public static ObjMaterialPack? TryGetMaterials(string? key)
    {
        if (key == null) return null;
        return _materialKeys.TryGetValue(key, out var p) ? p : null;
    }

    /// <summary>Adds or updates a single material entry in the pack registered under <paramref name="key"/>.
    /// Creates a fresh pack if none exists.</summary>
    public static void RegisterMaterial(string key, string usemtlName, ObjMaterial material)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        if (usemtlName == null) throw new ArgumentNullException(nameof(usemtlName));
        if (material == null) throw new ArgumentNullException(nameof(material));

        _materialKeys.AddOrUpdate(key,
            _ =>
            {
                var dict = new Dictionary<string, ObjMaterial>(StringComparer.Ordinal) { [usemtlName] = material };
                return new ObjMaterialPack(dict);
            },
            (_, existing) =>
            {
                var dict = new Dictionary<string, ObjMaterial>(existing.Materials, StringComparer.Ordinal)
                {
                    [usemtlName] = material,
                };
                return new ObjMaterialPack(dict, existing.Fallback);
            });
    }

    /// <summary>Loads (or returns a cached) material pack from an MTL file.
    /// Re-parses transparently if the file changed on disk.</summary>
    public static ObjMaterialPack GetOrLoadMtlFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("MTL file not found.", fullPath);

        var stamp = info.LastWriteTimeUtc;
        var length = info.Length;

        if (_materialFiles.TryGetValue(fullPath, out var existing) &&
            existing.LastWriteTimeUtc == stamp &&
            existing.Length == length)
        {
            return existing.Pack;
        }

        var text = File.ReadAllText(fullPath);
        var parsed = MtlParser.Parse(text);
        var pack = ConvertPack(parsed, Path.GetDirectoryName(fullPath));

        _materialFiles[fullPath] = new MtlFileEntry
        {
            Pack = pack,
            LastWriteTimeUtc = stamp,
            Length = length,
        };
        return pack;
    }

    /// <summary>
    /// Resolves and merges every <c>mtllib</c> referenced by <paramref name="model"/>.
    /// Each library is resolved against <paramref name="baseDirectory"/> if it isn't an absolute path.
    /// Errors loading individual libraries are reported via <paramref name="onError"/> (if provided)
    /// and do not abort the merge.
    /// </summary>
    public static ObjMaterialPack? GetOrLoadMtlForModel(ObjModel model, string? baseDirectory, Action<string, Exception>? onError = null)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (model.MaterialLibraries.Count == 0) return null;

        var merged = new Dictionary<string, ObjMaterial>(StringComparer.Ordinal);
        ObjMaterial? fallback = null;

        foreach (var lib in model.MaterialLibraries)
        {
            string resolved;
            if (Path.IsPathRooted(lib))
                resolved = lib;
            else if (!string.IsNullOrEmpty(baseDirectory))
                resolved = Path.Combine(baseDirectory!, lib);
            else
                resolved = lib;

            try
            {
                var pack = GetOrLoadMtlFile(resolved);
                foreach (var (name, mat) in pack.Materials)
                    merged[name] = mat;
                fallback ??= pack.Fallback;
            }
            catch (Exception ex)
            {
                onError?.Invoke(resolved, ex);
            }
        }

        return merged.Count == 0 && fallback == null ? null : new ObjMaterialPack(merged, fallback);
    }

    public static bool InvalidateMaterials(string key) => _materialKeys.TryRemove(key, out _);

    public static bool InvalidateMtlFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return _materialFiles.TryRemove(Path.GetFullPath(path), out _);
    }

    private static ObjMaterialPack ConvertPack(MtlParseResult parsed, string? baseDir)
    {
        var dict = new Dictionary<string, ObjMaterial>(StringComparer.Ordinal);
        foreach (var (name, mtl) in parsed.Materials)
            dict[name] = ConvertMaterial(mtl, baseDir);
        return new ObjMaterialPack(dict);
    }

    private static ObjMaterial ConvertMaterial(MtlMaterial mtl, string? baseDir)
    {
        ObjTextureSource? texture = null;
        var scale = System.Numerics.Vector2.One;
        var offset = System.Numerics.Vector2.Zero;
        var clamp = false;

        if (mtl.DiffuseTexture is { } tref)
        {
            scale = tref.Scale;
            offset = tref.Offset;
            clamp = tref.Clamp;

            string? resolvedPath = null;
            if (Path.IsPathRooted(tref.Path))
                resolvedPath = tref.Path;
            else if (!string.IsNullOrEmpty(baseDir))
                resolvedPath = Path.Combine(baseDir!, tref.Path);

            if (resolvedPath != null && File.Exists(resolvedPath))
            {
                texture = ObjTextureSource.FromFile(resolvedPath);
            }
            else if (Uri.TryCreate(tref.Path, UriKind.Absolute, out var uri))
            {
                texture = ObjTextureSource.FromUri(uri);
            }
        }

        return new ObjMaterial
        {
            Name = mtl.Name,
            DiffuseColor = mtl.DiffuseColor is { } v ? ToColor(v) : null,
            DiffuseTexture = texture,
            UvScale = scale,
            UvOffset = offset,
            ClampUv = clamp,
        };
    }

    private static global::Windows.UI.Color ToColor(System.Numerics.Vector3 v)
    {
        static byte To255(float f) => (byte)Math.Clamp((int)Math.Round(f * 255f), 0, 255);
        return global::Windows.UI.Color.FromArgb(255, To255(v.X), To255(v.Y), To255(v.Z));
    }
}
