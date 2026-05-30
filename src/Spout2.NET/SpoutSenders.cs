using System.Text;
using Spout2.NET.Interop;

namespace Spout2.NET;

/// <summary>Describes a Spout sender currently advertised on the machine.</summary>
/// <param name="Name">The sender name.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="ShareHandle">The DXGI shared texture handle.</param>
/// <param name="Format">The texture format.</param>
public readonly record struct SpoutSenderInfo(string Name, int Width, int Height, nint ShareHandle, DxgiFormat Format);

/// <summary>
/// Enumerates the Spout senders currently advertised on the machine (a process-global registry).
/// </summary>
public sealed class SpoutSenders : IDisposable
{
    private const int NameBufferSize = 256;
    private nint _handle;

    /// <summary>Open a handle to the sender registry.</summary>
    public SpoutSenders()
    {
        _handle = SpoutNative.sp_create();
        if (_handle == 0)
            throw new InvalidOperationException("Failed to open the Spout sender registry.");
    }

    /// <summary>Number of senders currently advertised.</summary>
    public int Count
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return SpoutNative.sp_get_sender_count(_handle);
        }
    }

    /// <summary>Snapshot the names of the currently advertised senders.</summary>
    public IReadOnlyList<string> Names()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        int count = SpoutNative.sp_get_sender_count(_handle);
        var list = new List<string>(count);
        byte[] buffer = new byte[NameBufferSize];
        for (int i = 0; i < count; i++)
        {
            if (SpoutNative.sp_get_sender(_handle, i, buffer, NameBufferSize) != 0)
                list.Add(Decode(buffer));
        }
        return list;
    }

    /// <summary>Look up the dimensions, share handle, and format of a named sender.</summary>
    public bool TryGetInfo(string name, out SpoutSenderInfo info)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (SpoutNative.sp_get_sender_info(_handle, name, out uint w, out uint h, out nint handle, out uint format) != 0)
        {
            info = new SpoutSenderInfo(name, (int)w, (int)h, handle, (DxgiFormat)format);
            return true;
        }
        info = default;
        return false;
    }

    private static string Decode(byte[] buffer)
    {
        int end = Array.IndexOf<byte>(buffer, 0);
        if (end < 0) end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) SpoutNative.sp_destroy(h);
    }
}
