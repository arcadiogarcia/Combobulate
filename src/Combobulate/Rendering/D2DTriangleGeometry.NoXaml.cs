#if COMBOBULATE_NO_XAML
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.UI.Composition;
using WinRT;

namespace Combobulate.Rendering;

/// <summary>
/// Win2D-free construction of the unit right-triangle
/// <c>(0,0)→(1,0)→(0,1)</c> as a system <see cref="Windows.UI.Composition.CompositionPath"/>,
/// for the XAML-free (<c>COMBOBULATE_NO_XAML</c>) build that has no
/// <c>Microsoft.Graphics.Canvas</c> (Win2D) available.
///
/// <para><b>How it works.</b> <see cref="Windows.UI.Composition.CompositionPath"/>'s
/// constructor accepts a <see cref="Windows.Graphics.IGeometrySource2D"/>. When
/// Composition consumes it, it QIs the object for the native COM interface
/// <c>IGeometrySource2DInterop</c> (IID <c>{0657AF73-53FD-47CF-84FF-C8492D2A80A3}</c>)
/// and calls <c>GetGeometry(out ID2D1Geometry)</c> to obtain a Direct2D geometry.
/// So we:</para>
/// <list type="number">
///   <item>Build a real <c>ID2D1PathGeometry</c> for the unit triangle via raw
///     Direct2D interop (P/Invoke <c>d2d1.dll!D2D1CreateFactory</c> then unmanaged
///     function-pointer calls through the object's COM vtables).</item>
///   <item>Wrap it in a hand-rolled multi-interface COM object that answers QI for
///     <c>IUnknown</c>, <c>IInspectable</c>, <c>Windows.Graphics.IGeometrySource2D</c>
///     and <c>IGeometrySource2DInterop</c>, the latter returning the D2D geometry.</item>
///   <item>Project it to a managed <see cref="Windows.Graphics.IGeometrySource2D"/>
///     via CsWinRT (<see cref="MarshalInterface{T}.FromAbi"/>) and hand it to
///     <c>new CompositionPath(source)</c>.</item>
/// </list>
///
/// <para>The unit triangle is a constant, so the built
/// <see cref="Windows.UI.Composition.CompositionPath"/> (and the underlying D2D
/// factory + geometry + COM object) are cached in statics and shared across every
/// per-compositor <c>CompositionPathGeometry</c>.</para>
/// </summary>
internal static unsafe class D2DTriangleGeometry
{
    // ---- IIDs ----
    private static readonly Guid IID_IUnknown =
        new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IInspectable =
        new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid IID_IGeometrySource2D =
        new("CAFF7902-670C-4181-A624-DA977203B845");
    private static readonly Guid IID_IGeometrySource2DInterop =
        new("0657AF73-53FD-47CF-84FF-C8492D2A80A3");
    private static readonly Guid IID_ID2D1Factory =
        new("06152247-6F50-465A-9245-118BFD3B6007");

    // D2D1_FACTORY_TYPE_SINGLE_THREADED = 0
    private const int D2D1_FACTORY_TYPE_SINGLE_THREADED = 0;
    // D2D1_FIGURE_BEGIN_FILLED = 0, D2D1_FIGURE_END_CLOSED = 1
    private const uint D2D1_FIGURE_BEGIN_FILLED = 0;
    private const uint D2D1_FIGURE_END_CLOSED = 1;

    private const int S_OK = 0;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    // D2D1_POINT_2F { FLOAT x; FLOAT y; }. Passing this as a real by-value struct
    // (rather than a hand-packed 8-byte integer) is REQUIRED for cross-architecture
    // correctness: the CLR then applies the platform ABI for the struct-by-value
    // argument. On x64 an 8-byte struct is passed in one integer register, but on
    // ARM64 (AAPCS64) a two-float struct is a homogeneous floating-point aggregate
    // (HFA) passed in the SIMD registers s0/s1 — packing it into a long would place
    // it in an integer register, so D2D would read garbage coordinates and the unit
    // triangle would collapse to an empty path (masking every triangle sprite away).
    [StructLayout(LayoutKind.Sequential)]
    private struct D2D_POINT_2F
    {
        public float X;
        public float Y;
        public D2D_POINT_2F(float x, float y) { X = x; Y = y; }
    }

    [DllImport("d2d1.dll", ExactSpelling = true)]
    private static extern int D2D1CreateFactory(
        int factoryType, in Guid riid, IntPtr pFactoryOptions, out IntPtr ppIFactory);

    private static readonly object Gate = new();
    private static IntPtr _d2dFactory;             // ID2D1Factory*
    private static CompositionPath? _cachedPath;   // shared unit-triangle path

    // Shared COM vtables (allocated once, never freed).
    private static IntPtr* _vtblInspectable;       // 6 slots: IInspectable
    private static IntPtr* _vtblInterop;           // 5 slots: IGeometrySource2DInterop

    // Object layout (native, IntPtr-sized slots):
    //   [0] -> _vtblInspectable   (this pointer for IInspectable / IGeometrySource2D)
    //   [1] -> _vtblInterop       (this pointer for IGeometrySource2DInterop)
    //   [2] -> refCount (int in low bytes)
    //   [3] -> ID2D1PathGeometry* (owned)
    private static readonly int SlotSize = IntPtr.Size;

    /// <summary>Returns the shared unit-triangle <see cref="CompositionPath"/>,
    /// building it (and the backing D2D geometry) on first call.</summary>
    public static CompositionPath GetOrCreateUnitTriangleCompositionPath()
    {
        lock (Gate)
        {
            if (_cachedPath is not null) return _cachedPath;

            EnsureVtables();

            IntPtr d2dGeometry = CreateUnitTriangleD2DGeometry(); // refcount 1 (owned by COM obj)
            IntPtr comObj = CreateGeometrySource(d2dGeometry);   // IGeometrySource2D*, refcount 1

            // Project to a managed IGeometrySource2D. FromAbi AddRefs (refcount -> 2);
            // release our raw ref so the projected object holds the sole remaining ref.
            var source = MarshalInterface<Windows.Graphics.IGeometrySource2D>.FromAbi(comObj);
            Marshal.Release(comObj);

            _cachedPath = new CompositionPath(source);
            return _cachedPath;
        }
    }

    // ---------------------------------------------------------------------
    //  Direct2D: build ID2D1PathGeometry for the unit right triangle.
    // ---------------------------------------------------------------------
    private static IntPtr GetD2DFactory()
    {
        if (_d2dFactory != IntPtr.Zero) return _d2dFactory;
        int hr = D2D1CreateFactory(
            D2D1_FACTORY_TYPE_SINGLE_THREADED, in IID_ID2D1Factory, IntPtr.Zero, out IntPtr factory);
        CheckHr(hr, "D2D1CreateFactory");
        _d2dFactory = factory;
        return factory;
    }

    private static IntPtr CreateUnitTriangleD2DGeometry()
    {
        IntPtr factory = GetD2DFactory();

        // ID2D1Factory::CreatePathGeometry  (vtable slot 10)
        var createPathGeometry =
            (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)factory)[10];
        IntPtr pathGeo;
        CheckHr(createPathGeometry(factory, &pathGeo), "ID2D1Factory::CreatePathGeometry");

        // ID2D1PathGeometry::Open  (vtable slot 17)
        var open = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)pathGeo)[17];
        IntPtr sink;
        CheckHr(open(pathGeo, &sink), "ID2D1PathGeometry::Open");

        // ID2D1SimplifiedGeometrySink::BeginFigure (slot 5), EndFigure (slot 8), Close (slot 9)
        // ID2D1GeometrySink::AddLine (slot 10).
        // D2D1_POINT_2F is passed by value as a real struct so the platform ABI is
        // applied correctly on both x64 (integer register) and ARM64 (HFA in s0/s1).
        var beginFigure = (delegate* unmanaged[Stdcall]<IntPtr, D2D_POINT_2F, uint, void>)(*(void***)sink)[5];
        var addLine     = (delegate* unmanaged[Stdcall]<IntPtr, D2D_POINT_2F, void>)(*(void***)sink)[10];
        var endFigure   = (delegate* unmanaged[Stdcall]<IntPtr, uint, void>)(*(void***)sink)[8];
        var close       = (delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)sink)[9];

        beginFigure(sink, new D2D_POINT_2F(0f, 0f), D2D1_FIGURE_BEGIN_FILLED);
        addLine(sink, new D2D_POINT_2F(1f, 0f));
        addLine(sink, new D2D_POINT_2F(0f, 1f));
        endFigure(sink, D2D1_FIGURE_END_CLOSED);
        CheckHr(close(sink), "ID2D1GeometrySink::Close");

        Marshal.Release(sink);
        return pathGeo; // refcount 1
    }

    // ---------------------------------------------------------------------
    //  Hand-rolled multi-interface COM object.
    // ---------------------------------------------------------------------
    private static IntPtr CreateGeometrySource(IntPtr d2dGeometry)
    {
        IntPtr obj = Marshal.AllocHGlobal(4 * SlotSize);
        IntPtr* slots = (IntPtr*)obj;
        slots[0] = (IntPtr)_vtblInspectable;
        slots[1] = (IntPtr)_vtblInterop;
        *(int*)((byte*)obj + 2 * SlotSize) = 1; // refcount
        slots[3] = d2dGeometry;                 // owns the ref
        return obj;
    }

    private static void EnsureVtables()
    {
        if (_vtblInspectable != null) return;

        _vtblInspectable = (IntPtr*)Marshal.AllocHGlobal(6 * SlotSize);
        _vtblInspectable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&Inspectable_QueryInterface;
        _vtblInspectable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Inspectable_AddRef;
        _vtblInspectable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Inspectable_Release;
        _vtblInspectable[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, Guid**, int>)&Inspectable_GetIids;
        _vtblInspectable[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&Inspectable_GetRuntimeClassName;
        _vtblInspectable[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&Inspectable_GetTrustLevel;

        _vtblInterop = (IntPtr*)Marshal.AllocHGlobal(5 * SlotSize);
        _vtblInterop[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&Interop_QueryInterface;
        _vtblInterop[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Interop_AddRef;
        _vtblInterop[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Interop_Release;
        _vtblInterop[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&Interop_GetGeometry;
        _vtblInterop[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)&Interop_TryGetGeometryUsingFactory;
    }

    // The IInspectable interface pointer == base; the interop interface pointer == base + SlotSize.
    private static int QueryInterface(nint basePtr, Guid* riid, IntPtr* ppv)
    {
        Guid r = *riid;
        if (r == IID_IUnknown || r == IID_IInspectable || r == IID_IGeometrySource2D)
        {
            *ppv = (IntPtr)basePtr;
            AddRefBase(basePtr);
            return S_OK;
        }
        if (r == IID_IGeometrySource2DInterop)
        {
            *ppv = (IntPtr)(basePtr + SlotSize);
            AddRefBase(basePtr);
            return S_OK;
        }
        *ppv = IntPtr.Zero;
        return E_NOINTERFACE;
    }

    private static int AddRefBase(nint basePtr)
        => Interlocked.Increment(ref *(int*)(basePtr + 2 * SlotSize));

    private static int ReleaseBase(nint basePtr)
    {
        int c = Interlocked.Decrement(ref *(int*)(basePtr + 2 * SlotSize));
        if (c == 0)
        {
            IntPtr geom = *(IntPtr*)(basePtr + 3 * SlotSize);
            if (geom != IntPtr.Zero) Marshal.Release(geom);
            Marshal.FreeHGlobal((IntPtr)basePtr);
        }
        return c;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Inspectable_QueryInterface(IntPtr pThis, Guid* riid, IntPtr* ppv)
        => QueryInterface((nint)pThis, riid, ppv);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Inspectable_AddRef(IntPtr pThis)
        => (uint)AddRefBase((nint)pThis);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Inspectable_Release(IntPtr pThis)
        => (uint)ReleaseBase((nint)pThis);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Inspectable_GetIids(IntPtr pThis, int* iidCount, Guid** iids)
    {
        *iidCount = 0;
        *iids = null;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Inspectable_GetRuntimeClassName(IntPtr pThis, IntPtr* className)
    {
        // A null HSTRING is the valid WinRT representation of the empty string.
        *className = IntPtr.Zero;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Inspectable_GetTrustLevel(IntPtr pThis, int* trustLevel)
    {
        *trustLevel = 0; // BaseTrust
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Interop_QueryInterface(IntPtr pThis, Guid* riid, IntPtr* ppv)
        => QueryInterface((nint)pThis - SlotSize, riid, ppv);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Interop_AddRef(IntPtr pThis)
        => (uint)AddRefBase((nint)pThis - SlotSize);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Interop_Release(IntPtr pThis)
        => (uint)ReleaseBase((nint)pThis - SlotSize);

    // IGeometrySource2DInterop::GetGeometry(out ID2D1Geometry) — slot 3.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Interop_GetGeometry(IntPtr pThis, IntPtr* ppGeometry)
    {
        nint basePtr = (nint)pThis - SlotSize;
        IntPtr geom = *(IntPtr*)(basePtr + 3 * SlotSize);
        Marshal.AddRef(geom); // [out] interface pointers are AddRef'd by the callee
        *ppGeometry = geom;
        return S_OK;
    }

    // IGeometrySource2DInterop::TryGetGeometryUsingFactory — slot 4.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int Interop_TryGetGeometryUsingFactory(IntPtr pThis, IntPtr factory, IntPtr* ppGeometry)
    {
        *ppGeometry = IntPtr.Zero;
        return E_NOTIMPL;
    }

    private static void CheckHr(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }
}
#endif
