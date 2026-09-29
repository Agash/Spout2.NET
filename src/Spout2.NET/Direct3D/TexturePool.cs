using System.Collections.Concurrent;
using Windows.Win32.Graphics.Direct3D11;

namespace Spout2.NET.Direct3D;

// Receiver-owned textures for retained frames, reused while the sender's size and format stay the
// same. A lease may outlive the receiver, so a return after disposal releases the texture instead.
internal sealed class TexturePool(SpoutDevice device) : IDisposable
{
    // Retained frames usually sit in a short queue (an encoder's), so a few per size suffice.
    private const int MaxPooled = 8;

    private readonly ConcurrentBag<ComPtr<ID3D11Texture2D>> _free = [];
    private (int Width, int Height, SpoutFormat Format) _shape;
    private readonly Lock _gate = new();
    private bool _disposed;

    public ComPtr<ID3D11Texture2D> Rent(int width, int height, SpoutFormat format)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shape != (width, height, format))
            {
                Drain();
                _shape = (width, height, format);
            }
            else if (_free.TryTake(out ComPtr<ID3D11Texture2D>? texture))
            {
                return texture;
            }
        }

        // Copies a Direct3D 12 application reads are opened there by their DXGI shared handle.
        return device.Api == SpoutGraphicsApi.Direct3D12
            ? SharedTextures.CreateShared(device, width, height, format, out _)
            : SharedTextures.CreateCopyTarget(device, width, height, format);
    }

    public void Return(ComPtr<ID3D11Texture2D> texture, int width, int height, SpoutFormat format)
    {
        lock (_gate)
        {
            if (!_disposed && _shape == (width, height, format) && _free.Count < MaxPooled)
            {
                _free.Add(texture);
                return;
            }
        }

        texture.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Drain();
        }
    }

    private void Drain()
    {
        while (_free.TryTake(out ComPtr<ID3D11Texture2D>? texture))
        {
            texture.Dispose();
        }
    }
}
