using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D11on12;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Spout2.NET.Direct3D;

// Direct3D 12 resources reach Spout's Direct3D 11 shared textures through Direct3D 11 on 12
// (spoutDX12): an application resource is wrapped as a Direct3D 11 resource on the 11-on-12 device,
// acquired, copied to or from the shared texture, released, and the work flushed to the application's
// queue. D3D12 cannot open Spout's legacy shared handles itself, so this is the path every Spout
// application interoperates on.
internal sealed unsafe class D3D12Bridge(ComPtr<ID3D11On12Device> device) : IDisposable
{
    // Wrapping is costly and applications cycle a few resources (a swap chain's, a pool's), so the
    // last few wrappers are kept. A wrapper holds a reference on its resource until evicted.
    private const int CachedWrappers = 8;

    private readonly Lock _gate = new();
    private readonly LinkedList<(D3D12Texture Texture, ComPtr<ID3D11Resource> Wrapper)> _wrappers =
    [];

    // Copies an application resource into a Direct3D 11 texture on the 11-on-12 device.
    public void CopyFrom(SpoutDevice spout, ID3D11Texture2D* destination, D3D12Texture source) =>
        Use(
            spout,
            source,
            wrapped =>
                spout.Context->CopyResource((ID3D11Resource*)destination, (ID3D11Resource*)wrapped)
        );

    // Copies a Direct3D 11 texture on the 11-on-12 device into an application resource.
    public void CopyTo(SpoutDevice spout, D3D12Texture destination, ID3D11Texture2D* source) =>
        Use(
            spout,
            destination,
            wrapped =>
                spout.Context->CopyResource((ID3D11Resource*)wrapped, (ID3D11Resource*)source)
        );

    public D3D11_TEXTURE2D_DESC Describe(D3D12Texture texture)
    {
        lock (_gate)
        {
            using ComPtr<ID3D11Texture2D> as2D =
                Wrapper(texture).As<ID3D11Texture2D>()
                ?? throw new ArgumentException(
                    "The resource is not a 2D texture.",
                    nameof(texture)
                );
            return SharedTextures.Describe(as2D.Pointer);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach ((_, ComPtr<ID3D11Resource> wrapper) in _wrappers)
            {
                wrapper.Dispose();
            }

            _wrappers.Clear();
        }

        device.Dispose();
    }

    private void Use(SpoutDevice spout, D3D12Texture texture, Action<nint> copy)
    {
        lock (_gate)
        {
            ID3D11Resource* wrapped = Wrapper(texture).Pointer;
            device.Pointer->AcquireWrappedResources(&wrapped, 1);
            try
            {
                copy((nint)wrapped);
            }
            finally
            {
                // Releasing transitions the resource back to its state and submits the Direct3D 11
                // work to the Direct3D 12 queue on the flush.
                device.Pointer->ReleaseWrappedResources(&wrapped, 1);
                spout.Context->Flush();
            }
        }
    }

    // Called holding the lock.
    private ComPtr<ID3D11Resource> Wrapper(D3D12Texture texture)
    {
        if (texture.Resource == 0)
        {
            throw new ArgumentNullException(nameof(texture));
        }

        for (
            LinkedListNode<(D3D12Texture Texture, ComPtr<ID3D11Resource> Wrapper)>? node =
                _wrappers.First;
            node is not null;
            node = node.Next
        )
        {
            if (node.Value.Texture == texture)
            {
                _wrappers.Remove(node);
                _wrappers.AddFirst(node);
                return node.Value.Wrapper;
            }
        }

        D3D11_RESOURCE_FLAGS flags = default;
        Guid iid = ID3D11Resource.IID_Guid;
        void* wrapped;
        device.Pointer->CreateWrappedResource(
            (IUnknown*)texture.Resource,
            &flags,
            (D3D12_RESOURCE_STATES)texture.State,
            (D3D12_RESOURCE_STATES)texture.State,
            &iid,
            &wrapped
        );
        ComPtr<ID3D11Resource> wrapper = ComPtr<ID3D11Resource>.Attach((ID3D11Resource*)wrapped);
        _ = _wrappers.AddFirst((texture, wrapper));
        if (_wrappers.Count > CachedWrappers)
        {
            _wrappers.Last!.Value.Wrapper.Dispose();
            _wrappers.RemoveLast();
        }

        return wrapper;
    }
}
