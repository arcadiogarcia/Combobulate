using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

#if WINAPPSDK
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
#else
using Windows.UI.Composition;
using Windows.UI.Xaml.Media;
#endif

namespace Combobulate.Caching;

/// <summary>
/// Identifies the pixel data backing a material's diffuse channel. Sources are cached
/// process-wide by <see cref="CacheKey"/>, so the same image is decoded once and the
/// resulting surface is reused across every control that brushes with it.
///
/// <para>
/// Sources created by <see cref="FromBitmap(SoftwareBitmap)"/> and
/// <see cref="FromStream(Func{IRandomAccessStream}, string)"/> support live pixel
/// updates via <see cref="Update(SoftwareBitmap)"/> — useful for app-rendered textures
/// (Win2D, paint-style canvases). All other sources are read-only.
/// </para>
/// </summary>
public abstract class ObjTextureSource
{
    public abstract string CacheKey { get; }

    internal event EventHandler? Invalidated;
    internal void RaiseInvalidated() => Invalidated?.Invoke(this, EventArgs.Empty);

    internal abstract Task<ICompositionSurface> CreateSurfaceAsync(Compositor compositor);

    public virtual void Update(SoftwareBitmap bitmap) =>
        throw new NotSupportedException(
            "This ObjTextureSource is read-only. Use FromBitmap or FromStream for updateable sources.");

    /// <summary>
    /// Ensures this texture's pixels are decoded and held by the shared texture
    /// cache, even if no <c>CompositionSurfaceBrush</c> is currently bound to it.
    /// Use before swapping a material to this texture to avoid a one-frame flash
    /// to blank while decoding is still in flight. The returned task completes
    /// when the underlying surface is ready to render.
    ///
    /// <para>
    /// Every call must be paired with a matching <see cref="Release"/>; the
    /// texture stays resident until pin count reaches zero AND no live
    /// composition brushes still reference its surface.
    /// </para>
    /// </summary>
    public Task<ICompositionSurface> AcquireAsync(Compositor compositor) =>
        MaterialResolver.AcquireAsync(compositor, this);

    /// <summary>
    /// Counterpart to <see cref="AcquireAsync"/>. Drops a pin; when no pins and
    /// no live brushes remain the cache entry is evicted and the underlying
    /// <c>LoadedImageSurface</c> is disposed (if it implements
    /// <see cref="IDisposable"/>).
    /// </summary>
    public void Release() => MaterialResolver.ReleaseTexture(this);

    /// <summary>
    /// When non-null, all <see cref="FromUri(Uri, Size)"/> /
    /// <see cref="FromFile(string, Size)"/> sources call this delegate at decode
    /// time instead of using <c>LoadedImageSurface.StartLoadFromUri</c> directly.
    /// The delegate receives the URI plus the requested <c>desiredMaxSize</c> cap
    /// (zero/zero = unclamped) and returns a fully-decoded
    /// <see cref="SoftwareBitmap"/> that will be encoded to PNG and handed to
    /// <c>LoadedImageSurface.StartLoadFromStream</c>. Intended for diagnostic
    /// overlays in DEBUG builds — e.g. baking the decoded resolution into the
    /// corner of every cover so the rendered LOD is visible at a glance.
    /// </summary>
    /// <remarks>
    /// Setting this slows every URI texture load (extra decode + Win2D draw +
    /// PNG re-encode). Production builds must leave it null.
    /// Returning null from the delegate falls back to the standard fast path.
    /// </remarks>
    public static Func<Uri, Size, Task<SoftwareBitmap?>>? UriDecodeInterceptor { get; set; }

    /// <summary>
    /// Drops every cached texture so the next bind re-decodes from source. Host
    /// apps use this after flipping a diagnostic toggle (e.g.
    /// <see cref="UriDecodeInterceptor"/>) so visible covers go through the new
    /// decode path immediately. Idempotent; safe to call from any thread.
    /// </summary>
    public static void ClearAllTextures() => MaterialResolver.ClearTextures();

    public static ObjTextureSource FromUri(Uri uri) => new UriSource(uri, default);

    /// <summary>
    /// Loads the texture from <paramref name="uri"/> and clamps the decoded image to
    /// <paramref name="desiredMaxSize"/> (in pixels) while preserving aspect ratio. Use
    /// this when the texture will be rendered onto a much smaller surface than its
    /// natural resolution — composition surfaces sample bilinearly without mipmaps, so
    /// downsample ratios &gt; 2× alias badly. The Windows imaging pipeline performs
    /// proper high-quality downsampling at decode time, after which bilinear sampling
    /// looks clean.
    /// </summary>
    public static ObjTextureSource FromUri(Uri uri, Size desiredMaxSize) => new UriSource(uri, desiredMaxSize);

    public static ObjTextureSource FromFile(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("Path is required.", nameof(absolutePath));
        var full = Path.GetFullPath(absolutePath);
        return new UriSource(new Uri(full), default);
    }

    /// <summary>
    /// File-path overload of <see cref="FromUri(Uri, Size)"/>. See that method for
    /// when to clamp the decode size.
    /// </summary>
    public static ObjTextureSource FromFile(string absolutePath, Size desiredMaxSize)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("Path is required.", nameof(absolutePath));
        var full = Path.GetFullPath(absolutePath);
        return new UriSource(new Uri(full), desiredMaxSize);
    }

    public static ObjTextureSource FromStream(Func<IRandomAccessStream> factory, string cacheKey)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        if (string.IsNullOrWhiteSpace(cacheKey))
            throw new ArgumentException("Cache key is required.", nameof(cacheKey));
        return new StreamSource(factory, cacheKey);
    }

    public static ObjTextureSource FromBitmap(SoftwareBitmap bitmap)
    {
        if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
        return new BitmapSource(bitmap);
    }

    public static ObjTextureSource FromSurface(ICompositionSurface surface)
    {
        if (surface == null) throw new ArgumentNullException(nameof(surface));
        return new ExternalSurfaceSource(surface);
    }

    private sealed class UriSource : ObjTextureSource
    {
        private readonly Uri _uri;
        private readonly Size _desiredMaxSize;
        public UriSource(Uri uri, Size desiredMaxSize)
        {
            _uri = uri;
            _desiredMaxSize = desiredMaxSize;
        }
        public override string CacheKey =>
            _desiredMaxSize.Width > 0 && _desiredMaxSize.Height > 0
                ? $"uri:{_uri.AbsoluteUri}@{_desiredMaxSize.Width}x{_desiredMaxSize.Height}"
                : "uri:" + _uri.AbsoluteUri;
        internal override async Task<ICompositionSurface> CreateSurfaceAsync(Compositor compositor)
        {
            // Diagnostic path: a host app (typically a #if DEBUG hook) can take
            // over the decode to bake an overlay into the bitmap. We round-trip
            // through PNG → StartLoadFromStream because LoadedImageSurface has
            // no public "load from SoftwareBitmap" constructor.
            var interceptor = UriDecodeInterceptor;
            if (interceptor != null)
            {
                SoftwareBitmap? baked = null;
                try { baked = await interceptor(_uri, _desiredMaxSize).ConfigureAwait(true); }
                catch { /* fall through to the fast path on interceptor failure */ }
                if (baked != null)
                {
                    var bytes = await EncodePngBytesAsync(baked).ConfigureAwait(true);
                    baked.Dispose();
                    var stream = BytesToStream(bytes);
                    return LoadedImageSurface.StartLoadFromStream(stream);
                }
            }

            ICompositionSurface surface = _desiredMaxSize.Width > 0 && _desiredMaxSize.Height > 0
                ? LoadedImageSurface.StartLoadFromUri(_uri, _desiredMaxSize)
                : LoadedImageSurface.StartLoadFromUri(_uri);
            return surface;
        }
    }

    private sealed class StreamSource : ObjTextureSource
    {
        private Func<IRandomAccessStream> _factory;
        private readonly string _key;
        public StreamSource(Func<IRandomAccessStream> factory, string key)
        {
            _factory = factory;
            _key = key;
        }
        public override string CacheKey => "stream:" + _key;
        internal override Task<ICompositionSurface> CreateSurfaceAsync(Compositor compositor)
        {
            var stream = _factory();
            var surface = LoadedImageSurface.StartLoadFromStream(stream);
            return Task.FromResult<ICompositionSurface>(surface);
        }
        public override void Update(SoftwareBitmap bitmap)
        {
            var bytes = Task.Run(() => EncodePngBytesAsync(bitmap)).GetAwaiter().GetResult();
            _factory = () => BytesToStream(bytes);
            RaiseInvalidated();
        }
    }

    private sealed class BitmapSource : ObjTextureSource
    {
        private byte[] _pngBytes;
        private readonly string _key;
        private static int _counter;
        public BitmapSource(SoftwareBitmap bitmap)
        {
            _key = "bmp:" + System.Threading.Interlocked.Increment(ref _counter);
            _pngBytes = Task.Run(() => EncodePngBytesAsync(bitmap)).GetAwaiter().GetResult();
        }
        public override string CacheKey => _key;
        internal override Task<ICompositionSurface> CreateSurfaceAsync(Compositor compositor)
        {
            var stream = BytesToStream(_pngBytes);
            var surface = LoadedImageSurface.StartLoadFromStream(stream);
            return Task.FromResult<ICompositionSurface>(surface);
        }
        public override void Update(SoftwareBitmap bitmap)
        {
            _pngBytes = Task.Run(() => EncodePngBytesAsync(bitmap)).GetAwaiter().GetResult();
            RaiseInvalidated();
        }
    }

    private sealed class ExternalSurfaceSource : ObjTextureSource
    {
        private readonly ICompositionSurface _surface;
        private readonly string _key;
        public ExternalSurfaceSource(ICompositionSurface surface)
        {
            _surface = surface;
            _key = "ext:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(surface);
        }
        public override string CacheKey => _key;
        internal override Task<ICompositionSurface> CreateSurfaceAsync(Compositor compositor) =>
            Task.FromResult(_surface);
    }

    private static async Task<byte[]> EncodePngBytesAsync(SoftwareBitmap bitmap)
    {
        SoftwareBitmap source = bitmap;
        if (source.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
            source.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
        {
            source = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        using var ms = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
        encoder.SetSoftwareBitmap(source);
        await encoder.FlushAsync();
        ms.Seek(0);

        var size = checked((int)ms.Size);
        var buf = new byte[size];
        var dr = new DataReader(ms.GetInputStreamAt(0));
        await dr.LoadAsync((uint)size);
        dr.ReadBytes(buf);
        dr.Dispose();
        return buf;
    }

    private static IRandomAccessStream BytesToStream(byte[] bytes)
    {
        var ms = new InMemoryRandomAccessStream();
        var dw = new DataWriter(ms.GetOutputStreamAt(0));
        dw.WriteBytes(bytes);
        dw.StoreAsync().AsTask().GetAwaiter().GetResult();
        dw.FlushAsync().AsTask().GetAwaiter().GetResult();
        dw.DetachStream();
        dw.Dispose();
        ms.Seek(0);
        return ms;
    }
}
