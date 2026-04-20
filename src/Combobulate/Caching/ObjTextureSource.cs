using System;
using System.IO;
using System.Threading.Tasks;
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

    public static ObjTextureSource FromUri(Uri uri) => new UriSource(uri);

    public static ObjTextureSource FromFile(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new ArgumentException("Path is required.", nameof(absolutePath));
        var full = Path.GetFullPath(absolutePath);
        return new UriSource(new Uri(full));
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
        public UriSource(Uri uri) { _uri = uri; }
        public override string CacheKey => "uri:" + _uri.AbsoluteUri;
        internal override Task<ICompositionSurface> CreateSurfaceAsync(Compositor compositor)
        {
            var surface = LoadedImageSurface.StartLoadFromUri(_uri);
            return Task.FromResult<ICompositionSurface>(surface);
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
