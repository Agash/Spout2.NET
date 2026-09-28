using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Spout2.NET.Direct3D;
using Spout2.NET.Protocol;
using Windows.Win32.Graphics.Direct3D11;

namespace Spout2.NET.Tests;

/// <summary>How frames and sender changes reach an application over time.</summary>
[TestClass]
public sealed class DeliveryTests
{
    private const int Width = 32;
    private const int Height = 16;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task WatchAsync_ReportsSendersAsTheyComeAndGo()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("watched");
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        await using IAsyncEnumerator<ImmutableArray<SpoutSenderInfo>> changes = SpoutSenders
            .WatchAsync(TimeSpan.FromMilliseconds(10), stop.Token)
            .GetAsyncEnumerator(stop.Token);

        Assert.IsTrue(await changes.MoveNextAsync(), "The first result is the current set.");
        Assert.DoesNotContain(name, changes.Current.Select(static s => s.Name));

        SpoutSender sender = new(name, device);
        using (ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height))
        {
            Assert.IsTrue(sender.Send(texture.Address));
        }

        await WaitForAsync(changes, set => set.Any(s => s.Name == name));
        sender.Dispose();
        await WaitForAsync(changes, set => set.All(s => s.Name != name));
        await stop.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await changes.MoveNextAsync()
        );
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            SpoutSenders.WatchAsync(TimeSpan.Zero, cancellationToken)
        );
    }

    [TestMethod]
    public async Task RunAsync_OnFrameSync_WakesOnTheSendersSignal()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("sync");
        using SpoutSender sender = new(name, device, new() { SignalFrameSync = true });
        using ComPtr<ID3D11Texture2D> texture = Gpu.CreateTexture(device, Width, Height);
        Gpu.Upload(device, texture.Address, Gpu.Pattern(1, Width, Height), Width);
        Assert.IsTrue(sender.Send(texture.Address));

        using SpoutReceiver receiver = new(
            device,
            new() { SenderName = name, WaitForFrameSync = true }
        );
        List<uint> delivered = [];
        TaskCompletionSource last = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        Task run = receiver.RunAsync(
            (in SpoutFrame frame) =>
            {
                uint index = Gpu.FrameIndex(Gpu.Read(device, frame.Texture));
                delivered.Add(index);
                if (index == 5)
                {
                    last.TrySetResult();
                }
            },
            stop.Token
        );
        for (uint i = 2; i <= 5; i++)
        {
            await Task.Delay(30, cancellationToken);
            Gpu.Upload(device, texture.Address, Gpu.Pattern(i, Width, Height), Width);
            Assert.IsTrue(sender.Send(texture.Address));
        }

        await last.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await stop.CancelAsync();
        await run;
        CollectionAssert.AreEqual(delivered.Order().ToList(), delivered);
        CollectionAssert.AllItemsAreUnique(delivered);
    }

    // A sender that does not count its frames: every read looks new, so delivery is paced instead.
    [TestMethod]
    public async Task RunAsync_OfAnUncountedSender_PacesDelivery()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SpoutDevice device = Gpu.Device();
        using ComPtr<ID3D11Texture2D> texture = SharedTextures.CreateShared(
            device,
            Width,
            Height,
            SpoutFormat.Bgra8Unorm,
            out uint handle
        );
        string name = Gpu.UniqueName("uncounted");
        using SharedMemory info = SharedMemory.CreateOrOpen(name, SharedTextureInfo.Size);
        using (SharedMemory.Lock held = info.Acquire())
        {
            SharedTextureInfo record = new()
            {
                ShareHandle = handle,
                Width = Width,
                Height = Height,
                Format = 87,
            };
            MemoryMarshal.Write(held.Bytes, in record);
        }

        SenderRegistry.Register(name);
        try
        {
            using SpoutReceiver receiver = new(
                device,
                new() { SenderName = name, UncountedFrameInterval = TimeSpan.FromMilliseconds(20) }
            );
            int deliveries = 0;
            long frameNumber = 0;
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            Task run = receiver.RunAsync(
                (in SpoutFrame frame) =>
                {
                    deliveries++;
                    frameNumber = frame.FrameNumber;
                },
                stop.Token
            );
            await Task.Delay(400, cancellationToken);
            await stop.CancelAsync();
            await run;

            Assert.AreEqual(-1, frameNumber, "An uncounted sender's frames carry no number.");
            Assert.IsInRange(5, 30, deliveries, "Paced at about 20 ms over 400 ms.");
        }
        finally
        {
            SenderRegistry.Release(name);
        }
    }

    [TestMethod]
    public async Task RunAsync_FaultsWithTheHandlersException_AndRunsOnlyOnce()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("fault");
        using SpoutSender sender = new(name, device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Assert.IsTrue(sender.Send(texture.Address));
        using SpoutReceiver receiver = new(device, new() { SenderName = name });

        Task run = receiver.RunAsync(
            static (in SpoutFrame _) => throw new InvalidDataException("handler"),
            cancellationToken
        );
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.AreEqual("handler", error.Message);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        Task again = receiver.RunAsync(static (in SpoutFrame _) => { }, stop.Token);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            receiver.RunAsync(static (in SpoutFrame _) => { }, stop.Token)
        );
        await stop.CancelAsync();
        await again;
    }

    [TestMethod]
    public void Retain_AfterAResize_UsesTexturesOfTheNewSize()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("pool");
        using SpoutSender sender = new(name, device);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        using ComPtr<ID3D11Texture2D> small = Gpu.Filled(device, 1, Width, Height);
        using ComPtr<ID3D11Texture2D> large = Gpu.Filled(device, 2, Width * 2, Height * 2);

        Assert.IsTrue(sender.Send(small.Address));
        SpoutFrameLease first = RetainOne(receiver);
        first.Dispose();
        SpoutFrameLease reused = RetainOne(receiver);
        Assert.AreEqual(Width, reused.Width);

        Assert.IsTrue(sender.Send(large.Address));
        using SpoutFrameLease resized = RetainOne(receiver);
        Assert.AreEqual(Width * 2, resized.Width);
        Assert.AreEqual(SpoutFormat.Bgra8Unorm, resized.Format);
        Assert.AreEqual(name, resized.Sender.Name);
        Assert.IsGreaterThan(0L, resized.ObservedAtNanoseconds);
        CollectionAssert.AreEqual(
            Gpu.Pattern(2, Width * 2, Height * 2),
            Gpu.Read(device, resized.Texture)
        );

        // A lease of the old size returns after the resize; it is released, not pooled.
        reused.Dispose();
        reused.Dispose();
    }

    [TestMethod]
    public void OpenFrame_DescribesTheSharedTexture_AndBlocksOtherSends()
    {
        using SpoutDevice device = Gpu.Device();
        using SpoutSender sender = new(Gpu.UniqueName("open"), device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Assert.IsTrue(
            sender.TryBeginFrame(Width, Height, SpoutFormat.Rgba8Unorm, out SpoutSenderFrame frame)
        );
        using (frame)
        {
            Assert.AreEqual(Width, frame.Width);
            Assert.AreEqual(Height, frame.Height);
            Assert.AreEqual(SpoutFormat.Rgba8Unorm, frame.Format);
            try
            {
                _ = sender.Send(texture.Address);
                Assert.Fail("A send was accepted while a frame was open.");
            }
            catch (InvalidOperationException)
            {
                // Expected: a ref struct cannot be captured by Assert.Throws.
            }
        }

        Assert.AreEqual(0, sender.FrameNumber);
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            sender.TryBeginFrame(0, Height, SpoutFormat.Bgra8Unorm, out _)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            sender.TryBeginFrame(Width, Height, SpoutFormat.Unknown, out _)
        );
        sender.Dispose();
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => sender.Send(texture.Address));
    }

    [TestMethod]
    public async Task Measures_ThePublishingRate()
    {
        using SpoutDevice device = Gpu.Device();
        using SpoutSender sender = new(Gpu.UniqueName("rate"), device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Stopwatch clock = Stopwatch.StartNew();
        for (int i = 0; i < 12; i++)
        {
            Assert.IsTrue(sender.Send(texture.Address));
            await Task.Delay(10, TestContext.CancellationToken);
        }

        Assert.AreEqual(12, sender.FrameNumber);
        Assert.IsInRange(
            20.0,
            120.0,
            sender.FramesPerSecond,
            $"measured over {clock.ElapsedMilliseconds} ms"
        );
    }

    // A Spout application that dies holding the access mutex leaves it abandoned; the next waiter
    // gets it, and the texture is whatever that application last wrote.
    [TestMethod]
    public void AnAbandonedAccessLock_IsTakenOver()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("abandoned");
        using SpoutSender sender = new(name, device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 4, Width, Height);
        Assert.IsTrue(sender.Send(texture.Address));

        Thread crashed = new(() => new Mutex(false, SpoutName.AccessMutex(name)).WaitOne());
        crashed.Start();
        crashed.Join();

        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            CollectionAssert.AreEqual(
                Gpu.Pattern(4, Width, Height),
                Gpu.Read(device, frame.Texture)
            );
        }
    }

    // A receiver holding the texture past Spout's 67 ms timeout makes the sender drop the frame.
    [TestMethod]
    public void AReceiverHoldingTheTexture_MakesTheSenderDropTheFrame()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("held");
        using SpoutSender sender = new(name, device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Assert.IsTrue(sender.Send(texture.Address));
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        using ManualResetEventSlim holding = new();
        using ManualResetEventSlim release = new();
        Thread reader = new(() =>
        {
            Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
            using (frame)
            {
                holding.Set();
                release.Wait();
            }
        });
        reader.Start();
        holding.Wait();
        try
        {
            Assert.IsFalse(sender.Send(texture.Address));
            Assert.IsFalse(sender.TryBeginFrame(Width, Height, SpoutFormat.Bgra8Unorm, out _));
            Assert.AreEqual(1, sender.FrameNumber);
        }
        finally
        {
            release.Set();
            reader.Join();
        }

        Assert.IsTrue(sender.Send(texture.Address));
    }

    [TestMethod]
    public async Task RunAsync_OnFrameSync_FallsBackToPolling_ForASenderThatDoesNotSignal()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("no sync");
        using SpoutSender sender = new(name, device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Assert.IsTrue(sender.Send(texture.Address));
        using SpoutReceiver receiver = new(
            device,
            new() { SenderName = name, WaitForFrameSync = true }
        );
        int delivered = 0;
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        Task run = receiver.RunAsync(
            (in SpoutFrame _) =>
            {
                if (Interlocked.Increment(ref delivered) == 2)
                {
                    second.TrySetResult();
                }
            },
            stop.Token
        );
        await Task.Delay(50, cancellationToken);
        Assert.IsTrue(sender.Send(texture.Address));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await stop.CancelAsync();
        await run;
    }

    [TestMethod]
    public void Receive_WhileAFrameIsBorrowed_IsRefused_AndDisposeIsIdempotent()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("borrowed");
        SpoutSender sender = new(name, device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.Filled(device, 1, Width, Height);
        Assert.IsTrue(sender.Send(texture.Address));
        SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            try
            {
                _ = receiver.TryReceive(out _);
                Assert.Fail("A second frame was received while one was borrowed.");
            }
            catch (InvalidOperationException)
            {
                // Expected: a ref struct cannot be captured by Assert.Throws.
            }
        }

        receiver.Dispose();
        receiver.Dispose();
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => receiver.TryReceive(out _));
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => receiver.ReadMetadata(new byte[1]));
        sender.Dispose();
        sender.Dispose();
    }

    private static SpoutFrameLease RetainOne(SpoutReceiver receiver)
    {
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            return frame.Retain();
        }
    }

    private static async Task WaitForAsync(
        IAsyncEnumerator<ImmutableArray<SpoutSenderInfo>> changes,
        Func<ImmutableArray<SpoutSenderInfo>, bool> condition
    )
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (await changes.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
        {
            if (condition(changes.Current))
            {
                return;
            }
        }

        Assert.Fail("The watch ended before the change.");
    }
}
