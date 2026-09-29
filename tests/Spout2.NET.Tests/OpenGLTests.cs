using Spout2.NET.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace Spout2.NET.Tests;

/// <summary>
/// OpenGL applications sharing through Spout: their textures are linked to Spout's shared textures
/// through WGL_NV_DX_interop2, and must interoperate with Direct3D 11 peers and the Spout SDK. Rows are
/// flipped by default on the way in and out of OpenGL, whose first row is the bottom of the image.
/// </summary>
[TestClass]
public sealed class OpenGLTests
{
    private const int Width = 64;
    private const int Height = 40;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OpenGLSender_IsReceivedByAD3D11Receiver(bool flip)
    {
        using OpenGLContext gl = OpenGLContext.Create();
        using SpoutDevice device = OpenGLDevice(gl);
        Assert.AreEqual(SpoutGraphicsApi.OpenGL, device.Api);

        // Upright to Direct3D is the pattern; OpenGL holds it upside down unless nothing flips.
        byte[] image = Gpu.Pattern(6, Width, Height);
        uint texture = gl.CreateTexture(
            Width,
            Height,
            flip ? OpenGLContext.Flip(image, Width, Height) : image
        );
        string name = Gpu.UniqueName("gl send");
        using SpoutSender sender = new(name, device);
        Assert.IsTrue(sender.Send(new OpenGLTexture(texture), flip));
        Assert.AreEqual(SpoutFormat.Bgra8Unorm, sender.Format);

        using SpoutDevice d3d11 = SpoutDevice.Create(device.AdapterLuid);
        using SpoutReceiver receiver = new(d3d11, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            CollectionAssert.AreEqual(image, Gpu.Read(d3d11, frame.Texture.NativePointer));
        }

        gl.DeleteTexture(texture);
    }

    [TestMethod]
    public void D3D11Sender_IsReadByAnOpenGLReceiver_WithAndWithoutACopy()
    {
        using OpenGLContext gl = OpenGLContext.Create();
        using SpoutDevice device = OpenGLDevice(gl);
        using SpoutDevice d3d11 = SpoutDevice.Create(device.AdapterLuid);
        string name = Gpu.UniqueName("gl receive");
        using SpoutSender sender = new(name, d3d11);
        using (ComPtr<ID3D11Texture2D> source = Gpu.Filled(d3d11, 8, Width, Height))
        {
            Assert.IsTrue(sender.Send(source.D3D11()));
        }

        byte[] image = Gpu.Pattern(8, Width, Height);
        uint flipped = gl.CreateTexture(Width, Height);
        uint straight = gl.CreateTexture(Width, Height);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            // The linked texture shares the sender's memory: Direct3D's rows, first row first.
            CollectionAssert.AreEqual(image, gl.Read(frame.OpenGLTexture.Name, Width, Height));
            frame.CopyTo(new OpenGLTexture(flipped));
            frame.CopyTo(new OpenGLTexture(straight), flip: false);
        }

        CollectionAssert.AreEqual(
            OpenGLContext.Flip(image, Width, Height),
            gl.Read(flipped, Width, Height)
        );
        CollectionAssert.AreEqual(image, gl.Read(straight, Width, Height));
        gl.DeleteTexture(flipped);
        gl.DeleteTexture(straight);
    }

    [TestMethod]
    public void KeptFrame_IsCopiedIntoAnOpenGLTexture_AfterTheBorrow()
    {
        using OpenGLContext gl = OpenGLContext.Create();
        using SpoutDevice device = OpenGLDevice(gl);
        using SpoutDevice d3d11 = SpoutDevice.Create(device.AdapterLuid);
        string name = Gpu.UniqueName("gl lease");
        using SpoutSender sender = new(name, d3d11);
        using (ComPtr<ID3D11Texture2D> source = Gpu.Filled(d3d11, 14, Width, Height))
        {
            Assert.IsTrue(sender.Send(source.D3D11()));
        }

        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        SpoutFrameLease lease;
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            lease = frame.Retain();
        }

        using (ComPtr<ID3D11Texture2D> source = Gpu.Filled(d3d11, 15, Width, Height))
        {
            Assert.IsTrue(sender.Send(source.D3D11()));
        }

        byte[] image = Gpu.Pattern(14, Width, Height);
        uint flipped = gl.CreateTexture(Width, Height);
        uint straight = gl.CreateTexture(Width, Height);
        using (lease)
        {
            lease.CopyTo(new OpenGLTexture(flipped));
            lease.CopyTo(new OpenGLTexture(straight), flip: false);
            _ = Assert.ThrowsExactly<ArgumentException>(() => lease.CopyTo(new D3D12Texture(1)));
        }

        CollectionAssert.AreEqual(
            OpenGLContext.Flip(image, Width, Height),
            gl.Read(flipped, Width, Height)
        );
        CollectionAssert.AreEqual(image, gl.Read(straight, Width, Height));
        gl.DeleteTexture(flipped);
        gl.DeleteTexture(straight);
    }

    [TestMethod]
    public void TryBeginFrame_RendersIntoTheLinkedTexture()
    {
        using OpenGLContext gl = OpenGLContext.Create();
        using SpoutDevice device = OpenGLDevice(gl);
        string name = Gpu.UniqueName("gl zero copy");
        using SpoutSender sender = new(name, device);
        byte[] image = Gpu.Pattern(12, Width, Height);
        Assert.IsTrue(
            sender.TryBeginFrame(Width, Height, SpoutFormat.Bgra8Unorm, out SpoutSenderFrame frame)
        );
        using (frame)
        {
            gl.Upload(frame.OpenGLTexture.Name, Width, Height, image);
            frame.Publish();
        }

        using SpoutDevice d3d11 = SpoutDevice.Create(device.AdapterLuid);
        using SpoutReceiver receiver = new(d3d11, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame received));
        using (received)
        {
            CollectionAssert.AreEqual(image, Gpu.Read(d3d11, received.Texture.NativePointer));
        }
    }

    [TestMethod]
    public async Task OpenGLSender_IsReceivedByTheSdk()
    {
        SpoutPeer.Require();
        using OpenGLContext gl = OpenGLContext.Create();
        using SpoutDevice device = OpenGLDevice(gl);
        string name = Gpu.UniqueName("gl to sdk");
        using SpoutSender sender = new(name, device);
        uint texture = gl.CreateTexture(Width, Height);

        // The SDK peer receives in another process while frames are sent from this, the context's thread.
        Task<PeerReceipt> receiving = SpoutPeer.ReceiveAsync(
            name,
            4,
            TimeSpan.FromSeconds(10),
            TestContext.CancellationToken
        );
        uint frame = 0;
        while (!receiving.IsCompleted)
        {
            gl.Upload(
                texture,
                Width,
                Height,
                OpenGLContext.Flip(Gpu.Pattern(++frame, Width, Height), Width, Height)
            );
            _ = sender.Send(new OpenGLTexture(texture));
            Thread.Yield();
            await Task.Delay(16, TestContext.CancellationToken).ConfigureAwait(true);
            gl.MakeCurrent();
        }

        PeerReceipt receipt = await receiving;
        Assert.AreEqual(0, receipt.ExitCode, receipt.Output);
        List<PeerFrame> drawn = [.. receipt.Frames.Where(static f => f.Index != 0)];
        Assert.IsGreaterThanOrEqualTo(2, drawn.Count);
        foreach (PeerFrame received in drawn)
        {
            Assert.AreEqual(
                Gpu.Fnv1a(Gpu.Pattern(received.Index, Width, Height)),
                received.Hash,
                $"frame {received.Index}"
            );
        }

        gl.DeleteTexture(texture);
    }

    [TestMethod]
    public async Task SdkSender_IsReadByAnOpenGLReceiver()
    {
        SpoutPeer.Require();
        string name = Gpu.UniqueName("sdk to gl");
        await using SpoutPeer peer = await SpoutPeer.StartSenderAsync(
            name,
            Width,
            Height,
            TestContext.CancellationToken
        );
        using OpenGLContext gl = OpenGLContext.Create();
        using SpoutDevice device = OpenGLDevice(gl);
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        uint target = gl.CreateTexture(Width, Height);
        int checkedFrames = 0;
        for (int attempt = 0; attempt < 300 && checkedFrames < 5; attempt++)
        {
            if (receiver.TryReceive(out SpoutFrame frame) == SpoutReceiveResult.Received)
            {
                using (frame)
                {
                    if (frame.IsNew)
                    {
                        frame.CopyTo(new OpenGLTexture(target), flip: false);
                        checkedFrames++;
                    }
                }

                byte[] pixels = gl.Read(target, Width, Height);
                uint index = Gpu.FrameIndex(pixels);
                CollectionAssert.AreEqual(
                    Gpu.Pattern(index, Width, Height),
                    pixels,
                    $"frame {index}"
                );
            }

            Thread.Sleep(10);
        }

        Assert.AreEqual(5, checkedFrames);
        gl.DeleteTexture(target);
    }

    [TestMethod]
    public void OpenGLDevice_NeedsItsContextCurrent_AndRefusesD3D12Textures()
    {
        using OpenGLContext gl = OpenGLContext.Create();
        using SpoutDevice device = OpenGLDevice(gl);
        using SpoutSender sender = new(Gpu.UniqueName("gl refuse"), device);
        uint texture = gl.CreateTexture(Width, Height);
        _ = Assert.ThrowsExactly<ArgumentException>(() => sender.Send(new D3D12Texture(1)));

        OpenGLContext.ReleaseCurrent();
        try
        {
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                sender.Send(new OpenGLTexture(texture))
            );
            _ = Assert.ThrowsExactly<InvalidOperationException>(() => SpoutDevice.ForOpenGL());
        }
        finally
        {
            gl.MakeCurrent();
        }

        gl.DeleteTexture(texture);
    }

    // The device for the context, or inconclusive where the context has no Direct3D interop.
    private static SpoutDevice OpenGLDevice(OpenGLContext gl)
    {
        try
        {
            return SpoutDevice.ForOpenGL();
        }
        catch (SpoutException error)
        {
            throw new AssertInconclusiveException(
                $"OpenGL ({gl.Renderer}) cannot share with Direct3D: {error.Message}"
            );
        }
    }
}
