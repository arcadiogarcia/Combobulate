using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

#if WINAPPSDK
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Media;
#else
using Windows.UI.Composition;
#if !COMBOBULATE_NO_XAML
using Windows.UI.Xaml.Media;
#endif
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

#if WINAPPSDK
    /// <summary>
    /// Optional fast-path that decodes this source's pixels DIRECTLY into a
    /// caller-provided <see cref="Microsoft.UI.Composition.CompositionDrawingSurface"/>,
    /// resizing the surface to match the decoded image as needed. Returning
    /// <c>true</c> indicates the surface now holds the decoded pixels and the
    /// caller can keep using it. Returning <c>false</c> (the default) means
    /// this source can't draw in-place; the caller must fall back to
    /// <see cref="CreateSurfaceAsync"/>.
    ///
    /// <para>
    /// Why this exists: every effect graph in Combobulate that consumes a
    /// texture (lit-material front cover) snapshots its source brush's sampler
    /// at <c>SetSourceParameter</c> time. If the inner <c>CompositionSurfaceBrush</c>
    /// has no surface (or only a 1×1 placeholder) at that moment, the effect
    /// renders the lighting layer with no diffuse — even after the brush's
    /// <c>Surface</c> is later assigned, the effect doesn't re-sample.
    /// Drawing in-place into a surface that brushes were bound to from the
    /// start sidesteps the issue: the brush's <c>Surface</c> reference never
    /// changes, only the surface's pixels do, and Composition picks up the
    /// new pixels automatically.
    /// </para>
    /// </summary>
    internal virtual Task<bool> TryDecodeIntoAsync(
        Compositor compositor,
        Microsoft.UI.Composition.CompositionDrawingSurface target) =>
        Task.FromResult(false);
#endif

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
    /// The delegate receives the source URI plus the requested
    /// <c>desiredMaxSize</c> cap (zero/zero = unclamped) and returns the URI of a
    /// file (typically a baked PNG under the app's local state) that will be
    /// handed to <c>LoadedImageSurface.StartLoadFromUri</c> in place of the
    /// original. Intended for diagnostic overlays in DEBUG builds — e.g. baking
    /// the decoded resolution into the corner of every cover so the rendered
    /// LOD is visible at a glance.
    /// </summary>
    /// <remarks>
    /// Why a URI and not raw bytes: <c>LoadedImageSurface.StartLoadFromStream</c>
    /// fed by an <c>InMemoryRandomAccessStream</c> corrupts large textures
    /// (rows past ~25% of the height get replaced with garbage for non-stride-
    /// aligned widths). <c>StartLoadFromUri</c> with a file path goes through a
    /// different decode path and renders reliably at any size, so the host
    /// app is expected to persist the baked bytes to a temp file before
    /// returning. Setting this slows every URI texture load (extra decode +
    /// Win2D draw + PNG encode + disk write). Production builds must leave
    /// it null. Returning null falls back to the standard fast path.
    /// </remarks>
    public static Func<Uri, Size, Task<Uri?>>? UriDecodeInterceptor { get; set; }

    /// <summary>
    /// Optional sink for diagnostic strings emitted by texture loads — surface
    /// load completion status, interceptor results, etc. Host apps hook this to
    /// their perf log in DEBUG builds. Null in production = no logging.
    /// </summary>
    public static Action<string>? DiagnosticLog { get; set; }

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

    /// <summary>
    /// Opt-in switch that forces file:// URIs through the OS-managed
    /// <see cref="LoadedImageSurface"/> decode path instead of the default
    /// Win2D <see cref="CompositionDrawingSurface"/> path.
    ///
    /// <para>
    /// The Win2D path is the default because <see cref="LoadedImageSurface"/>
    /// silently stops firing <c>LoadCompleted</c> after ~30-40 surfaces per
    /// process (see <see cref="LoadAsCompositionSurfaceAsync"/>). However, the
    /// Win2D <see cref="CompositionDrawingSurface"/> path can intermittently
    /// trigger a Direct2D "Objects used together must be created from the same
    /// factory instance" fail-fast at composition-commit time under repeated
    /// surface churn. Apps that create only a small, bounded number of surfaces
    /// (well under the LoadedImageSurface ceiling) can set this to <c>true</c>
    /// to take the stable OS-managed path and avoid that fail-fast entirely.
    /// </para>
    /// </summary>
    public static bool PreferLoadedImageSurface { get; set; }

#if WINAPPSDK
    /// <summary>
    /// One <see cref="CompositionGraphicsDevice"/> per Compositor. Created
    /// lazily on first use; held weakly so it dies with the compositor.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Compositor, CompositionGraphicsDevice> _graphicsDevices = new();

    /// <summary>
    /// Optional override hook so the host app can supply a pre-existing
    /// <see cref="CompositionGraphicsDevice"/> for a compositor. Allows the
    /// host to share a single graphics device across its own surfaces
    /// (procedural textures drawn by the host) and ObjTextureSource cover
    /// textures, instead of each subsystem allocating its own.
    ///
    /// <para>
    /// One <see cref="CompositionGraphicsDevice"/> per compositor is the
    /// recommended pattern: each device owns a separate Direct3D device-
    /// context pair, and the per-process working set / driver-resource cost
    /// adds up quickly when every cache instantiates its own. Wiring the
    /// host through this hook keeps the process to a single shared device.
    /// </para>
    /// </summary>
    public static Func<Compositor, CompositionGraphicsDevice>? GraphicsDeviceFactory { get; set; }

    /// <summary>
    /// Returns the <see cref="CompositionGraphicsDevice"/> ObjTextureSource
    /// uses to allocate <see cref="CompositionDrawingSurface"/>s for the
    /// given compositor. Host apps that draw their own procedural surfaces
    /// (Win2D directly) should call this same accessor so every surface in
    /// the process shares the same underlying device — see
    /// <see cref="GraphicsDeviceFactory"/>.
    /// </summary>
    public static CompositionGraphicsDevice GetGraphicsDevice(Compositor compositor)
    {
        var factory = GraphicsDeviceFactory;
        if (factory != null)
            return factory(compositor);
        return _graphicsDevices.GetValue(
            compositor,
            c => CanvasComposition.CreateCompositionGraphicsDevice(c, CanvasDevice.GetSharedDevice()));
    }
#endif

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

        /// <summary>
        /// Gates concurrent texture decodes. Even with the Win2D-based decode
        /// path used on WinAppSdk (which avoids LoadedImageSurface's hard
        /// internal limits — see <see cref="LoadAsCompositionSurfaceAsync"/>),
        /// allowing dozens of parallel <c>CanvasBitmap.LoadAsync</c> calls
        /// spikes CPU and disk I/O; 4 in flight keeps decode latency low while
        /// still parallelising disk and decode work. On UWP the gate also
        /// protects against <see cref="LoadedImageSurface"/>'s internal
        /// concurrency limits.
        /// </summary>
        private static readonly System.Threading.SemaphoreSlim _decodeGate = new(4, 4);

#if COMBOBULATE_NO_XAML
        internal override async Task<ICompositionSurface> CreateSurfaceAsync(Compositor compositor)
        {
            // XAML-free / system-composition build: decode via raw WIC + Direct2D
            // (SystemImageDecoder). Honor the diagnostic interceptor for parity
            // with the WinAppSdk path, then decode file:// URIs directly. Non-file
            // URIs (ms-appx://, http(s)://) are unsupported here.
            var interceptor = UriDecodeInterceptor;
            var log = DiagnosticLog;
            Uri targetUri = _uri;
            if (interceptor != null)
            {
                try
                {
                    var bakedUri = await interceptor(_uri, _desiredMaxSize).ConfigureAwait(true);
                    if (bakedUri != null) targetUri = bakedUri;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[ObjTextureSource] interceptor threw for {_uri}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (!targetUri.IsFile)
                throw new NotSupportedException(
                    $"The XAML-free build can only decode file:// textures (got {targetUri.Scheme} for {targetUri}).");

            try
            {
                return SystemImageDecoder.DecodeFileToSurface(compositor, targetUri, _desiredMaxSize, log);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ObjTextureSource] XAML-free decode failed for {targetUri}: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
#else
        internal override async Task<ICompositionSurface> CreateSurfaceAsync(Compositor compositor)
        {
            // Diagnostic path: a host app (typically a #if DEBUG hook) can take
            // over the decode to bake an overlay into the bitmap. The host
            // returns a URI pointing at a temp file containing the baked PNG;
            // we treat it like any other file:// URI from here on.
            var interceptor = UriDecodeInterceptor;
            var log = DiagnosticLog;
            Uri targetUri = _uri;
            if (interceptor != null)
            {
                Uri? bakedUri = null;
                Exception? interceptorEx = null;
                try { bakedUri = await interceptor(_uri, _desiredMaxSize).ConfigureAwait(true); }
                catch (Exception ex) { interceptorEx = ex; }
                if (interceptorEx != null)
                    log?.Invoke($"[ObjTextureSource] interceptor threw for {_uri}: {interceptorEx.GetType().Name}: {interceptorEx.Message}");
                else
                    log?.Invoke($"[ObjTextureSource] interceptor returned baked={(bakedUri?.ToString() ?? "<null>")} for {_uri} cap={_desiredMaxSize.Width}x{_desiredMaxSize.Height}");
                if (bakedUri != null)
                    targetUri = bakedUri;
            }

#if WINAPPSDK
            // Prefer the Win2D-backed decode for file:// URIs (which covers
            // every real cover path). <see cref="LoadAsCompositionSurfaceAsync"/>
            // explains why we can't keep using LoadedImageSurface here. Non-file
            // URIs (ms-appx://, http(s)://, etc.) still go through
            // LoadedImageSurface because Win2D can't load those directly.
            if (targetUri.IsFile && !PreferLoadedImageSurface)
                return await LoadAsCompositionSurfaceAsync(compositor, targetUri, _desiredMaxSize, log).ConfigureAwait(true);
#endif

            return await LoadFromLoadedImageSurfaceAsync(compositor, targetUri, _desiredMaxSize, log).ConfigureAwait(true);
        }
#endif

#if WINAPPSDK
        internal override async Task<bool> TryDecodeIntoAsync(Compositor compositor, CompositionDrawingSurface target)
        {
            var interceptor = UriDecodeInterceptor;
            var log = DiagnosticLog;
            Uri targetUri = _uri;
            if (interceptor != null)
            {
                try
                {
                    var bakedUri = await interceptor(_uri, _desiredMaxSize).ConfigureAwait(true);
                    if (bakedUri != null) targetUri = bakedUri;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[ObjTextureSource] interceptor threw for {_uri}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (!targetUri.IsFile) return false; // only Win2D path supports in-place

            await _decodeGate.WaitAsync().ConfigureAwait(true);
            try
            {
                var device = CanvasDevice.GetSharedDevice();
                using var bmp = await CanvasBitmap.LoadAsync(device, targetUri.LocalPath);
                var (sourceW, sourceH, targetW, targetH) = ComputeTargetSize(bmp.SizeInPixels, _desiredMaxSize);

                var dispatcher = compositor.DispatcherQueue;
                if (dispatcher == null || dispatcher.HasThreadAccess)
                {
                    ResizeAndDrawIntoSurface(target, bmp, sourceW, sourceH, targetW, targetH);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var bmpCaptured = bmp;
                    var sw = sourceW; var sh = sourceH; var tw = targetW; var th = targetH;
                    bool enqueued = dispatcher.TryEnqueue(() =>
                    {
                        try { ResizeAndDrawIntoSurface(target, bmpCaptured, sw, sh, tw, th); tcs.SetResult(true); }
                        catch (Exception ex) { tcs.SetException(ex); }
                    });
                    if (!enqueued) return false;
                    await tcs.Task.ConfigureAwait(true);
                }

                log?.Invoke($"[ObjTextureSource] Win2D in-place decode src={sourceW}x{sourceH} target={targetW}x{targetH} uri={targetUri}");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ObjTextureSource] Win2D in-place decode failed uri={targetUri}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                _decodeGate.Release();
            }
        }

        private static (uint sourceW, uint sourceH, uint targetW, uint targetH) ComputeTargetSize(
            global::Windows.Graphics.Imaging.BitmapSize srcSize, Size desiredMaxSize)
        {
            uint sourceW = srcSize.Width;
            uint sourceH = srcSize.Height;
            uint targetW = sourceW;
            uint targetH = sourceH;
            if (desiredMaxSize.Width > 0 && desiredMaxSize.Height > 0)
            {
                double sx = desiredMaxSize.Width / (double)sourceW;
                double sy = desiredMaxSize.Height / (double)sourceH;
                double s = Math.Min(Math.Min(sx, sy), 1.0);
                targetW = (uint)Math.Max(1, Math.Round(sourceW * s));
                targetH = (uint)Math.Max(1, Math.Round(sourceH * s));
            }
            // Cap the decoded texture size to keep GPU working set and JPEG
            // decode time bounded. 2048 matches the focus-mode LOD target
            // (a focused cover at 2× DPI fills ~1.4k DIPs ≈ 2.8k pixels of
            // surface area but 2048 along the longest axis is visually
            // indistinguishable from full resolution at typical viewing
            // distances). Shelf entries downscale further via desiredMaxSize
            // before reaching this cap. NOTE: a previous diagnosis blamed a
            // ~900-axis CompositionDrawingSurface "atlas cap" — that turned
            // out to be wrong; the real bug was elsewhere (SpriteVisual size
            // sampling, fixed in BakedAspectGraphRenderer.BuildTreeContent).
            // This cap is purely a memory/decode-cost budget.
            const uint MaxAxis = 2048;
            if (targetW > MaxAxis || targetH > MaxAxis)
            {
                double cap = Math.Min((double)MaxAxis / targetW, (double)MaxAxis / targetH);
                targetW = (uint)Math.Max(1, Math.Round(targetW * cap));
                targetH = (uint)Math.Max(1, Math.Round(targetH * cap));
            }
            return (sourceW, sourceH, targetW, targetH);
        }

        private static void ResizeAndDrawIntoSurface(
            CompositionDrawingSurface surface, CanvasBitmap bmp,
            uint sourceW, uint sourceH, uint targetW, uint targetH)
        {
            // Resize the placeholder to the decode target. CompositionDrawingSurface
            // supports Resize on the compositor's dispatcher thread. targetW/H have
            // already been capped by ComputeTargetSize() to the budget axis limit.
            CanvasComposition.Resize(surface, new Size(targetW, targetH));
            using var ds = CanvasComposition.CreateDrawingSession(surface);
            ds.DrawImage(
                bmp,
                new Rect(0, 0, targetW, targetH),
                new Rect(0, 0, sourceW, sourceH));
        }

#endif

        /// <summary>
        /// Throttled wrapper around <see cref="LoadedImageSurface.StartLoadFromUri(Uri)"/>
        /// + <see cref="AwaitLoadAsync"/>. Used for non-file URIs and on the UWP
        /// target. Always releases the gate, including on decode failure or
        /// exception, so a single bad image can't permanently hold a slot.
        /// </summary>
#if !COMBOBULATE_NO_XAML
        // LoadedImageSurface.LoadCompleted is not guaranteed to fire. Two failure
        // modes are known and neither errors out — the awaiting load just never
        // returns and the bound brush stays blank forever:
        //   1. After a process accumulates ~30-40 surfaces the event silently
        //      stops firing for new ones (documented on LoadAsCompositionSurfaceAsync).
        //   2. LoadCompleted is a non-sticky event and StartLoadFromUri begins the
        //      decode before it returns the object we subscribe to, so a load that
        //      completes inline (already-decodable image) can deliver its
        //      completion before our handler is attached.
        // We can't detect either case up front (DecodedPhysicalSize/DecodedSize
        // throw until the load completes, and there is no "already loaded" flag),
        // so bound the wait. On timeout, fall back to the Win2D decode path for
        // file:// URIs — it owns its surface outright and always completes.
        private const int LoadedImageSurfaceLoadTimeoutMs = 1200;

        private static async Task<ICompositionSurface> LoadFromLoadedImageSurfaceAsync(
            Compositor compositor, Uri uri, Size desiredMaxSize, Action<string>? log, bool allowWin2DFallback = true)
        {
            LoadedImageSurface lis;
            bool completed;
            await _decodeGate.WaitAsync().ConfigureAwait(true);
            try
            {
                lis = desiredMaxSize.Width > 0 && desiredMaxSize.Height > 0
                    ? LoadedImageSurface.StartLoadFromUri(uri, desiredMaxSize)
                    : LoadedImageSurface.StartLoadFromUri(uri);
                completed = await AwaitLoadAsync(lis, uri, log).ConfigureAwait(true);
            }
            finally
            {
                _decodeGate.Release();
            }

            if (completed)
                return lis;

#if WINAPPSDK
            // LoadCompleted never arrived (raced-ahead completion or the surface
            // ceiling). For file:// URIs the Win2D path is a reliable substitute;
            // pass allowLisFallback:false so its own error path can't bounce back
            // here and ping-pong.
            if (allowWin2DFallback && uri.IsFile)
            {
                log?.Invoke($"[ObjTextureSource] LoadedImageSurface did not signal within {LoadedImageSurfaceLoadTimeoutMs}ms; falling back to Win2D for {uri}");
                return await LoadAsCompositionSurfaceAsync(compositor, uri, desiredMaxSize, log, allowLisFallback: false).ConfigureAwait(true);
            }
#endif
            // No fallback available (non-file URI, or already inside the Win2D
            // error path). Return the surface best-effort — it may still finish
            // decoding even though we never observed the event.
            return lis;
        }
#endif

#if WINAPPSDK
        /// <summary>
        /// Decodes <paramref name="uri"/> via Win2D and blits the pixels into a
        /// <see cref="CompositionDrawingSurface"/> that we own outright.
        /// </summary>
        /// <remarks>
        /// The Composition-XAML <see cref="LoadedImageSurface"/> looks like
        /// the obvious choice for "decode a file into an ICompositionSurface",
        /// but in practice it has two hard limits that bite at scale:
        /// <list type="bullet">
        ///   <item>After a process has created roughly 30-40 surfaces,
        ///   <c>LoadCompleted</c> silently stops firing for any new ones —
        ///   they never error out and never invoke the handler, so the
        ///   awaiting load never returns and the bound brush stays blank.
        ///   Throttling concurrency reduces but does not eliminate this.</item>
        ///   <item>Even when it works, it allocates GPU-backed XAML-internal
        ///   resources that we can't release independently of the
        ///   <c>LoadedImageSurface</c> object's lifetime.</item>
        /// </list>
        /// Going through <see cref="CanvasBitmap.LoadAsync(ICanvasResourceCreator, string)"/>
        /// + a <see cref="CompositionDrawingSurface"/> we created ourselves
        /// gives us a surface that's fully owned by this cache: it's an
        /// <see cref="IDisposable"/> we can release the moment the cache
        /// evicts the entry, with no shared pool to exhaust. The
        /// <see cref="CanvasBitmap"/> is transient — we just blit it into the
        /// drawing surface (with downsample baked in if a cap was requested)
        /// then dispose it via <c>using</c>.
        /// </remarks>
        private static async Task<ICompositionSurface> LoadAsCompositionSurfaceAsync(
            Compositor compositor, Uri uri, Size desiredMaxSize, Action<string>? log, bool allowLisFallback = true)
        {
            await _decodeGate.WaitAsync().ConfigureAwait(true);
            try
            {
                var device = CanvasDevice.GetSharedDevice();
                // Intentionally NOT using `using var` here. CanvasBitmap.Dispose
                // releases the underlying D2D bitmap; if disposal races the
                // GPU completion of the DrawImage we queued into the surface,
                // the queued draw silently produces nothing (surface ends up
                // empty / unsampleable). For now, keep the bitmap alive for
                // the lifetime of the process; covers cache at modest counts
                // (≤ a few hundred) so the leak is bounded.
                var bmp = await CanvasBitmap.LoadAsync(device, uri.LocalPath);
                var (sourceW, sourceH, targetW, targetH) = ComputeTargetSize(bmp.SizeInPixels, desiredMaxSize);

                // Composition + Win2D drawing-session APIs have thread affinity to
                // the compositor's dispatcher. The decode above may resume on a
                // worker thread; explicitly marshal the surface allocation and
                // draw to the dispatcher to avoid a native XAML
                // STATUS_FATAL_USER_CALLBACK_EXCEPTION (E_UNEXPECTED) when the
                // composition graphics device is touched off-thread.
                var dispatcher = compositor.DispatcherQueue;
                CompositionDrawingSurface drawingSurface;
                if (dispatcher == null || dispatcher.HasThreadAccess)
                {
                    drawingSurface = CreateAndDrawSurface(compositor, bmp, sourceW, sourceH, targetW, targetH);
                }
                else
                {
                    var tcs = new TaskCompletionSource<CompositionDrawingSurface>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var bmpCaptured = bmp;
                    var sw = sourceW; var sh = sourceH; var tw = targetW; var th = targetH;
                    bool enqueued = dispatcher.TryEnqueue(() =>
                    {
                        try { tcs.SetResult(CreateAndDrawSurface(compositor, bmpCaptured, sw, sh, tw, th)); }
                        catch (Exception ex) { tcs.SetException(ex); }
                    });
                    if (!enqueued)
                        throw new InvalidOperationException("Compositor dispatcher refused enqueue (shutting down?).");
                    drawingSurface = await tcs.Task.ConfigureAwait(true);
                }

                log?.Invoke($"[ObjTextureSource] Win2D decode src={sourceW}x{sourceH} target={targetW}x{targetH} uri={uri}");
                return drawingSurface;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[ObjTextureSource] Win2D decode failed uri={uri}: {ex.GetType().Name}: {ex.Message}");
                // Fall back to the legacy path so a Win2D-specific failure
                // (e.g. unsupported image format) doesn't leave the cover blank.
                // Pass allowWin2DFallback:false so it can't bounce straight back
                // here on its own timeout and ping-pong between the two paths.
                if (allowLisFallback)
                    return await LoadFromLoadedImageSurfaceAsync(compositor, uri, desiredMaxSize, log, allowWin2DFallback: false).ConfigureAwait(true);
                throw;
            }
            finally
            {
                _decodeGate.Release();
            }
        }

        private static CompositionDrawingSurface CreateAndDrawSurface(
            Compositor compositor, CanvasBitmap bmp, uint sourceW, uint sourceH, uint targetW, uint targetH)
        {
            // targetW/H have already been capped by ComputeTargetSize() to the
            // memory/decode-cost budget axis limit.
            var graphicsDevice = GetGraphicsDevice(compositor);
            var drawingSurface = graphicsDevice.CreateDrawingSurface(
                new Size(targetW, targetH),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);

            using (var ds = CanvasComposition.CreateDrawingSession(drawingSurface))
            {
                ds.DrawImage(bmp, new Rect(0, 0, targetW, targetH), new Rect(0, 0, sourceW, sourceH));
            }

            return drawingSurface;
        }
#endif

        /// <summary>
        /// Awaits the <see cref="LoadedImageSurface.LoadCompleted"/> event so
        /// callers (cache <c>AcquireAsync</c>, etc.) only see the surface once
        /// pixels have actually decoded. Without this, the surface object is
        /// returned synchronously and bound to brushes while decode is still
        /// in flight — some effect graphs (notably BlendEffect Multiply with a
        /// SceneLightingEffect foreground) snapshot the effective sampler at
        /// bind time and never repaint when the surface's pixels arrive,
        /// leaving the rendered face as the lighting gradient only (no diffuse).
        /// </summary>
#if !COMBOBULATE_NO_XAML
        /// <summary>
        /// Waits for <see cref="LoadedImageSurface.LoadCompleted"/>, bounded by
        /// <see cref="LoadedImageSurfaceLoadTimeoutMs"/>. Returns <c>true</c> when
        /// the surface decoded successfully, <c>false</c> on a decode failure OR
        /// when the event never arrived within the timeout (the two documented
        /// LoadCompleted failure modes described on
        /// <see cref="LoadFromLoadedImageSurfaceAsync"/>).
        /// </summary>
        private static async Task<bool> AwaitLoadAsync(LoadedImageSurface lis, Uri uri, Action<string>? log)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(LoadedImageSurface s, LoadedImageSourceLoadCompletedEventArgs e)
            {
                log?.Invoke($"[ObjTextureSource] LoadCompleted source={uri} status={e.Status}");
                lis.LoadCompleted -= Handler;
                tcs.TrySetResult(e.Status == LoadedImageSourceLoadStatus.Success);
            }
            lis.LoadCompleted += Handler;

            var winner = await Task.WhenAny(tcs.Task, Task.Delay(LoadedImageSurfaceLoadTimeoutMs)).ConfigureAwait(true);
            if (winner == tcs.Task)
                return tcs.Task.Result;

            // Timed out — the completion event was never delivered to us. Stop
            // listening and report failure so the caller can fall back.
            lis.LoadCompleted -= Handler;
            log?.Invoke($"[ObjTextureSource] LoadCompleted never fired within {LoadedImageSurfaceLoadTimeoutMs}ms for {uri}");
            return false;
        }
#endif
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
#if COMBOBULATE_NO_XAML
            throw new NotSupportedException("Texture loading is not available in the XAML-free build.");
#else
            var stream = _factory();
            var surface = LoadedImageSurface.StartLoadFromStream(stream);
            return Task.FromResult<ICompositionSurface>(surface);
#endif
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
#if COMBOBULATE_NO_XAML
            throw new NotSupportedException("Texture loading is not available in the XAML-free build.");
#else
            var stream = BytesToStream(_pngBytes);
            var surface = LoadedImageSurface.StartLoadFromStream(stream);
            return Task.FromResult<ICompositionSurface>(surface);
#endif
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
