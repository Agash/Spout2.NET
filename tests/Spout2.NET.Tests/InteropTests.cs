using Spout2.NET.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace Spout2.NET.Tests;

/// <summary>
/// Spout2.NET against the upstream Spout SDK in another process (tests/SpoutPeer): each side sends
/// frames the other receives byte-exact, finds the other in the shared registry, and reads its
/// metadata buffer.
/// </summary>
[TestClass]
public sealed class InteropTests
{
    private const int Width = 96;
    private const int Height = 54;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void RequirePeer() => SpoutPeer.Require();

    [TestMethod]
    public async Task OurSender_IsReceivedByTheSdk()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("to sdk");
        using SpoutSender sender = new(name, device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.CreateTexture(device, Width, Height);
        Gpu.Upload(device, texture.Address, Gpu.Pattern(1, Width, Height), Width);
        Assert.IsTrue(sender.Send(texture.D3D11()));
        sender.WriteMetadata("hello from Spout2.NET"u8);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        Task sending = SendUntilAsync(device, sender, texture, stop.Token);
        PeerReceipt receipt;
        try
        {
            receipt = await SpoutPeer.ReceiveAsync(
                name,
                8,
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
        Assert.AreEqual("hello from Spout2.NET", receipt.Metadata);
        AssertFramesMatch(receipt.Frames);
    }

    [TestMethod]
    public async Task OurSender_IsTheSdksActiveSender()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SpoutDevice device = Gpu.Device();
        using SpoutSender sender = new(Gpu.UniqueName("active"), device);
        using ComPtr<ID3D11Texture2D> texture = Gpu.CreateTexture(device, Width, Height);
        Gpu.Upload(device, texture.Address, Gpu.Pattern(1, Width, Height), Width);
        Assert.IsTrue(sender.Send(texture.D3D11()));
        Assert.AreEqual(sender.Name, SpoutSenders.Active);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        Task sending = SendUntilAsync(device, sender, texture, stop.Token);
        PeerReceipt receipt;
        try
        {
            // No name: the SDK follows the active sender, which a new sender becomes.
            receipt = await SpoutPeer.ReceiveAsync(
                null,
                3,
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
        AssertFramesMatch(receipt.Frames);
    }

    [TestMethod]
    public async Task SdkSender_IsReceivedByUs()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string name = Gpu.UniqueName("from sdk");
        await using SpoutPeer peer = await SpoutPeer.StartSenderAsync(
            name,
            Width,
            Height,
            cancellationToken
        );

        Assert.IsTrue(SpoutSenders.TryGet(name, out SpoutSenderInfo info));
        Assert.AreEqual(Width, info.Width);
        Assert.AreEqual(Height, info.Height);
        Assert.AreEqual(SpoutFormat.Bgra8Unorm, info.Format);
        Assert.IsFalse(info.SharesCpuMemory);
        StringAssert.EndsWith(info.ExecutablePath, "spout_peer.exe");
        Assert.Contains(name, SpoutSenders.GetAll().Select(static s => s.Name));

        using SpoutDevice device = Gpu.Device();
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        List<uint> indices = [];
        long lastFrame = 0;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        TaskCompletionSource enough = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = receiver.RunAsync(
            (in SpoutFrame frame) =>
            {
                byte[] pixels = Gpu.Read(device, frame.Texture.NativePointer);
                uint index = Gpu.FrameIndex(pixels);
                CollectionAssert.AreEqual(
                    Gpu.Pattern(index, Width, Height),
                    pixels,
                    $"frame {index}"
                );
                Assert.IsGreaterThan(lastFrame, frame.FrameNumber);
                lastFrame = frame.FrameNumber;
                indices.Add(index);
                if (indices.Count == 8)
                {
                    enough.TrySetResult();
                }
            },
            stop.Token
        );
        try
        {
            await enough.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }

        CollectionAssert.AreEqual(
            indices.Order().ToList(),
            indices,
            "Frames arrived out of order."
        );
        byte[] metadata = new byte[64];
        Assert.IsGreaterThan(0, receiver.ReadMetadata(metadata));
        StringAssert.StartsWith(
            System.Text.Encoding.ASCII.GetString(metadata),
            "spout peer metadata\0"
        );
    }

    [TestMethod]
    public async Task SdkSender_LeavesTheRegistryWhenItStops()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string name = Gpu.UniqueName("sdk leaves");
        await using (
            SpoutPeer peer = await SpoutPeer.StartSenderAsync(
                name,
                Width,
                Height,
                cancellationToken
            )
        )
        {
            Assert.IsTrue(SpoutSenders.TryGet(name, out _));
        }

        Assert.IsFalse(SpoutSenders.TryGet(name, out _));
        Assert.DoesNotContain(name, SpoutSenders.GetAll().Select(static s => s.Name));
    }

    // Every frame the SDK read carries the index our sender drew into it, and its pixels are that
    // frame's pattern. SpoutDX reads through double-buffered staging textures, so its first read of a
    // new sender can be an empty buffer; that one carries index 0 and is skipped.
    private static void AssertFramesMatch(List<PeerFrame> frames)
    {
        List<PeerFrame> drawn = [.. frames.Where(static f => f.Index != 0)];
        Assert.IsGreaterThanOrEqualTo(2, drawn.Count, "The SDK read no frames our sender drew.");
        foreach (PeerFrame frame in drawn)
        {
            Assert.AreEqual(Width, frame.Width);
            Assert.AreEqual(Height, frame.Height);
            Assert.AreEqual(
                Gpu.Fnv1a(Gpu.Pattern(frame.Index, Width, Height)),
                frame.Hash,
                $"frame {frame.Index}"
            );
        }

        CollectionAssert.AreEqual(
            drawn.Select(static f => f.Index).Order().ToList(),
            drawn.Select(static f => f.Index).ToList()
        );
        CollectionAssert.AllItemsAreUnique(drawn.Select(static f => f.SenderFrame).ToList());
    }

    // Sends a new frame every 16 ms until cancelled, drawing each frame's index into it.
    private static async Task SendUntilAsync(
        SpoutDevice device,
        SpoutSender sender,
        ComPtr<ID3D11Texture2D> texture,
        CancellationToken cancellationToken
    )
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(16));
        uint frame = 1;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                frame++;
                Gpu.Upload(device, texture.Address, Gpu.Pattern(frame, Width, Height), Width);
                _ = sender.Send(texture.D3D11());
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately ignored: cancellation is how the sending ends.
        }
    }
}
