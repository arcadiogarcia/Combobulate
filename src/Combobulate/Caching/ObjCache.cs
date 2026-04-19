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
    }

    /// <summary>Snapshot of currently cached keys (for diagnostics).</summary>
    public static IReadOnlyCollection<string> Keys => (IReadOnlyCollection<string>)_keyed.Keys;

    /// <summary>Snapshot of currently cached file paths (for diagnostics).</summary>
    public static IReadOnlyCollection<string> Files => (IReadOnlyCollection<string>)_files.Keys;
}
