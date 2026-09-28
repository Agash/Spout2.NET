using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;

namespace Spout2.NET.Direct3D;

// Owns one reference to a COM object, released exactly once when the handle is disposed or
// finalized. The COM interfaces come from CsWin32 as blittable structs, so the pointer is typed by
// the interface and the release goes through IUnknown.
internal sealed unsafe class ComPtr<T> : SafeHandle
    where T : unmanaged, IComIID
{
    public ComPtr()
        : base(0, ownsHandle: true) { }

    private ComPtr(T* pointer)
        : base(0, ownsHandle: true)
    {
        SetHandle((nint)pointer);
    }

    public override bool IsInvalid => handle == 0;

    public T* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            return (T*)handle;
        }
    }

    // The pointer as an address, for handing to code that takes COM pointers as nint.
    public nint Address
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsClosed, this);
            return handle;
        }
    }

    // Takes ownership of a reference the caller already holds.
    public static ComPtr<T> Attach(T* pointer) =>
        pointer is null ? throw new ArgumentNullException(nameof(pointer)) : new(pointer);

    // Takes a new reference to an object the caller does not own.
    public static ComPtr<T> AddRef(T* pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        _ = ((IUnknown*)pointer)->AddRef();
        return new(pointer);
    }

    // The same object through another of its interfaces, or null when it does not implement it.
    public ComPtr<TOther>? As<TOther>()
        where TOther : unmanaged, IComIID
    {
        void* other;
        Guid iid = TOther.Guid;
        return ((IUnknown*)Pointer)->QueryInterface(&iid, &other).Succeeded
            ? ComPtr<TOther>.Attach((TOther*)other)
            : null;
    }

    protected override bool ReleaseHandle()
    {
        _ = ((IUnknown*)handle)->Release();
        return true;
    }
}
