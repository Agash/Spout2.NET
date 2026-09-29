using Spout2.NET.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D12;

namespace Spout2.NET.Tests;

/// <summary>
/// A frame read under Spout's lock is the frame the sender counted, whole. Spout's lock orders the CPU
/// only, so Spout2.NET waits for its GPU copies before releasing it on either side; without that, a
/// reader measurably gets the previous frame about half the time while the sender works flat out. A
/// sender alternates two frames as fast as it can, and every read is checked against the count.
/// </summary>
[TestClass]
public sealed class D3D12SyncTests
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Reads = 150;

    public enum Side
    {
        D3D11,
        D3D12,
        D3D12View,
    }

    [TestMethod]
    [DataRow(Side.D3D11, Side.D3D11)]
    [DataRow(Side.D3D11, Side.D3D12)]
    [DataRow(Side.D3D11, Side.D3D12View)]
    [DataRow(Side.D3D12, Side.D3D11)]
    [DataRow(Side.D3D12, Side.D3D12)]
    public void Reader_SeesTheCountedFrame_WhileTheSenderRuns(Side sending, Side reading)
    {
        using D3D12Tests.D3D12Context senderContext = D3D12Tests.D3D12Context.Create();
        using D3D12Tests.D3D12Context readerContext = D3D12Tests.D3D12Context.Create(
            senderContext.AdapterLuid
        );
        using SpoutDevice senderDevice =
            sending == Side.D3D11
                ? SpoutDevice.Create(senderContext.AdapterLuid)
                : senderContext.SpoutDevice();
        using SpoutDevice readerDevice =
            reading == Side.D3D11
                ? SpoutDevice.Create(senderContext.AdapterLuid)
                : readerContext.SpoutDevice();
        byte[] odd = Gpu.Pattern(1, Width, Height);
        byte[] even = Gpu.Pattern(2, Width, Height);

        string name = Gpu.UniqueName($"sync {sending} {reading}");
        using SpoutSender sender = new(name, senderDevice);
        using Source first = new(senderDevice, senderContext, 1);
        using Source second = new(senderDevice, senderContext, 2);
        Assert.IsTrue(first.SendTo(sender));
        long sent = 1;
        using CancellationTokenSource stop = new();
        Thread sendingThread = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if ((sent % 2 == 0 ? first : second).SendTo(sender))
                {
                    sent++;
                }
            }
        });
        sendingThread.Start();

        using ComPtr<ID3D11Texture2D> target11 = Gpu.CreateTexture(readerDevice, Width, Height);
        using ComPtr<ID3D12Resource>? target12 =
            reading == Side.D3D12 ? readerContext.CreateTexture(Width, Height) : null;
        using SpoutReceiver receiver = new(readerDevice, new() { SenderName = name });
        int stale = 0;
        int mixed = 0;
        try
        {
            for (int i = 0; i < Reads; i++)
            {
                (long count, byte[] pixels) = Receive(receiver, reading, target11, target12);
                byte[] expected = count % 2 == 1 ? odd : even;
                if (!pixels.AsSpan().SequenceEqual(expected))
                {
                    if (pixels.AsSpan().SequenceEqual(count % 2 == 1 ? even : odd))
                    {
                        stale++;
                    }
                    else
                    {
                        mixed++;
                    }
                }
            }
        }
        finally
        {
            stop.Cancel();
            sendingThread.Join();
        }

        Assert.AreEqual(0, stale, $"reads holding the other frame, of {Reads}");
        Assert.AreEqual(0, mixed, $"reads holding neither frame, of {Reads}");
    }

    private static (long Count, byte[] Pixels) Receive(
        SpoutReceiver receiver,
        Side reading,
        ComPtr<ID3D11Texture2D> target11,
        ComPtr<ID3D12Resource>? target12
    )
    {
        while (true)
        {
            if (receiver.TryReceive(out SpoutFrame frame) != SpoutReceiveResult.Received)
            {
                continue;
            }

            long count;
            using (frame)
            {
                count = frame.FrameNumber;
                switch (reading)
                {
                    case Side.D3D11:
                        frame.CopyTo(target11.D3D11());
                        break;
                    case Side.D3D12:
                        frame.CopyTo(new D3D12Texture(target12!.Address));
                        break;
                    default:
                        // Read in place, inside the borrow.
                        return (
                            count,
                            D3D12Tests.Read(receiver.Device, frame.D3D12Texture, Width, Height)
                        );
                }
            }

            byte[] pixels =
                reading == Side.D3D11
                    ? Gpu.Read(receiver.Device, target11.Address)
                    : D3D12Tests.Read(
                        receiver.Device,
                        new D3D12Texture(target12!.Address),
                        Width,
                        Height
                    );
            return (count, pixels);
        }
    }

    // A frame to send, on the sender's API.
    private sealed class Source : IDisposable
    {
        private readonly ComPtr<ID3D11Texture2D>? _d3d11;
        private readonly ComPtr<ID3D12Resource>? _d3d12;

        public Source(SpoutDevice device, D3D12Tests.D3D12Context context, uint frame)
        {
            if (device.Api == SpoutGraphicsApi.Direct3D12)
            {
                _d3d12 = context.CreateTexture(Width, Height);
                D3D12Tests.Fill(device, new D3D12Texture(_d3d12.Address), frame, Width, Height);
            }
            else
            {
                _d3d11 = Gpu.Filled(device, frame, Width, Height);
            }
        }

        public bool SendTo(SpoutSender sender) =>
            _d3d12 is not null
                ? sender.Send(new D3D12Texture(_d3d12.Address))
                : sender.Send(_d3d11!.D3D11());

        public void Dispose()
        {
            _d3d11?.Dispose();
            _d3d12?.Dispose();
        }
    }
}
