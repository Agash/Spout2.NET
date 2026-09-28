using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Spout2.NET.Direct3D;
using Spout2.NET.Protocol;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;

namespace Spout2.NET.Tests;

/// <summary>The Spout protocol pieces against the layouts and semantics the SDK uses.</summary>
[TestClass]
public sealed unsafe class ProtocolTests
{
    [TestMethod]
    public void SharedTextureInfo_HasTheSdksLayout()
    {
        Assert.AreEqual(280, Unsafe.SizeOf<SharedTextureInfo>());
        Assert.AreEqual(SharedTextureInfo.Size, Unsafe.SizeOf<SharedTextureInfo>());
        Assert.AreEqual(
            20,
            (int)Marshal.OffsetOf<SharedTextureInfo>(nameof(SharedTextureInfo.Description))
        );
        Assert.AreEqual(
            276,
            (int)Marshal.OffsetOf<SharedTextureInfo>(nameof(SharedTextureInfo.PartnerId))
        );
    }

    [TestMethod]
    public void Names_AreCheckedAgainstTheRegistrysSlots()
    {
        byte[] slot = new byte[SpoutName.MaxLength];
        SpoutName.Write("Camera 1", slot);
        Assert.AreEqual("Camera 1", SpoutName.Decode(slot));
        Assert.AreEqual(0, slot[8]);
        Assert.IsNull(SpoutName.Decode(new byte[SpoutName.MaxLength]));
        Assert.AreEqual(255, SpoutName.Encode(new string('a', 255)).Length);

        _ = Assert.ThrowsExactly<ArgumentException>(() => SpoutName.Encode(new string('a', 256)));
        _ = Assert.ThrowsExactly<ArgumentException>(() => SpoutName.Encode("a\0b"));
        _ = Assert.ThrowsExactly<ArgumentException>(() => SpoutName.Encode(string.Empty));

        // Text that only describes, such as an executable path, is truncated to fit its field.
        byte[] field = new byte[8];
        Assert.IsFalse(SpoutName.TryWrite("0123456789", field));
        Assert.AreEqual("0123456", Encoding.ASCII.GetString(field, 0, 7));
        Assert.AreEqual(0, field[7]);
        Assert.IsTrue(SpoutName.TryWrite("short", field));

        Assert.IsLessThan(
            0,
            SpoutName.CompareOrdinalBytes("A", "a"),
            "The registry sorts by byte, not culture."
        );
    }

    [TestMethod]
    public void Name_OutsideTheAnsiCodePage_IsRefused()
    {
        // On a system whose ANSI code page is UTF-8, every name is representable.
        if (CodePagesEncodingProvider.Instance.GetEncoding(0) is null)
        {
            Assert.Inconclusive("The system code page is UTF-8.");
        }

        _ = Assert.ThrowsExactly<ArgumentException>(() => SpoutName.Encode("\U0001F4A5"));
    }

    [TestMethod]
    public void LegacyFormats_ReadAsBgra()
    {
        Assert.AreEqual(SpoutFormat.Bgra8Unorm, SpoutFormats.FromRegistry(0));
        Assert.AreEqual(SpoutFormat.Bgra8Unorm, SpoutFormats.FromRegistry(21));
        Assert.AreEqual(SpoutFormat.Bgra8Unorm, SpoutFormats.FromRegistry(22));
        Assert.AreEqual(SpoutFormat.Rgba16Float, SpoutFormats.FromRegistry(10));
        Assert.AreEqual((SpoutFormat)61, SpoutFormats.FromRegistry(61));
    }

    [TestMethod]
    public void FrameCounter_CountsAsTheSdkDoes()
    {
        string name = Gpu.UniqueName("counter");
        using FrameCounter sender = FrameCounter.OpenOrCreate(name);
        using FrameCounter receiver = FrameCounter.OpenOrCreate(name);
        Assert.AreEqual(0, receiver.Read(), "A fresh counter reports no frames.");
        Assert.IsTrue(sender.Increment());
        Assert.IsTrue(sender.Increment());
        Assert.AreEqual(2, receiver.Read());
        Assert.AreEqual(2, receiver.Read(), "Reading does not change the count.");
    }

    [TestMethod]
    public void MetadataBuffer_WritesTheSdksHeader()
    {
        string name = Gpu.UniqueName("metadata");
        using MetadataBuffer written = MetadataBuffer.Create(name, 100);
        written.Write("abc"u8);

        using (SharedMemory map = SharedMemory.TryOpen(SpoutName.MetadataMap(name))!)
        using (SharedMemory.Lock held = map.Acquire())
        {
            Assert.AreEqual("100\0", Encoding.ASCII.GetString(held.Bytes[..4]));
            Assert.AreEqual("abc\0", Encoding.ASCII.GetString(held.Bytes.Slice(16, 4)));
        }

        using MetadataBuffer read = MetadataBuffer.TryOpen(name)!;
        Assert.AreEqual(100, read.Capacity);
        byte[] buffer = new byte[200];
        Assert.AreEqual(100, read.Read(buffer));
        _ = Assert.ThrowsExactly<ArgumentException>(() => written.Write(new byte[101]));
        Assert.IsNull(MetadataBuffer.TryOpen(Gpu.UniqueName("no metadata")));
    }

    [TestMethod]
    public void Registry_PrunesSendersThatAreGone()
    {
        // A name whose sender map no longer exists, as a crashed sender leaves behind.
        string stale = Gpu.UniqueName("stale");
        string live = Gpu.UniqueName("live");
        using SharedMemory info = SharedMemory.CreateOrOpen(live, SharedTextureInfo.Size);
        SenderRegistry.Register(live);
        using (SharedMemory stub = SharedMemory.CreateOrOpen(stale, SharedTextureInfo.Size))
        {
            SenderRegistry.Register(stale);
            Assert.Contains(stale, SenderRegistry.GetNames());
        }

        List<string> names = SenderRegistry.GetNames();
        Assert.DoesNotContain(stale, names);
        Assert.Contains(live, names);
        _ = Assert.ThrowsExactly<SpoutException>(() => SenderRegistry.Register(live));

        SenderRegistry.Release(live);
        Assert.DoesNotContain(live, SenderRegistry.GetNames());
        SenderRegistry.Release(live);
    }

    [TestMethod]
    public void Receive_OfACpuSender_ReportsIt()
    {
        string name = Gpu.UniqueName("cpu");
        using SharedMemory info = SharedMemory.CreateOrOpen(name, SharedTextureInfo.Size);
        Write(
            info,
            new SharedTextureInfo
            {
                Width = 64,
                Height = 48,
                Format = 87,
                PartnerId = SharedTextureInfo.CpuSharing,
            }
        );
        SenderRegistry.Register(name);
        try
        {
            Assert.IsTrue(SpoutSenders.TryGet(name, out SpoutSenderInfo sender));
            Assert.IsTrue(sender.SharesCpuMemory);
            _ = Assert.ThrowsExactly<SpoutException>(() => SpoutDevice.CreateFor(sender));
            using SpoutDevice device = Gpu.Device();
            using SpoutReceiver receiver = new(device, new() { SenderName = name });
            Assert.AreEqual(
                SpoutReceiveResult.CpuSender,
                receiver.TryReceive(out SpoutFrame frame)
            );
            frame.Dispose();
        }
        finally
        {
            SenderRegistry.Release(name);
        }
    }

    // Senders built on other Spout code can share a texture with a keyed mutex instead of the named
    // access mutex; the receiver takes the texture's own lock then.
    [TestMethod]
    public void Receive_OfAKeyedMutexTexture_TakesTheTexturesLock()
    {
        using SpoutDevice device = Gpu.Device();
        using ComPtr<ID3D11Texture2D> texture = Gpu.CreateTexture(
            device,
            32,
            16,
            misc: D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX
        );
        using ComPtr<IDXGIKeyedMutex> keyed = texture.As<IDXGIKeyedMutex>()!;
        Assert.AreEqual(0, keyed.Pointer->AcquireSync(0, 1000).Value);
        Gpu.Upload(device, texture.Address, Gpu.Pattern(5, 32, 16), 32);
        keyed.Pointer->ReleaseSync(0);
        using ComPtr<IDXGIResource> resource = texture.As<IDXGIResource>()!;
        HANDLE handle;
        resource.Pointer->GetSharedHandle(&handle);

        string name = Gpu.UniqueName("keyed");
        using SharedMemory info = SharedMemory.CreateOrOpen(name, SharedTextureInfo.Size);
        Write(
            info,
            new SharedTextureInfo
            {
                ShareHandle = (uint)(nint)handle.Value,
                Width = 32,
                Height = 16,
                Format = 87,
            }
        );
        SenderRegistry.Register(name);
        try
        {
            using SpoutReceiver receiver = new(device, new() { SenderName = name });
            Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
            using (frame)
            {
                CollectionAssert.AreEqual(Gpu.Pattern(5, 32, 16), Gpu.Read(device, frame.Texture));
            }
        }
        finally
        {
            SenderRegistry.Release(name);
        }
    }

    [TestMethod]
    [DataRow(SpoutFormat.Rgba8Unorm)]
    [DataRow(SpoutFormat.Rgb10A2Unorm)]
    [DataRow(SpoutFormat.Rgba16Float)]
    [DataRow(SpoutFormat.Rgba32Float)]
    public void Formats_ReachTheReceiverUnchanged(SpoutFormat format)
    {
        using SpoutDevice device = Gpu.Device();
        const int width = 40;
        const int height = 24;
        int bytes = Gpu.BytesPerPixel(format);
        // Float formats get ordinary values: random bits include NaNs, whose payload a copy need not keep.
        byte[] pixels = format switch
        {
            SpoutFormat.Rgba16Float =>
            [
                .. Enumerable
                    .Range(0, width * height * 4)
                    .SelectMany(static i => BitConverter.GetBytes((Half)(i % 97 / 97f))),
            ],
            SpoutFormat.Rgba32Float =>
            [
                .. Enumerable
                    .Range(0, width * height * 4)
                    .SelectMany(static i => BitConverter.GetBytes(i % 89 / 89f)),
            ],
            _ =>
            [
                .. Enumerable.Range(0, width * height * bytes).Select(static i => (byte)(i * 31)),
            ],
        };

        using ComPtr<ID3D11Texture2D> texture = Gpu.CreateTexture(device, width, height, format);
        Gpu.Upload(device, texture.Address, pixels, width, bytes);
        string name = Gpu.UniqueName(format.ToString());
        using SpoutSender sender = new(name, device);
        Assert.IsTrue(sender.Send(texture.Address));
        Assert.AreEqual(format, sender.Format);
        Assert.IsTrue(SpoutSenders.TryGet(name, out SpoutSenderInfo info));
        Assert.AreEqual(format, info.Format);

        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            Assert.AreEqual(format, frame.Format);
            CollectionAssert.AreEqual(pixels, Gpu.Read(device, frame.Texture));
        }
    }

    private static void Write(SharedMemory map, SharedTextureInfo info)
    {
        using SharedMemory.Lock held = map.Acquire();
        MemoryMarshal.Write(held.Bytes, in info);
    }
}
