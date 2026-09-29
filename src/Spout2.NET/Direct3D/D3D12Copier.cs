using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;

namespace Spout2.NET.Direct3D;

// Copies between Direct3D 12 resources on the application's device and queue: Spout's shared texture,
// opened from its DXGI shared handle, and the application's own. Each copy is recorded with explicit
// transitions, submitted to the application's queue behind its own work, and waited for on a fence
// of the copier's before it returns: Spout's protocol has no GPU fence, only its lock, so a copy must
// be finished while the lock is held.
internal sealed unsafe class D3D12Copier : IDisposable
{
    private readonly Lock _gate = new();
    private readonly ComPtr<ID3D12Device> _device;
    private readonly ComPtr<ID3D12CommandQueue> _queue;
    private readonly ComPtr<ID3D12CommandAllocator> _allocator;
    private readonly ComPtr<ID3D12GraphicsCommandList> _list;
    private readonly ComPtr<ID3D12Fence> _fence;
    private readonly AutoResetEvent _completed = new(false);
    private ulong _submitted;

    public D3D12Copier(ID3D12Device* device, ID3D12CommandQueue* queue)
    {
        _device = ComPtr<ID3D12Device>.AddRef(device);
        _queue = ComPtr<ID3D12CommandQueue>.AddRef(queue);
        try
        {
            Guid iid = ID3D12CommandAllocator.IID_Guid;
            void* allocator;
            device->CreateCommandAllocator(
                D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
                &iid,
                &allocator
            );
            _allocator = ComPtr<ID3D12CommandAllocator>.Attach((ID3D12CommandAllocator*)allocator);

            iid = ID3D12GraphicsCommandList.IID_Guid;
            void* list;
            device->CreateCommandList(
                0,
                D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
                _allocator.Pointer,
                null,
                &iid,
                &list
            );
            _list = ComPtr<ID3D12GraphicsCommandList>.Attach((ID3D12GraphicsCommandList*)list);
            _list.Pointer->Close();

            iid = ID3D12Fence.IID_Guid;
            void* fence;
            device->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, &iid, &fence);
            _fence = ComPtr<ID3D12Fence>.Attach((ID3D12Fence*)fence);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ID3D12Device* Device => _device.Pointer;

    // Spout's shared texture as a resource of the application's device.
    public ComPtr<ID3D12Resource> Open(uint shareHandle)
    {
        Guid iid = ID3D12Resource.IID_Guid;
        void* resource;
        _device.Pointer->OpenSharedHandle(new HANDLE((void*)(nint)shareHandle), &iid, &resource);
        return ComPtr<ID3D12Resource>.Attach((ID3D12Resource*)resource);
    }

    // Copies source into destination and returns when the GPU has finished; both are left in the
    // states they were handed over in.
    public void Copy(
        ID3D12Resource* destination,
        D3D12_RESOURCE_STATES destinationState,
        ID3D12Resource* source,
        D3D12_RESOURCE_STATES sourceState
    )
    {
        lock (_gate)
        {
            _allocator.Pointer->Reset();
            _list.Pointer->Reset(_allocator.Pointer, null);
            D3D12_RESOURCE_BARRIER* barriers = stackalloc D3D12_RESOURCE_BARRIER[2];
            barriers[0] = Transition(
                source,
                sourceState,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE
            );
            barriers[1] = Transition(
                destination,
                destinationState,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST
            );
            _list.Pointer->ResourceBarrier(2, barriers);
            _list.Pointer->CopyResource(destination, source);
            barriers[0] = Transition(
                source,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE,
                sourceState
            );
            barriers[1] = Transition(
                destination,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST,
                destinationState
            );
            _list.Pointer->ResourceBarrier(2, barriers);
            _list.Pointer->Close();

            ID3D12CommandList* submitted = (ID3D12CommandList*)_list.Pointer;
            _queue.Pointer->ExecuteCommandLists(1, &submitted);
            Wait(Signal());
        }
    }

    // Called holding the lock.
    private ulong Signal()
    {
        ulong value = ++_submitted;
        _queue.Pointer->Signal(_fence.Pointer, value);
        return value;
    }

    // Called holding the lock.
    private void Wait(ulong value)
    {
        if (_fence.Pointer->GetCompletedValue() < value)
        {
            _fence.Pointer->SetEventOnCompletion(
                value,
                new HANDLE((void*)_completed.SafeWaitHandle.DangerousGetHandle())
            );
            _ = _completed.WaitOne();
        }
    }

    // Returns when the GPU has finished everything submitted to the application's queue so far: the
    // application's own work on Spout's texture before Spout's lock is released.
    public void Drain()
    {
        lock (_gate)
        {
            Wait(Signal());
        }
    }

    // The size and format of an application texture, which must be a single 2D texture.
    public (int Width, int Height, SpoutFormat Format) Describe(D3D12Texture texture)
    {
        if (texture.Resource == 0)
        {
            throw new ArgumentNullException(nameof(texture));
        }

        D3D12_RESOURCE_DESC description = ((ID3D12Resource*)texture.Resource)->GetDesc();
        return
            description.Dimension != D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D
            || description.DepthOrArraySize != 1
            || description.MipLevels != 1
            || description.SampleDesc.Count != 1
            ? throw new ArgumentException(
                $"The resource is a {description.Dimension} of {description.DepthOrArraySize} slices, {description.MipLevels} mip levels and {description.SampleDesc.Count} samples; Spout shares single 2D textures.",
                nameof(texture)
            )
            : ((int)description.Width, (int)description.Height, (SpoutFormat)description.Format);
    }

    public void Dispose()
    {
        _list?.Dispose();
        _allocator?.Dispose();
        _fence?.Dispose();
        _queue.Dispose();
        _device.Dispose();
        _completed.Dispose();
    }

    private static D3D12_RESOURCE_BARRIER Transition(
        ID3D12Resource* resource,
        D3D12_RESOURCE_STATES before,
        D3D12_RESOURCE_STATES after
    )
    {
        D3D12_RESOURCE_BARRIER barrier = new()
        {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };
        barrier.Anonymous.Transition.pResource = resource;
        barrier.Anonymous.Transition.Subresource = Win32.D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Anonymous.Transition.StateBefore = before;
        barrier.Anonymous.Transition.StateAfter = after;
        return barrier;
    }
}
