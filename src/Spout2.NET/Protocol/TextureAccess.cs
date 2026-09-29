using Spout2.NET.Direct3D;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;

namespace Spout2.NET.Protocol;

// How taking Spout's lock around a shared texture went.
internal enum AccessResult
{
    // Taken.
    Held,

    // Taken from a process that died holding it.
    Abandoned,

    // The other side held it for the whole timeout.
    Busy,
}

// Spout's lock around a shared texture (spoutFrameCount::CheckTextureAccess): the texture's own
// keyed mutex when it was created with one, otherwise the named mutex "<sender>_SpoutAccessMutex"
// that sender and receivers open or create. Either way the wait is 67 ms, and a timeout means the
// other side is still using the texture.
internal sealed unsafe class TextureAccess : IDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMilliseconds(67);

    private readonly Mutex? _mutex;
    private readonly ComPtr<IDXGIKeyedMutex>? _keyed;

    private TextureAccess(Mutex? mutex, ComPtr<IDXGIKeyedMutex>? keyed)
    {
        _mutex = mutex;
        _keyed = keyed;
    }

    public static TextureAccess For(string sender, ComPtr<ID3D11Texture2D> texture)
    {
        D3D11_TEXTURE2D_DESC description = SharedTextures.Describe(texture.Pointer);
        return description.MiscFlags.HasFlag(
            D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX
        )
            ? new(null, texture.As<IDXGIKeyedMutex>())
            : new(new Mutex(false, SpoutName.AccessMutex(sender)), null);
    }

    // Must be released on the thread that entered: a Win32 mutex is owned by a thread.
    public AccessResult TryEnter()
    {
        if (_keyed is not null)
        {
            HRESULT result = _keyed.Pointer->AcquireSync(0, (uint)s_timeout.TotalMilliseconds);

            // WAIT_TIMEOUT and WAIT_ABANDONED are success codes for this method; only S_OK is the lock.
            return result.Value == 0 ? AccessResult.Held : AccessResult.Busy;
        }

        try
        {
            return _mutex!.WaitOne(s_timeout) ? AccessResult.Held : AccessResult.Busy;
        }
        catch (AbandonedMutexException)
        {
            // The other side died holding the lock; the wait transferred it to us, and the texture is
            // whatever that side last wrote. The caller logs it.
            return AccessResult.Abandoned;
        }
    }

    public void Exit()
    {
        if (_keyed is not null)
        {
            _keyed.Pointer->ReleaseSync(0);
        }
        else
        {
            _mutex!.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        _mutex?.Dispose();
        _keyed?.Dispose();
    }
}
