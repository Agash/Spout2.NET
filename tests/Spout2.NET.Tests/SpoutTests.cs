using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpGen.Runtime;
using Spout2.NET;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Spout2.NET.Tests;

[TestClass]
public sealed class SpoutValueTypeTests
{
    [TestMethod]
    public void DxgiFormat_Values_MatchNativeEnum()
    {
        Assert.AreEqual(87u, Convert.ToUInt32(DxgiFormat.B8G8R8A8UNorm));
        Assert.AreEqual(28u, Convert.ToUInt32(DxgiFormat.R8G8B8A8UNorm));
    }
}

/// <summary>
/// End-to-end frame transport: a sender publishes a known BGRA texture and a receiver reads it back
/// byte-exact through Spout's shared DirectX 11 texture. Requires a Direct3D 11 device; reports
/// Inconclusive when none is available.
/// </summary>
[TestClass]
public sealed class SpoutTransportTests
{
    [TestMethod]
    [TestCategory("RequiresGpu")]
    public void Bgra_64x64_RoundTripsByteExact()
    {
        // A DXGI shared handle cannot be opened on the device that created it, so the sender and
        // receiver use separate devices - exactly what separate processes have.
        if (!TryCreateDevice(out ID3D11Device? sendDevice, out ID3D11DeviceContext? sendContext))
            Assert.Inconclusive("No Direct3D 11 device available on this host.");
        if (!TryCreateDevice(out ID3D11Device? recvDevice, out ID3D11DeviceContext? recvContext))
            Assert.Inconclusive("Could not create a second Direct3D 11 device.");

        using (sendDevice)
        using (sendContext)
        using (recvDevice)
        using (recvContext)
        {
            const int w = 64, h = 64;
            byte[] source = Pattern(w, h);

            using ID3D11Texture2D sourceTexture = CreateTexture(sendDevice!, w, h, source);
            using var sender = new SpoutSender("Spout.NET Test", sendDevice!.NativePointer);
            using var receiver = new SpoutReceiver(recvDevice!.NativePointer, "Spout.NET Test");

            // Publish repeatedly and pull until a frame is delivered.
            nint received = 0;
            var sw = Stopwatch.StartNew();
            int frames = 0;
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                sender.Send(sourceTexture.NativePointer);
                if (receiver.Receive() && receiver.Texture != 0)
                {
                    received = receiver.Texture;
                    if (++frames >= 3) break;
                }
                Thread.Sleep(16);
            }

            using (var senders = new SpoutSenders())
                Assert.IsTrue(senders.Names().Contains("Spout.NET Test"), "the sender should appear in the registry");

            Assert.AreNotEqual(0, received, "the receiver should connect and deliver a texture");
            Assert.AreEqual(w, receiver.SenderWidth);
            Assert.AreEqual(h, receiver.SenderHeight);

            byte[] got = ReadBack(recvDevice, recvContext!, received, w, h);
            CollectionAssert.AreEqual(source, got, "received pixels must match the sent texture");
        }
    }

    private static bool TryCreateDevice(out ID3D11Device? device, out ID3D11DeviceContext? context)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_0];
        foreach (DriverType driver in new[] { DriverType.Hardware, DriverType.Warp })
        {
            Result hr = D3D11.D3D11CreateDevice(
                null, driver, DeviceCreationFlags.BgraSupport, levels,
                out device, out context);
            if (hr.Success && device is not null && context is not null) return true;
            device?.Dispose();
            context?.Dispose();
        }
        device = null;
        context = null;
        return false;
    }

    private static ID3D11Texture2D CreateTexture(ID3D11Device device, int w, int h, byte[] bgra)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };
        unsafe
        {
            fixed (byte* p = bgra)
            {
                var data = new SubresourceData((nint)p, (uint)(w * 4));
                return device.CreateTexture2D(desc, [data]);
            }
        }
    }

    private static byte[] ReadBack(ID3D11Device device, ID3D11DeviceContext context, nint texturePtr, int w, int h)
    {
        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        };
        using ID3D11Texture2D staging = device.CreateTexture2D(stagingDesc);

        // Wrap Spout's texture without taking its reference: AddRef balances the Dispose.
        var received = new ID3D11Texture2D(texturePtr);
        received.AddRef();
        context.CopyResource(staging, received);
        received.Dispose();

        MappedSubresource map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        byte[] result = new byte[w * h * 4];
        unsafe
        {
            byte* src = (byte*)map.DataPointer;
            int pitch = (int)map.RowPitch;
            for (int y = 0; y < h; y++)
                Marshal.Copy((nint)(src + y * pitch), result, y * w * 4, w * 4);
        }
        context.Unmap(staging, 0);
        return result;
    }

    private static byte[] Pattern(int w, int h)
    {
        byte[] p = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                p[i] = (byte)x;
                p[i + 1] = (byte)y;
                p[i + 2] = (byte)(x ^ y);
                p[i + 3] = (byte)((x + y) & 0xFF);
            }
        }
        return p;
    }
}
