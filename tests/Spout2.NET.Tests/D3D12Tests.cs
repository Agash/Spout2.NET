using Spout2.NET.Direct3D;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Spout2.NET.Tests;

/// <summary>
/// Direct3D 12 applications sharing through Spout: their resources reach Spout's Direct3D 11 shared
/// textures through Direct3D 11 on 12, and must interoperate with the Spout SDK's Direct3D 11 peers.
/// </summary>
[TestClass]
public sealed class D3D12Tests
{
    private const int Width = 80;
    private const int Height = 45;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void D3D12Sender_IsReceivedByAD3D11Receiver()
    {
        using D3D12Context d3d12 = D3D12Context.Create();
        using SpoutDevice sending = d3d12.SpoutDevice();
        Assert.AreEqual(SpoutGraphicsApi.Direct3D12, sending.Api);
        Assert.AreEqual(d3d12.AdapterLuid, sending.AdapterLuid);

        using ComPtr<ID3D12Resource> texture = d3d12.CreateTexture(Width, Height);
        D3D12Texture resource = new(texture.Address);
        Fill(sending, resource, 3);

        string name = Gpu.UniqueName("d3d12 send");
        using SpoutSender sender = new(name, sending);
        Assert.IsTrue(sender.Send(resource));
        Assert.AreEqual(Width, sender.Width);
        Assert.AreEqual(SpoutFormat.Bgra8Unorm, sender.Format);
        d3d12.WaitForIdle();

        using SpoutDevice receiving = SpoutDevice.Create(d3d12.AdapterLuid);
        using SpoutReceiver receiver = new(receiving, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            CollectionAssert.AreEqual(
                Gpu.Pattern(3, Width, Height),
                Gpu.Read(receiving, frame.Texture.NativePointer)
            );
        }
    }

    [TestMethod]
    public void D3D11Sender_IsCopiedIntoAD3D12Texture()
    {
        using D3D12Context d3d12 = D3D12Context.Create();
        using SpoutDevice sending = SpoutDevice.Create(d3d12.AdapterLuid);
        string name = Gpu.UniqueName("d3d12 receive");
        using SpoutSender sender = new(name, sending);
        using (ComPtr<ID3D11Texture2D> source = Gpu.Filled(sending, 5, Width, Height))
        {
            Assert.IsTrue(sender.Send(source.D3D11()));
        }

        using SpoutDevice receiving = d3d12.SpoutDevice();
        using ComPtr<ID3D12Resource> target = d3d12.CreateTexture(Width, Height);
        D3D12Texture resource = new(target.Address);
        using SpoutReceiver receiver = new(receiving, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            frame.CopyTo(resource);
            try
            {
                frame.CopyTo(new D3D11Texture(0));
                Assert.Fail("A null Direct3D 11 destination was accepted.");
            }
            catch (ArgumentNullException)
            {
                // Expected: a ref struct cannot be captured by Assert.Throws.
            }
        }

        CollectionAssert.AreEqual(Gpu.Pattern(5, Width, Height), Read(receiving, resource));
    }

    [TestMethod]
    public void KeptFrame_IsCopiedIntoAD3D12Texture_AfterTheBorrow()
    {
        using D3D12Context d3d12 = D3D12Context.Create();
        using SpoutDevice sending = SpoutDevice.Create(d3d12.AdapterLuid);
        string name = Gpu.UniqueName("d3d12 lease");
        using SpoutSender sender = new(name, sending);
        using (ComPtr<ID3D11Texture2D> source = Gpu.Filled(sending, 9, Width, Height))
        {
            Assert.IsTrue(sender.Send(source.D3D11()));
        }

        using SpoutDevice receiving = d3d12.SpoutDevice();
        using SpoutReceiver receiver = new(receiving, new() { SenderName = name });
        SpoutFrameLease lease;
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            lease = frame.Retain();
        }

        // The sender moves on; the kept frame does not.
        using (ComPtr<ID3D11Texture2D> source = Gpu.Filled(sending, 10, Width, Height))
        {
            Assert.IsTrue(sender.Send(source.D3D11()));
        }

        using ComPtr<ID3D12Resource> target = d3d12.CreateTexture(Width, Height);
        D3D12Texture resource = new(target.Address);
        using (lease)
        {
            lease.CopyTo(resource);
            _ = Assert.ThrowsExactly<ArgumentException>(() => lease.CopyTo(new OpenGLTexture(1)));
        }

        CollectionAssert.AreEqual(Gpu.Pattern(9, Width, Height), Read(receiving, resource));
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => lease.CopyTo(resource));
    }

    [TestMethod]
    public void D3D12Device_RefusesTheOtherApisTextures_AndRenderingIntoTheSharedTexture()
    {
        using D3D12Context d3d12 = D3D12Context.Create();
        using SpoutDevice device = d3d12.SpoutDevice();
        using SpoutSender sender = new(Gpu.UniqueName("d3d12 refuse"), device);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            sender.TryBeginFrame(Width, Height, SpoutFormat.Bgra8Unorm, out _)
        );

        using SpoutDevice d3d11 = SpoutDevice.Create(d3d12.AdapterLuid);
        using SpoutSender other = new(Gpu.UniqueName("d3d11 refuse"), d3d11);
        using ComPtr<ID3D12Resource> texture = d3d12.CreateTexture(Width, Height);
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            other.Send(new D3D12Texture(texture.Address))
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            SpoutDevice.FromD3D12Device(0, d3d12.Queue.Address)
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            SpoutDevice.FromD3D12Device(d3d12.Device.Address, 0)
        );
    }

    [TestMethod]
    public void D3D12Sender_CyclingResources_ReusesTheirWrappers()
    {
        using D3D12Context d3d12 = D3D12Context.Create();
        using SpoutDevice device = d3d12.SpoutDevice();
        using SpoutSender sender = new(Gpu.UniqueName("d3d12 cycle"), device);
        List<ComPtr<ID3D12Resource>> textures =
        [
            .. Enumerable.Range(0, 10).Select(_ => d3d12.CreateTexture(Width, Height)),
        ];
        try
        {
            // More resources than the wrapper cache holds, twice round: evicted wrappers are recreated.
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < textures.Count; i++)
                {
                    D3D12Texture texture = new(textures[i].Address);
                    Fill(device, texture, (uint)((round * 10) + i + 1));
                    Assert.IsTrue(sender.Send(texture));
                }
            }

            Assert.AreEqual(20, sender.FrameNumber);
            using SpoutReceiver receiver = new(device, new() { SenderName = sender.Name });
            Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
            using (frame)
            {
                CollectionAssert.AreEqual(
                    Gpu.Pattern(20, Width, Height),
                    Gpu.Read(device, frame.Texture.NativePointer)
                );
            }
        }
        finally
        {
            foreach (ComPtr<ID3D12Resource> texture in textures)
            {
                texture.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task D3D12Sender_IsReceivedByTheSdk()
    {
        SpoutPeer.Require();
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using D3D12Context d3d12 = D3D12Context.Create();
        using SpoutDevice device = d3d12.SpoutDevice();
        using ComPtr<ID3D12Resource> texture = d3d12.CreateTexture(Width, Height);
        D3D12Texture resource = new(texture.Address);
        string name = Gpu.UniqueName("d3d12 to sdk");
        using SpoutSender sender = new(name, device);
        Fill(device, resource, 1);
        Assert.IsTrue(sender.Send(resource));

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        Task sending = Task.Run(
            async () =>
            {
                using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(16));
                uint frame = 1;
                try
                {
                    while (await timer.WaitForNextTickAsync(stop.Token))
                    {
                        Fill(device, resource, ++frame);
                        _ = sender.Send(resource);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Deliberately ignored: cancellation is how the sending ends.
                }
            },
            CancellationToken.None
        );

        PeerReceipt receipt;
        try
        {
            receipt = await SpoutPeer.ReceiveAsync(
                name,
                6,
                TimeSpan.FromSeconds(10),
                cancellationToken
            );
        }
        finally
        {
            await stop.CancelAsync();
            await sending;
        }

        Assert.AreEqual(0, receipt.ExitCode, receipt.Output);
        List<PeerFrame> drawn = [.. receipt.Frames.Where(static f => f.Index != 0)];
        Assert.IsGreaterThanOrEqualTo(2, drawn.Count);
        foreach (PeerFrame frame in drawn)
        {
            Assert.AreEqual(
                Gpu.Fnv1a(Gpu.Pattern(frame.Index, Width, Height)),
                frame.Hash,
                $"frame {frame.Index}"
            );
        }
    }

    [TestMethod]
    public async Task SdkSender_IsCopiedIntoAD3D12Texture()
    {
        SpoutPeer.Require();
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string name = Gpu.UniqueName("sdk to d3d12");
        await using SpoutPeer peer = await SpoutPeer.StartSenderAsync(
            name,
            Width,
            Height,
            cancellationToken
        );
        Assert.IsTrue(SpoutSenders.TryGet(name, out SpoutSenderInfo info));

        using D3D12Context d3d12 = D3D12Context.Create();
        using SpoutDevice device = d3d12.SpoutDevice();
        using ComPtr<ID3D12Resource> target = d3d12.CreateTexture(Width, Height);
        D3D12Texture resource = new(target.Address);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        int checkedFrames = 0;
        for (int attempt = 0; attempt < 200 && checkedFrames < 5; attempt++)
        {
            if (receiver.TryReceive(out SpoutFrame frame) == SpoutReceiveResult.Received)
            {
                using (frame)
                {
                    if (frame.IsNew)
                    {
                        frame.CopyTo(resource);
                        checkedFrames++;
                    }
                }

                byte[] pixels = Read(device, resource);
                uint index = Gpu.FrameIndex(pixels);
                CollectionAssert.AreEqual(
                    Gpu.Pattern(index, Width, Height),
                    pixels,
                    $"frame {index}"
                );
            }

            await Task.Delay(10, cancellationToken);
        }

        Assert.AreEqual(5, checkedFrames);
        Assert.AreEqual(Width, info.Width);
    }

    // Fills a D3D12 texture with a frame's pattern: uploaded into a Direct3D 11 texture on the device's
    // 11-on-12 device, then copied across.
    private static unsafe void Fill(SpoutDevice device, D3D12Texture texture, uint frame)
    {
        using ComPtr<ID3D11Texture2D> staging = Gpu.Filled(device, frame, Width, Height);
        device.D3D12!.CopyTo(device, texture, staging.Pointer);
    }

    private static unsafe byte[] Read(SpoutDevice device, D3D12Texture texture)
    {
        using ComPtr<ID3D11Texture2D> copy = Gpu.CreateTexture(device, Width, Height);
        device.D3D12!.CopyFrom(device, copy.Pointer, texture);
        return Gpu.Read(device, copy.Address);
    }

    // A Direct3D 12 device and direct queue on the test adapter, as a Direct3D 12 application has them.
    private sealed unsafe class D3D12Context : IDisposable
    {
        private D3D12Context(
            ComPtr<ID3D12Device> device,
            ComPtr<ID3D12CommandQueue> queue,
            long luid
        )
        {
            Device = device;
            Queue = queue;
            AdapterLuid = luid;
        }

        public ComPtr<ID3D12Device> Device { get; }

        public ComPtr<ID3D12CommandQueue> Queue { get; }

        public long AdapterLuid { get; }

        public static D3D12Context Create()
        {
            using SpoutDevice probe = Gpu.Device();
            long luid = probe.AdapterLuid;
            Windows
                .Win32.Win32.CreateDXGIFactory1(
                    out Windows.Win32.Graphics.Dxgi.IDXGIFactory1* factory
                )
                .ThrowOnFailure();
            using ComPtr<Windows.Win32.Graphics.Dxgi.IDXGIFactory1> ownedFactory =
                ComPtr<Windows.Win32.Graphics.Dxgi.IDXGIFactory1>.Attach(factory);
            Windows.Win32.Graphics.Dxgi.IDXGIAdapter1* adapter = null;
            for (uint i = 0; factory->EnumAdapters1(i, &adapter).Succeeded; i++)
            {
                Windows.Win32.Graphics.Dxgi.DXGI_ADAPTER_DESC1 description = adapter->GetDesc1();
                if (
                    (
                        ((long)description.AdapterLuid.HighPart << 32)
                        | description.AdapterLuid.LowPart
                    ) == luid
                )
                {
                    break;
                }

                _ = adapter->Release();
                adapter = null;
            }

            using ComPtr<Windows.Win32.Graphics.Dxgi.IDXGIAdapter1> ownedAdapter =
                ComPtr<Windows.Win32.Graphics.Dxgi.IDXGIAdapter1>.Attach(adapter);
            Guid deviceIid = ID3D12Device.IID_Guid;
            void* device;
            Windows
                .Win32.Win32.D3D12CreateDevice(
                    (IUnknown*)adapter,
                    D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                    &deviceIid,
                    &device
                )
                .ThrowOnFailure();
            ComPtr<ID3D12Device> ownedDevice = ComPtr<ID3D12Device>.Attach((ID3D12Device*)device);
            D3D12_COMMAND_QUEUE_DESC queueDescription = new()
            {
                Type = D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT,
            };
            Guid queueIid = ID3D12CommandQueue.IID_Guid;
            void* queue;
            ownedDevice.Pointer->CreateCommandQueue(&queueDescription, &queueIid, &queue);
            return new(
                ownedDevice,
                ComPtr<ID3D12CommandQueue>.Attach((ID3D12CommandQueue*)queue),
                luid
            );
        }

        public SpoutDevice SpoutDevice() =>
            Spout2.NET.SpoutDevice.FromD3D12Device(Device.Address, Queue.Address);

        public ComPtr<ID3D12Resource> CreateTexture(int width, int height)
        {
            D3D12_HEAP_PROPERTIES heap = new() { Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT };
            D3D12_RESOURCE_DESC description = new()
            {
                Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
                Width = (ulong)width,
                Height = (uint)height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new() { Count = 1 },
                Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN,
                Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
            };
            Guid iid = ID3D12Resource.IID_Guid;
            void* resource;
            Device.Pointer->CreateCommittedResource(
                &heap,
                D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                &description,
                D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
                null,
                &iid,
                &resource
            );
            return ComPtr<ID3D12Resource>.Attach((ID3D12Resource*)resource);
        }

        // The copies were submitted to the queue; a signal waited on from the CPU confirms they ran.
        public void WaitForIdle()
        {
            Guid iid = ID3D12Fence.IID_Guid;
            void* fence;
            Device.Pointer->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE, &iid, &fence);
            using ComPtr<ID3D12Fence> owned = ComPtr<ID3D12Fence>.Attach((ID3D12Fence*)fence);
            Queue.Pointer->Signal(owned.Pointer, 1);
            using EventWaitHandle done = new(false, EventResetMode.ManualReset);
            owned.Pointer->SetEventOnCompletion(
                1,
                new Windows.Win32.Foundation.HANDLE(done.SafeWaitHandle.DangerousGetHandle())
            );
            _ = done.WaitOne(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            Queue.Dispose();
            Device.Dispose();
        }
    }
}
