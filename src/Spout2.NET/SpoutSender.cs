using Spout2.NET.Interop;

namespace Spout2.NET;

/// <summary>
/// Publishes video frames for other applications to receive (for example an OBS Spout source).
/// Frames are shared zero-copy as DirectX 11 shared textures. Pass DirectX objects as their native
/// pointers (for example a Vortice <c>ID3D11Device.NativePointer</c> / <c>ID3D11Texture2D.NativePointer</c>).
/// </summary>
public sealed class SpoutSender : IDisposable
{
    private nint _handle;

    /// <summary>
    /// Create a sender advertised under <paramref name="name"/>, using <paramref name="d3d11Device"/>
    /// (an <c>ID3D11Device*</c>). Pass <see cref="nint.Zero"/> to let Spout create its own device,
    /// then read it back from <see cref="Device"/> to create compatible textures.
    /// </summary>
    public SpoutSender(string name, nint d3d11Device)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _handle = SpoutNative.sp_create();
        if (_handle == 0)
            throw new InvalidOperationException("Failed to create the Spout sender.");
        if (SpoutNative.sp_open_directx11(_handle, d3d11Device) == 0)
        {
            Dispose();
            throw new InvalidOperationException("Failed to open DirectX 11 for the Spout sender.");
        }
        if (SpoutNative.sp_set_sender_name(_handle, name) == 0)
        {
            Dispose();
            throw new InvalidOperationException($"Failed to create a sender named '{name}'.");
        }
        Name = name;
    }

    /// <summary>The advertised sender name.</summary>
    public string Name { get; }

    /// <summary>The <c>ID3D11Device*</c> in use (the one supplied, or the one Spout created).</summary>
    public nint Device
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return SpoutNative.sp_get_device(_handle);
        }
    }

    /// <summary>Set the shared texture format. The default is <see cref="DxgiFormat.B8G8R8A8UNorm"/>.</summary>
    public void SetFormat(DxgiFormat format)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        SpoutNative.sp_set_sender_format(_handle, (uint)format);
    }

    /// <summary>Publish a frame from a DirectX 11 texture (an <c>ID3D11Texture2D*</c>), zero-copy.</summary>
    public void Send(nint d3d11Texture)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (d3d11Texture == 0) throw new ArgumentException("Texture pointer is null.", nameof(d3d11Texture));
        if (SpoutNative.sp_send_texture(_handle, d3d11Texture) == 0)
            throw new InvalidOperationException("Failed to send the texture.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h == 0) return;
        SpoutNative.sp_release_sender(h);
        SpoutNative.sp_destroy(h);
    }
}
