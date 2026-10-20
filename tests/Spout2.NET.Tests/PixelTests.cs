using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace Spout2.NET.Tests;

// Frames to and from CPU memory, and the clock and registration that come with a factory.
[TestClass]
public sealed class PixelTests
{
    private const int Width = 64;
    private const int Height = 48;

    [TestMethod]
    [DataRow(SpoutFormat.Bgra8Unorm)]
    [DataRow(SpoutFormat.Rgba8Unorm)]
    [DataRow(SpoutFormat.Rgba16Float)]
    [DataRow(SpoutFormat.Rgba32Float)]
    public void SendPixels_ThenCopyPixelsTo_RoundTripsTheBytes(SpoutFormat format)
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("pixels");
        using SpoutSender sender = new(name, device);
        byte[] pixels = Pattern(format);
        Assert.IsTrue(sender.SendPixels(pixels, Width, Height, format));
        Assert.AreEqual(1, sender.FrameNumber);

        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            Assert.AreEqual(format, frame.Format);
            byte[] received = new byte[pixels.Length];
            Assert.AreEqual(pixels.Length, frame.CopyPixelsTo(received));
            CollectionAssert.AreEqual(pixels, received);
        }
    }

    [TestMethod]
    public void Pixels_AtPaddedStrides_KeepEachRowInPlace()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("stride");
        using SpoutSender sender = new(name, device);
        int row = Width * 4;
        int sendStride = row + 12;
        byte[] packed = Pattern(SpoutFormat.Bgra8Unorm);
        byte[] padded = new byte[(sendStride * (Height - 1)) + row];
        for (int y = 0; y < Height; y++)
        {
            packed.AsSpan(y * row, row).CopyTo(padded.AsSpan(y * sendStride));
        }

        Assert.IsTrue(sender.SendPixels(padded, Width, Height, SpoutFormat.Bgra8Unorm, sendStride));

        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            int readStride = row + 64;
            byte[] read = new byte[(readStride * (Height - 1)) + row];
            read.AsSpan().Fill(0xEE);
            Assert.AreEqual(read.Length, frame.CopyPixelsTo(read, readStride));
            for (int y = 0; y < Height; y++)
            {
                CollectionAssert.AreEqual(
                    packed.AsSpan(y * row, row).ToArray(),
                    read.AsSpan(y * readStride, row).ToArray(),
                    $"Row {y}."
                );
                if (y < Height - 1)
                {
                    Assert.IsLessThan(
                        0,
                        read.AsSpan((y * readStride) + row, readStride - row)
                            .IndexOfAnyExcept((byte)0xEE),
                        $"The padding after row {y} is left alone."
                    );
                }
            }
        }
    }

    [TestMethod]
    public void CopyPixelsTo_OnAKeptFrame_ReadsTheCopy()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("lease");
        using SpoutSender sender = new(name, device);
        byte[] first = Pattern(SpoutFormat.Bgra8Unorm);
        Assert.IsTrue(sender.SendPixels(first, Width, Height, SpoutFormat.Bgra8Unorm));

        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        SpoutFrameLease lease;
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            lease = frame.Retain();
        }

        using (lease)
        {
            // The sender moves on; the kept frame does not.
            byte[] second = [.. first.Select(static b => (byte)~b)];
            Assert.IsTrue(sender.SendPixels(second, Width, Height, SpoutFormat.Bgra8Unorm));
            byte[] read = new byte[first.Length];
            Assert.AreEqual(read.Length, lease.CopyPixelsTo(read));
            CollectionAssert.AreEqual(first, read);
        }
    }

    [TestMethod]
    public void Pixels_RefuseShortBuffersAndUnsizedFormats()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("refuse");
        using SpoutSender sender = new(name, device);
        byte[] pixels = Pattern(SpoutFormat.Bgra8Unorm);
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            sender.SendPixels(pixels.AsSpan(1), Width, Height, SpoutFormat.Bgra8Unorm)
        );
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            sender.SendPixels(pixels, Width, Height, SpoutFormat.Bgra8Unorm, (Width * 4) - 1)
        );
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            sender.SendPixels(pixels, Width, Height, SpoutFormat.Unknown)
        );
        Assert.IsFalse(sender.IsPublished, "Nothing refused reaches the registry.");

        Assert.IsTrue(sender.SendPixels(pixels, Width, Height, SpoutFormat.Bgra8Unorm));
        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            byte[] tooShort = new byte[pixels.Length - 1];
            bool refused = false;
            try
            {
                _ = frame.CopyPixelsTo(tooShort);
            }
            catch (ArgumentException)
            {
                refused = true;
            }

            Assert.IsTrue(refused, "A destination one byte short is refused.");
        }
    }

    [TestMethod]
    public void Receiver_StampsFramesOnItsClock()
    {
        using SpoutDevice device = Gpu.Device();
        string name = Gpu.UniqueName("clock");
        using SpoutSender sender = new(name, device);
        Assert.IsTrue(
            sender.SendPixels(
                Pattern(SpoutFormat.Bgra8Unorm),
                Width,
                Height,
                SpoutFormat.Bgra8Unorm
            )
        );
        FixedClock clock = new(TimeSpan.FromSeconds(42));
        using SpoutReceiver receiver = new(
            device,
            new() { SenderName = name, TimeProvider = clock }
        );
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        using (frame)
        {
            Assert.AreEqual(42_000_000_000, frame.ObservedAtNanoseconds);
        }
    }

    [TestMethod]
    public void AddSpout_RegistersADeviceAndAFactoryOnTheApplicationsClock()
    {
        FixedClock clock = new(TimeSpan.FromSeconds(7));
        ServiceCollection services = new();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddSpout();
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            SpoutFactory factory = provider.GetRequiredService<SpoutFactory>();
            SpoutDevice device = provider.GetRequiredService<SpoutDevice>();
            Assert.AreSame(device, factory.Device);
            Assert.AreSame(clock, factory.TimeProvider);

            string name = Gpu.UniqueName("factory");
            using SpoutSender sender = factory.CreateSender(name);
            Assert.IsTrue(
                sender.SendPixels(
                    Pattern(SpoutFormat.Bgra8Unorm),
                    Width,
                    Height,
                    SpoutFormat.Bgra8Unorm
                )
            );

            // A receiver from the factory follows the active sender, which another test may have
            // moved; this one names its sender and keeps the factory's clock.
            using SpoutReceiver receiver = factory.CreateReceiver(
                new() { SenderName = name, TimeProvider = factory.TimeProvider }
            );
            Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
            using (frame)
            {
                Assert.AreEqual(7_000_000_000, frame.ObservedAtNanoseconds);
            }
        }
    }

    // Distinct bytes at every position. Float formats hold small finite values, whose bits round-trip.
    private static byte[] Pattern(SpoutFormat format)
    {
        int size = format switch
        {
            SpoutFormat.Rgba32Float => 16,
            SpoutFormat.Rgba16Float => 8,
            _ => 4,
        };
        byte[] pixels = new byte[Width * Height * size];
        if (format is SpoutFormat.Rgba16Float)
        {
            Span<Half> halves = MemoryMarshal.Cast<byte, Half>(pixels);
            for (int i = 0; i < halves.Length; i++)
            {
                halves[i] = (Half)((i % 997) / 1024f);
            }
        }
        else if (format is SpoutFormat.Rgba32Float)
        {
            Span<float> floats = MemoryMarshal.Cast<byte, float>(pixels);
            for (int i = 0; i < floats.Length; i++)
            {
                floats[i] = (i % 4093) / 4096f;
            }
        }
        else
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)((i * 31) + (i / 251));
            }
        }

        return pixels;
    }

    // A clock stopped at one instant.
    private sealed class FixedClock(TimeSpan at) : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => at.Ticks;
    }
}
