// Spout2.NET under Native AOT: a sender and a receiver in one process, through the public API only.
// CI publishes this with PublishAot and runs it; a non-zero exit fails the build.
using System.Runtime.Versioning;
using Spout2.NET;

[assembly: SupportedOSPlatform("windows6.1")]

// A GPU where there is one; CI runners have only the software rasterizer.
System.Collections.Immutable.ImmutableArray<SpoutAdapter> adapters = SpoutDevice.GetAdapters();
SpoutAdapter adapter = adapters.FirstOrDefault(static a => !a.IsSoftware) is { Luid: not 0 } gpu
    ? gpu
    : adapters[0];
using SpoutDevice device = SpoutDevice.Create(adapter.Luid);
Console.WriteLine($"device: {device.AdapterName}");

string name = $"Spout2.NET smoke {Environment.ProcessId}";
using SpoutSender sender = new(name, device);
if (!sender.TryBeginFrame(64, 32, SpoutFormat.Bgra8Unorm, out SpoutSenderFrame frame))
{
    return Fail("the shared texture was busy");
}

using (frame)
{
    frame.Publish();
}

sender.WriteMetadata("smoke"u8);
if (!SpoutSenders.TryGet(name, out SpoutSenderInfo info) || info.Width != 64 || info.Height != 32)
{
    return Fail("the sender is not in the registry");
}

using SpoutReceiver receiver = new(device, new() { SenderName = name });
if (receiver.TryReceive(out SpoutFrame received) != SpoutReceiveResult.Received)
{
    return Fail("no frame");
}

using (received)
{
    if (received.Width != 64 || received.FrameNumber != 1 || !received.IsNew)
    {
        return Fail($"frame {received.Width}x{received.Height} #{received.FrameNumber}");
    }
}

Span<byte> metadata = stackalloc byte[8];
_ = receiver.ReadMetadata(metadata);
if (!metadata.StartsWith("smoke\0"u8))
{
    return Fail("metadata");
}

Console.WriteLine(
    $"ok: {name} {info.Width}x{info.Height} {info.Format}, {SpoutSenders.GetAll().Length} sender(s)"
);
return 0;

static int Fail(string what)
{
    Console.Error.WriteLine($"FAIL: {what}");
    return 1;
}
