using System.Runtime.InteropServices;

namespace Spout2.NET.Interop;

/// <summary>
/// P/Invoke surface over the native shim (<c>spout_shim.dll</c>), whose flat C ABI is declared in
/// <c>native/include/spout_shim.h</c>. DirectX objects cross the boundary as opaque pointers.
/// </summary>
internal static partial class SpoutNative
{
    internal const string Lib = "spout_shim";

    [LibraryImport(Lib)]
    internal static partial nint sp_create();

    [LibraryImport(Lib)]
    internal static partial void sp_destroy(nint s);

    [LibraryImport(Lib)]
    internal static partial int sp_open_directx11(nint s, nint device);

    [LibraryImport(Lib)]
    internal static partial nint sp_get_device(nint s);

    // ---- Sender ----

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sp_set_sender_name(nint s, string name);

    [LibraryImport(Lib)]
    internal static partial void sp_set_sender_format(nint s, uint format);

    [LibraryImport(Lib)]
    internal static partial int sp_send_texture(nint s, nint texture);

    [LibraryImport(Lib)]
    internal static partial void sp_release_sender(nint s);

    // ---- Receiver ----

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void sp_set_receiver_name(nint s, string? name);

    [LibraryImport(Lib)]
    internal static partial int sp_receive_texture(nint s, out nint texture);

    [LibraryImport(Lib)]
    internal static partial int sp_is_updated(nint s);

    [LibraryImport(Lib)]
    internal static partial int sp_is_connected(nint s);

    [LibraryImport(Lib)]
    internal static partial int sp_is_frame_new(nint s);

    [LibraryImport(Lib)]
    internal static partial uint sp_get_sender_width(nint s);

    [LibraryImport(Lib)]
    internal static partial uint sp_get_sender_height(nint s);

    [LibraryImport(Lib)]
    internal static partial void sp_release_receiver(nint s);

    // ---- Discovery ----

    [LibraryImport(Lib)]
    internal static partial int sp_get_sender_count(nint s);

    [LibraryImport(Lib)]
    internal static partial int sp_get_sender(nint s, int index, byte[] name, int maxSize);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sp_get_sender_info(nint s, string name, out uint width, out uint height, out nint shareHandle, out uint format);
}
