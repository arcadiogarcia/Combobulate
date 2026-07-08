#if COMBOBULATE_NO_XAML
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

namespace Combobulate.Caching;

/// <summary>
/// Win2D-free, XAML-free image decode for the system-composition
/// (<c>COMBOBULATE_NO_XAML</c>) build. Decodes an image file into a system
/// <see cref="Windows.UI.Composition.CompositionDrawingSurface"/> using raw
/// Windows Imaging Component (WIC) + Direct2D + Composition COM interop, with
/// zero managed graphics dependencies (no Win2D, no <c>LoadedImageSurface</c>).
///
/// <para><b>Why this exists.</b> The WinAppSdk build decodes through Win2D
/// (<c>CanvasBitmap</c> + <c>CanvasComposition</c>), which binds to the lifted
/// <c>Microsoft.UI.Composition</c> compositor. The <c>Microsoft.Graphics.Win2D</c>
/// NuGet package has no asset that binds Win2D to the <em>system</em>
/// <c>Windows.UI.Composition</c> compositor for a modern <c>net*-windows</c> TFM
/// (its <c>net6.0-windows</c> asset depends on WindowsAppSDK and is lifted-only),
/// so it can't produce a surface for the transparent, per-pixel-alpha ghost
/// window that renders on the system compositor. This decoder fills that gap the
/// same way <see cref="Combobulate.Rendering.D2DTriangleGeometry"/> builds
/// geometry without Win2D: by talking to Direct2D directly.</para>
///
/// <para><b>Pipeline.</b>
/// <list type="number">
///   <item>WIC decodes the file to a 32bpp premultiplied BGRA
///     <c>IWICBitmapSource</c> (with an optional high-quality downscale to the
///     requested cap).</item>
///   <item>A cached process-wide Direct3D 11 + Direct2D device backs a per-
///     compositor system <see cref="CompositionGraphicsDevice"/> (created via
///     <c>ICompositorInterop.CreateGraphicsDevice</c>).</item>
///   <item>A <see cref="CompositionDrawingSurface"/> of the target size is
///     allocated; <c>ICompositionDrawingSurfaceInterop.BeginDraw</c> hands back
///     an <c>ID2D1DeviceContext</c>, into which we draw the WIC bitmap, then
///     <c>EndDraw</c>.</item>
/// </list>
/// The returned surface is owned by the caller's cache and is a normal system
/// <see cref="ICompositionSurface"/> that can brush any composition visual.</para>
/// </summary>
internal static unsafe class SystemImageDecoder
{
    // ---- CLSIDs / IIDs ----
    private static readonly Guid CLSID_WICImagingFactory =
        new("CACAF262-9370-4615-A13B-9F5539DA4C0A");
    private static readonly Guid IID_IWICImagingFactory =
        new("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
    private static readonly Guid IID_IDXGIDevice =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_ICompositorInterop =
        new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop =
        new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid IID_ID2D1DeviceContext =
        new("E8F7FE7A-191C-466D-AD95-975678BDA998");

    // 32bpp premultiplied BGRA — matches DirectXAlphaMode.Premultiplied.
    private static readonly Guid GUID_WICPixelFormat32bppPBGRA =
        new("6FDDC324-4E03-4BFE-B185-3D77768DC910");

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint GENERIC_READ = 0x80000000;
    private const int WICDecodeMetadataCacheOnDemand = 0;
    private const int WICBitmapDitherTypeNone = 0;
    private const int WICBitmapPaletteTypeCustom = 0;
    private const int WICBitmapInterpolationModeFant = 3;

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D_DRIVER_TYPE_WARP = 5;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;

    private const int D2D1_BITMAP_INTERPOLATION_MODE_LINEAR = 1;
    private const int S_OK = 0;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevels, uint sdkVersion,
        out IntPtr ppDevice, IntPtr pFeatureLevel, IntPtr ppImmediateContext);

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateDevice(
        IntPtr dxgiDevice, IntPtr creationProperties, out IntPtr d2dDevice);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private static readonly object Gate = new();
    private static IntPtr _d2dDevice; // ID2D1Device* (process-wide, never freed)
    private static readonly ConditionalWeakTable<Compositor, object> _graphicsDevices = new();

    /// <summary>
    /// Decodes the file at <paramref name="fileUri"/> into a system
    /// <see cref="CompositionDrawingSurface"/> on <paramref name="compositor"/>.
    /// Must be called on the compositor's thread (the caller — MaterialResolver —
    /// awaits on the UI/dispatcher thread). Throws on any decode/interop failure;
    /// the caller falls back to a solid material.
    /// </summary>
    public static ICompositionSurface DecodeFileToSurface(
        Compositor compositor, Uri fileUri, Size desiredMaxSize, Action<string>? log)
    {
        if (compositor is null) throw new ArgumentNullException(nameof(compositor));
        if (fileUri is null) throw new ArgumentNullException(nameof(fileUri));
        if (!fileUri.IsFile)
            throw new NotSupportedException(
                $"The XAML-free decoder only supports file:// URIs (got {fileUri.Scheme}).");

        string path = fileUri.LocalPath;

        IntPtr wicSource = IntPtr.Zero; // IWICBitmapSource* to draw (converter or scaler->converter)
        try
        {
            wicSource = CreateWicPbgraSource(path, desiredMaxSize, out uint w, out uint h);

            var graphicsDevice = GetGraphicsDevice(compositor);
            var surface = graphicsDevice.CreateDrawingSurface(
                new Size(w, h),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);

            DrawWicSourceIntoSurface(surface, wicSource, w, h);

            log?.Invoke($"[SystemImageDecoder] decoded {w}x{h} from {path}");
            return surface;
        }
        finally
        {
            if (wicSource != IntPtr.Zero) Marshal.Release(wicSource);
        }
    }

    // -----------------------------------------------------------------
    //  WIC: decode file -> 32bpp PBGRA IWICBitmapSource (optionally scaled).
    // -----------------------------------------------------------------
    private static IntPtr CreateWicPbgraSource(
        string path, Size desiredMaxSize, out uint width, out uint height)
    {
        IntPtr factory = IntPtr.Zero, decoder = IntPtr.Zero, frame = IntPtr.Zero,
               scaler = IntPtr.Zero, converter = IntPtr.Zero;
        try
        {
            CheckHr(CoCreateInstance(
                in CLSID_WICImagingFactory, IntPtr.Zero, CLSCTX_INPROC_SERVER,
                in IID_IWICImagingFactory, out factory), "CoCreateInstance(WICImagingFactory)");

            // IWICImagingFactory::CreateDecoderFromFilename (slot 3)
            var createDecoder =
                (delegate* unmanaged[Stdcall]<IntPtr, char*, Guid*, uint, int, IntPtr*, int>)(*(void***)factory)[3];
            fixed (char* pPath = path)
                CheckHr(createDecoder(factory, pPath, null, GENERIC_READ,
                    WICDecodeMetadataCacheOnDemand, &decoder), "CreateDecoderFromFilename");

            // IWICBitmapDecoder::GetFrame (slot 13)
            var getFrame = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)decoder)[13];
            CheckHr(getFrame(decoder, 0, &frame), "IWICBitmapDecoder::GetFrame");

            // IWICBitmapSource::GetSize (slot 3) on the frame.
            uint srcW, srcH;
            var getSize = (delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)(*(void***)frame)[3];
            CheckHr(getSize(frame, &srcW, &srcH), "IWICBitmapFrameDecode::GetSize");

            ComputeTargetSize(srcW, srcH, desiredMaxSize, out uint dstW, out uint dstH);

            // Source fed to the format converter: the frame, or a scaler if downsizing.
            IntPtr toConvert = frame;
            if (dstW != srcW || dstH != srcH)
            {
                // IWICImagingFactory::CreateBitmapScaler (slot 11)
                var createScaler = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)factory)[11];
                CheckHr(createScaler(factory, &scaler), "CreateBitmapScaler");
                // IWICBitmapScaler::Initialize (slot 8): (source, w, h, interpolationMode)
                var scalerInit =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int, int>)(*(void***)scaler)[8];
                CheckHr(scalerInit(scaler, frame, dstW, dstH, WICBitmapInterpolationModeFant),
                    "IWICBitmapScaler::Initialize");
                toConvert = scaler;
            }

            // IWICImagingFactory::CreateFormatConverter (slot 10)
            var createConverter = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)factory)[10];
            CheckHr(createConverter(factory, &converter), "CreateFormatConverter");

            // IWICFormatConverter::Initialize (slot 8):
            //   (source, dstFormat*, ditherType, palette, alphaThresholdPercent, paletteType)
            var convInit =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)(*(void***)converter)[8];
            Guid dstFmt = GUID_WICPixelFormat32bppPBGRA;
            CheckHr(convInit(converter, toConvert, &dstFmt, WICBitmapDitherTypeNone,
                IntPtr.Zero, 0.0, WICBitmapPaletteTypeCustom), "IWICFormatConverter::Initialize");

            width = dstW;
            height = dstH;

            // Hand the converter ref to the caller; keep it alive.
            IntPtr result = converter;
            converter = IntPtr.Zero;
            return result;
        }
        finally
        {
            if (converter != IntPtr.Zero) Marshal.Release(converter);
            if (scaler != IntPtr.Zero) Marshal.Release(scaler);
            if (frame != IntPtr.Zero) Marshal.Release(frame);
            if (decoder != IntPtr.Zero) Marshal.Release(decoder);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    private static ref Guid GetPbgraFormatRef()
    {
        // Local static holder so we can take a fixed pointer to the format GUID.
        return ref _pbgra;
    }
    private static Guid _pbgra = new("6FDDC324-4E03-4BFE-B185-3D77768DC910");

    private static void ComputeTargetSize(
        uint srcW, uint srcH, Size cap, out uint dstW, out uint dstH)
    {
        dstW = srcW;
        dstH = srcH;
        if (cap.Width <= 0 || cap.Height <= 0 || srcW == 0 || srcH == 0)
            return;
        double sx = cap.Width / srcW;
        double sy = cap.Height / srcH;
        double s = Math.Min(sx, sy);
        if (s >= 1.0) return; // never upscale
        dstW = Math.Max(1u, (uint)Math.Round(srcW * s));
        dstH = Math.Max(1u, (uint)Math.Round(srcH * s));
    }

    // -----------------------------------------------------------------
    //  Composition graphics device (system) backed by a shared D2D device.
    // -----------------------------------------------------------------
    private static CompositionGraphicsDevice GetGraphicsDevice(Compositor compositor)
    {
        if (_graphicsDevices.TryGetValue(compositor, out var existing))
            return (CompositionGraphicsDevice)existing;

        lock (Gate)
        {
            if (_graphicsDevices.TryGetValue(compositor, out existing))
                return (CompositionGraphicsDevice)existing;

            IntPtr d2dDevice = GetD2DDevice();
            var device = CreateSystemGraphicsDevice(compositor, d2dDevice);
            _graphicsDevices.Add(compositor, device);
            return device;
        }
    }

    private static IntPtr GetD2DDevice()
    {
        if (_d2dDevice != IntPtr.Zero) return _d2dDevice;

        IntPtr d3dDevice = IntPtr.Zero, dxgiDevice = IntPtr.Zero;
        try
        {
            int hr = D3D11CreateDevice(
                IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                out d3dDevice, IntPtr.Zero, IntPtr.Zero);
            if (hr < 0)
            {
                // Fall back to WARP (no GPU / RDP / headless CI).
                hr = D3D11CreateDevice(
                    IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
                    out d3dDevice, IntPtr.Zero, IntPtr.Zero);
            }
            CheckHr(hr, "D3D11CreateDevice");

            CheckHr(Marshal.QueryInterface(d3dDevice, ref Unsafe.AsRef(in IID_IDXGIDevice), out dxgiDevice),
                "QI(IDXGIDevice)");

            CheckHr(D2D1CreateDevice(dxgiDevice, IntPtr.Zero, out IntPtr d2dDevice), "D2D1CreateDevice");
            _d2dDevice = d2dDevice;
            return d2dDevice;
        }
        finally
        {
            if (dxgiDevice != IntPtr.Zero) Marshal.Release(dxgiDevice);
            if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
        }
    }

    private static CompositionGraphicsDevice CreateSystemGraphicsDevice(
        Compositor compositor, IntPtr d2dDevice)
    {
        IntPtr pCompositor = MarshalInspectable<Compositor>.FromManaged(compositor);
        IntPtr pInterop = IntPtr.Zero;
        try
        {
            CheckHr(Marshal.QueryInterface(pCompositor, ref Unsafe.AsRef(in IID_ICompositorInterop), out pInterop),
                "QI(ICompositorInterop)");

            // ICompositorInterop::CreateGraphicsDevice (slot 5):
            //   (renderingDevice(IUnknown*), out CompositionGraphicsDevice*)
            var createGraphicsDevice =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(void***)pInterop)[5];
            IntPtr abi;
            CheckHr(createGraphicsDevice(pInterop, d2dDevice, &abi),
                "ICompositorInterop::CreateGraphicsDevice");
            try
            {
                return MarshalInterface<CompositionGraphicsDevice>.FromAbi(abi);
            }
            finally
            {
                Marshal.Release(abi);
            }
        }
        finally
        {
            if (pInterop != IntPtr.Zero) Marshal.Release(pInterop);
            if (pCompositor != IntPtr.Zero) Marshal.Release(pCompositor);
        }
    }

    // -----------------------------------------------------------------
    //  Draw the WIC bitmap into the drawing surface via ID2D1DeviceContext.
    // -----------------------------------------------------------------
    private static void DrawWicSourceIntoSurface(
        CompositionDrawingSurface surface, IntPtr wicSource, uint w, uint h)
    {
        IntPtr pSurface = MarshalInspectable<CompositionDrawingSurface>.FromManaged(surface);
        IntPtr pSurfInterop = IntPtr.Zero, pDc = IntPtr.Zero, pBitmap = IntPtr.Zero;
        bool begun = false;
        try
        {
            CheckHr(Marshal.QueryInterface(pSurface,
                ref Unsafe.AsRef(in IID_ICompositionDrawingSurfaceInterop), out pSurfInterop),
                "QI(ICompositionDrawingSurfaceInterop)");

            RECT update = new() { left = 0, top = 0, right = (int)w, bottom = (int)h };
            POINT offset;
            Guid iidDc = IID_ID2D1DeviceContext;

            // ICompositionDrawingSurfaceInterop::BeginDraw (slot 3)
            var beginDraw =
                (delegate* unmanaged[Stdcall]<IntPtr, RECT*, Guid*, IntPtr*, POINT*, int>)(*(void***)pSurfInterop)[3];
            CheckHr(beginDraw(pSurfInterop, &update, &iidDc, &pDc, &offset),
                "ICompositionDrawingSurfaceInterop::BeginDraw");
            begun = true;

            // ID2D1DeviceContext::CreateBitmapFromWicBitmap (slot 5):
            //   (IWICBitmapSource*, D2D1_BITMAP_PROPERTIES1* (null), out ID2D1Bitmap1*)
            var createBmp =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)(*(void***)pDc)[5];
            CheckHr(createBmp(pDc, wicSource, IntPtr.Zero, &pBitmap),
                "ID2D1DeviceContext::CreateBitmapFromWicBitmap");

            // ID2D1RenderTarget::DrawBitmap (slot 26):
            //   (ID2D1Bitmap*, D2D1_RECT_F* dest, float opacity, int interpolation, D2D1_RECT_F* src)
            var drawBitmap =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, float, int, float*, void>)(*(void***)pDc)[26];
            float* dest = stackalloc float[4] { offset.x, offset.y, offset.x + w, offset.y + h };
            drawBitmap(pDc, pBitmap, dest, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, null);
        }
        finally
        {
            if (pBitmap != IntPtr.Zero) Marshal.Release(pBitmap);
            if (begun && pSurfInterop != IntPtr.Zero)
            {
                // ICompositionDrawingSurfaceInterop::EndDraw (slot 4)
                var endDraw = (delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)pSurfInterop)[4];
                endDraw(pSurfInterop);
            }
            if (pDc != IntPtr.Zero) Marshal.Release(pDc);
            if (pSurfInterop != IntPtr.Zero) Marshal.Release(pSurfInterop);
            if (pSurface != IntPtr.Zero) Marshal.Release(pSurface);
        }
    }

    private static void CheckHr(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }
}
#endif
