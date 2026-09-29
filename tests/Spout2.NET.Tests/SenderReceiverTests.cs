using Spout2.NET.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace Spout2.NET.Tests;

[TestClass]
public sealed class SenderReceiverTests
{
    private const int Width = 64;
    private const int Height = 48;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Send_ThenReceive_DeliversTheTextureByteExact()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("send");
        using SpoutSender sender = new(name, device);
        using (ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height))
        {
            Assert.IsTrue(sender.Send(texture.D3D11()));
        }

        Assert.IsTrue(sender.IsPublished);
        Assert.AreEqual(1, sender.FrameNumber);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            Assert.AreEqual(Width, frame.Width);
            Assert.AreEqual(Height, frame.Height);
            Assert.AreEqual(SpoutFormat.Bgra8Unorm, frame.Format);
            Assert.AreEqual(name, frame.Sender.Name);
            Assert.IsTrue(frame.IsNew);
            Assert.IsTrue(frame.SenderChanged);
            Assert.AreEqual(1, frame.FrameNumber);
            CollectionAssert.AreEqual(
                Gpu.Pattern(1, Width, Height),
                Gpu.Read(device, frame.Texture.NativePointer)
            );
        }

        Assert.IsTrue(receiver.IsConnected);
        Assert.AreEqual(name, receiver.Sender?.Name);
    }

    [TestMethod]
    public void Receive_TellsNewFramesFromRepeats()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("repeat");
        using SpoutSender sender = new(name, device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Assert.IsTrue(sender.Send(texture.D3D11()));
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Receive(receiver, frame => Assert.IsTrue(frame.IsNew));

        Assert.IsFalse(receiver.HasNewFrame);
        Receive(
            receiver,
            frame =>
            {
                Assert.IsFalse(frame.IsNew);
                Assert.IsFalse(frame.SenderChanged);
            }
        );

        Assert.IsTrue(sender.Send(texture.D3D11()));
        Assert.IsTrue(receiver.HasNewFrame);
        Receive(
            receiver,
            frame =>
            {
                Assert.IsTrue(frame.IsNew);
                Assert.AreEqual(2, frame.FrameNumber);
            }
        );
    }

    [TestMethod]
    public void Sender_IsRegisteredWithItsFirstFrame_AndLeavesWhenDisposed()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("registry");
        SpoutSender sender = new(name, device);
        Assert.IsFalse(SpoutSenders.TryGet(name, out _), "Registered before its first frame.");
        using (ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height))
        {
            Assert.IsTrue(sender.Send(texture.D3D11()));
        }

        Assert.IsTrue(SpoutSenders.TryGet(name, out SpoutSenderInfo info));
        Assert.AreEqual(Width, info.Width);
        Assert.AreEqual(Height, info.Height);
        Assert.AreEqual(SpoutFormat.Bgra8Unorm, info.Format);
        Assert.AreNotEqual(0u, info.ShareHandle);
        Assert.AreEqual(Environment.ProcessPath, info.ExecutablePath);
        Assert.IsFalse(info.SharesCpuMemory);
        Assert.Contains(info, SpoutSenders.GetAll());
        Assert.AreEqual(name, SpoutSenders.Active, "A new sender becomes the active sender.");

        sender.Dispose();
        Assert.IsFalse(SpoutSenders.TryGet(name, out _));
        Assert.DoesNotContain(name, SpoutSenders.GetAll().Select(static s => s.Name));
    }

    [TestMethod]
    public void Senders_AreListedInTheRegistrysOrder()
    {
        using SpoutDevice device = Gpu.Device();
        string prefix = Gpu.UniqueName("order");
        using SpoutSender b = new(prefix + " b", device);
        using SpoutSender a = new(prefix + " a", device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Assert.IsTrue(b.Send(texture.D3D11()));
        Assert.IsTrue(a.Send(texture.D3D11()));

        string[] ours =
        [
            .. SpoutSenders
                .GetAll()
                .Select(static s => s.Name)
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)),
        ];
        CollectionAssert.AreEqual(new[] { prefix + " a", prefix + " b" }, ours);
        Assert.IsTrue(SpoutSenders.TrySetActive(b.Name));
        Assert.AreEqual(b.Name, SpoutSenders.Active);
        Assert.IsFalse(SpoutSenders.TrySetActive(prefix + " none"));
    }

    [TestMethod]
    public void Sender_WithANameInUse_IsRefused()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("taken");
        using SpoutSender first = new(name, device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Assert.IsTrue(first.Send(texture.D3D11()));

        _ = Assert.ThrowsExactly<SpoutException>(() => new SpoutSender(name, device));
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            new SpoutSender(new string('x', 256), device)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() => new SpoutSender(string.Empty, device));
    }

    [TestMethod]
    public void Resize_ReachesTheReceiverAsASenderChange()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("resize");
        using SpoutSender sender = new(name, device);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        using (ComPtr<ID3D11Texture2D> small = Gpu.Filled(device, 1, Width, Height))
        {
            Assert.IsTrue(sender.Send(small.D3D11()));
        }

        Receive(receiver, static frame => Assert.AreEqual(64, frame.Width));
        using (ComPtr<ID3D11Texture2D> large = Gpu.Filled(device, 2, 128, 72))
        {
            Assert.IsTrue(sender.Send(large.D3D11()));
        }

        Receive(
            receiver,
            frame =>
            {
                Assert.IsTrue(frame.SenderChanged);
                Assert.AreEqual(128, frame.Width);
                Assert.AreEqual(72, frame.Height);
                CollectionAssert.AreEqual(
                    Gpu.Pattern(2, 128, 72),
                    Gpu.Read(device, frame.Texture.NativePointer)
                );
            }
        );
    }

    [TestMethod]
    public void TryBeginFrame_RendersIntoTheSharedTextureDirectly()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("zero-copy");
        using SpoutSender sender = new(name, device);
        Assert.IsTrue(
            sender.TryBeginFrame(Width, Height, SpoutFormat.Bgra8Unorm, out SpoutSenderFrame frame)
        );
        using (frame)
        {
            Assert.AreEqual(sender.SharedTexture, frame.Texture);
            Gpu.Upload(device, frame.Texture.NativePointer, Gpu.Pattern(7, Width, Height), Width);
            frame.Publish();
        }

        Assert.AreEqual(1, sender.FrameNumber);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Receive(
            receiver,
            received =>
                CollectionAssert.AreEqual(
                    Gpu.Pattern(7, Width, Height),
                    Gpu.Read(device, received.Texture.NativePointer)
                )
        );

        // A frame disposed unpublished is discarded: the count and the content stay.
        Assert.IsTrue(
            sender.TryBeginFrame(
                Width,
                Height,
                SpoutFormat.Bgra8Unorm,
                out SpoutSenderFrame discarded
            )
        );
        using (discarded)
        {
            Gpu.Upload(
                device,
                discarded.Texture.NativePointer,
                Gpu.Pattern(8, Width, Height),
                Width
            );
        }

        Assert.AreEqual(1, sender.FrameNumber);
        Assert.IsFalse(receiver.HasNewFrame);
    }

    [TestMethod]
    public void AnOpenFrame_KeepsReceiversOut_UntilItIsPublished()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("busy");
        using SpoutSender sender = new(name, device);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.IsTrue(
            sender.TryBeginFrame(Width, Height, SpoutFormat.Bgra8Unorm, out SpoutSenderFrame frame)
        );
        using (frame)
        {
            // Spout's lock is a mutex owned by a thread (and reentrant for it), so the competing
            // receiver runs on another thread while this one keeps the frame open.
            SpoutReceiveResult whileOpen = default;
            Thread other = new(() =>
            {
                whileOpen = receiver.TryReceive(out SpoutFrame received);
                received.Dispose();
            });
            other.Start();
            other.Join();
            Assert.AreEqual(SpoutReceiveResult.Busy, whileOpen);
            frame.Publish();
        }

        Receive(receiver, static received => Assert.IsTrue(received.IsNew));
    }

    // Runtime-async code may carry a ref struct across an await, onto another thread; the frame
    // refuses to end there instead of failing inside the Win32 mutex.
    [TestMethod]
    public void AFrameEndedOnAnotherThread_IsRefused()
    {
        using SpoutDevice device = Gpu.Device();
        using SpoutSender sender = new(Gpu.UniqueName("thread"), device);
        Assert.IsTrue(
            sender.TryBeginFrame(Width, Height, SpoutFormat.Bgra8Unorm, out SpoutSenderFrame frame)
        );
        using (frame)
        {
            Exception? error = null;
            Thread other = new(() =>
            {
                try
                {
                    sender.EndFrame(publish: true);
                }
                catch (InvalidOperationException caught)
                {
                    error = caught;
                }
            });
            other.Start();
            other.Join();
            Assert.IsInstanceOfType<InvalidOperationException>(error);
            frame.Publish();
        }

        Assert.AreEqual(1, sender.FrameNumber);
    }

    [TestMethod]
    public void Retain_KeepsTheFrameAfterTheSenderMovesOn()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("retain");
        using SpoutSender sender = new(name, device);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        using ComPtr<ID3D11Texture2D> first = Gpu.Filled(device, 1, Width, Height);
        using ComPtr<ID3D11Texture2D> second = Gpu.Filled(device, 2, Width, Height);
        Assert.IsTrue(sender.Send(first.D3D11()));

        SpoutFrameLease? lease = null;
        Receive(receiver, frame => lease = frame.Retain());
        using (lease)
        {
            Assert.IsTrue(sender.Send(second.D3D11()));
            Receive(
                receiver,
                frame =>
                    CollectionAssert.AreEqual(
                        Gpu.Pattern(2, Width, Height),
                        Gpu.Read(device, frame.Texture.NativePointer)
                    )
            );
            Assert.AreEqual(1, lease!.FrameNumber);
            CollectionAssert.AreEqual(
                Gpu.Pattern(1, Width, Height),
                Gpu.Read(device, lease.Texture.NativePointer)
            );
        }

        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => lease.Texture);
    }

    [TestMethod]
    public void CopyTo_CopiesTheFrameIntoTheApplicationsTexture()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("copy");
        using SpoutSender sender = new(name, device);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 3, Width, Height);
        Assert.IsTrue(sender.Send(texture.D3D11()));
        using ComPtr<ID3D11Texture2D> target = Gpu.CreateTexture(device, Width, Height);
        using ComPtr<ID3D11Texture2D> wrongSize = Gpu.CreateTexture(device, 32, 32);

        Receive(
            receiver,
            frame =>
            {
                frame.CopyTo(target.D3D11());
                try
                {
                    frame.CopyTo(wrongSize.D3D11());
                    Assert.Fail("A destination of another size was accepted.");
                }
                catch (ArgumentException)
                {
                    // Expected: a ref struct cannot be captured by Assert.Throws.
                }
            }
        );
        CollectionAssert.AreEqual(Gpu.Pattern(3, Width, Height), Gpu.Read(device, target.Address));
    }

    [TestMethod]
    public void Metadata_ReachesTheReceiver()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("meta");
        using SpoutSender sender = new(name, device);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => sender.WriteMetadata("early"u8));
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Assert.IsTrue(sender.Send(texture.D3D11()));
        sender.WriteMetadata("hello from Spout2.NET"u8);
        _ = Assert.ThrowsExactly<ArgumentException>(() => sender.WriteMetadata(new byte[4097]));

        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.AreEqual(0, receiver.ReadMetadata(new byte[16]), "Read before connecting.");
        Receive(receiver, static _ => { });
        byte[] buffer = new byte[8192];
        int read = receiver.ReadMetadata(buffer);
        Assert.AreEqual(4096, read, "Spout's buffer has no length; the whole buffer is read.");
        CollectionAssert.AreEqual("hello from Spout2.NET\0"u8.ToArray(), buffer[..22]);
    }

    [TestMethod]
    public void Receive_WithoutASender_ReportsNoSender()
    {
        using SpoutDevice device = Gpu.Device();
        using SpoutReceiver receiver = new(device, new() { SenderName = Gpu.UniqueName("absent") });
        Assert.AreEqual(SpoutReceiveResult.NoSender, receiver.TryReceive(out SpoutFrame frame));
        frame.Dispose();
        Assert.IsFalse(receiver.IsConnected);
        Assert.IsTrue(receiver.HasNewFrame);
    }

    // Spout shares the latest frame: frames published while the handler is busy coalesce, as they do
    // for every Spout receiver. What RunAsync guarantees is no repeats, publish order, and the last one.
    [TestMethod]
    public async Task RunAsync_DeliversNewFramesInOrderWithoutRepeats()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("run");
        using SpoutSender sender = new(name, device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.CreateTexture(device, Width, Height);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken
        );
        List<uint> delivered = [];
        TaskCompletionSource enough = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = receiver.RunAsync(
            (in SpoutFrame frame) =>
            {
                uint index = Gpu.FrameIndex(Gpu.Read(device, frame.Texture.NativePointer));
                delivered.Add(index);
                if (index == 10)
                {
                    enough.TrySetResult();
                }
            },
            stop.Token
        );

        for (uint i = 1; i <= 10; i++)
        {
            Gpu.Upload(device, texture.Address, Gpu.Pattern(i, Width, Height), Width);
            Assert.IsTrue(sender.Send(texture.D3D11()));
            await Task.Delay(20, TestContext.CancellationToken);
        }

        await enough.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        await stop.CancelAsync();
        await run;
        CollectionAssert.AllItemsAreUnique(delivered);
        CollectionAssert.AreEqual(delivered.Order().ToList(), delivered);
        Assert.AreEqual(10u, delivered[^1]);
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            receiver.RunAsync(null!, TestContext.CancellationToken)
        );
    }

    [TestMethod]
    public void Receive_OnAnotherGpu_ReportsTheAdapter()
    {
        long[] gpus =
        [
            .. SpoutDevice
                .GetAdapters()
                .Where(static a => !a.IsSoftware)
                .Select(static a => a.Luid),
        ];
        if (gpus.Length < 2)
        {
            Assert.Inconclusive("This machine has one GPU.");
        }

        using SpoutDevice first = SpoutDevice.Create(gpus[0]);
        using SpoutDevice second = SpoutDevice.Create(gpus[1]);
        Assert.AreEqual(gpus[1], second.AdapterLuid);
        string name = Gpu.UniqueName("adapter");
        using SpoutSender sender = new(name, first);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(first, 1, Width, Height);
        Assert.IsTrue(sender.Send(texture.D3D11()));

        using SpoutReceiver receiver = new(second, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.OtherAdapter, receiver.TryReceive(out SpoutFrame frame));
        frame.Dispose();

        // The sender's GPU is found by trying each; a receiver there opens the texture.
        Assert.IsTrue(SpoutSenders.TryGet(name, out SpoutSenderInfo info));
        using SpoutDevice found = SpoutDevice.CreateFor(info);
        Assert.AreEqual(gpus[0], found.AdapterLuid);
        using SpoutReceiver onItsGpu = new(found, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, onItsGpu.TryReceive(out SpoutFrame received));
        received.Dispose();
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            SpoutDevice.Create(0x7FFF_FFFF_0000_0001)
        );
    }

    [TestMethod]
    public void Send_OfAnotherDevicesTexture_IsRefused()
    {
        using SpoutDevice device = Gpu.Device();
        using SpoutDevice other = Gpu.Device();
        using SpoutSender sender = new(Gpu.UniqueName("foreign"), device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(other, 1, Width, Height);
        _ = Assert.ThrowsExactly<ArgumentException>(() => sender.Send(texture.D3D11()));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => sender.Send(new D3D11Texture(0)));
        Assert.IsFalse(sender.IsPublished);
    }

    [TestMethod]
    public void FromD3D11Device_SharesTheApplicationsDevice()
    {
        using SpoutDevice created = Gpu.Device();
        using SpoutDevice wrapped = SpoutDevice.FromD3D11Device(created.NativePointer);
        Assert.AreEqual(created.NativePointer, wrapped.NativePointer);
        Assert.AreEqual(created.AdapterLuid, wrapped.AdapterLuid);
        Assert.IsFalse(string.IsNullOrEmpty(wrapped.AdapterName));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => SpoutDevice.FromD3D11Device(0));
    }

    // Receives one frame and hands it to a check; the frame is released afterwards.
    private static void Receive(SpoutReceiver receiver, Action<SpoutFrame> check)
    {
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            check(frame);
        }
    }
}
