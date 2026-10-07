# Spout2.NET

[![NuGet](https://img.shields.io/nuget/v/Spout2.NET.svg)](https://www.nuget.org/packages/Spout2.NET)
[![build](https://github.com/Agash/Spout2.NET/actions/workflows/build.yml/badge.svg)](https://github.com/Agash/Spout2.NET/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[Spout](https://spout.zeal.co) for .NET 11: share video frames between Windows applications in real time
as GPU textures, from Direct3D 11, Direct3D 12 and OpenGL. Frames published here appear in OBS, Resolume,
TouchDesigner and every other Spout 2 application, and theirs can be received here.

It implements the Spout protocol: the shared sender registry, texture sharing, the access lock, frame
counting, frame sync and the sender memory buffer, over Direct3D 11 through
[CsWin32](https://github.com/microsoft/CsWin32) and .NET's named kernel objects. Direct3D 12
applications work natively: the shared textures are opened on their device and copied or rendered on
their queue. OpenGL textures are linked through `WGL_NV_DX_interop2`. It is tested byte-exact in both
directions against the Spout SDK running in a separate process.

- Native AOT compatible, with no native dependencies.
- Borrowed frames are the sender's own texture, opened on your device: receiving costs no copy.
- Zero-copy sending: render straight into the shared texture.
- Frames carry size, format, the sender's frame number and when they were observed.

> **Alpha.** Expect breaking changes before 1.0.

## Requirements

- Windows 10 or later, a Direct3D 11 GPU (or the WARP software rasterizer).
- For OpenGL, a GPU driver with `WGL_NV_DX_interop2` (NVIDIA, AMD and Intel drivers have it).
- .NET 11.

## Install

```sh
dotnet add package Spout2.NET
```

## Devices

Spout shares textures between devices on the same GPU. A `SpoutDevice` is the graphics device frames
are sent from and received on, and it fixes which API's textures it takes:

```csharp
// Direct3D 11: the application's device, or one Spout2.NET creates.
using SpoutDevice device = SpoutDevice.FromD3D11Device(myDevicePointer); // an ID3D11Device*
using SpoutDevice device = SpoutDevice.Create();                         // the default GPU
using SpoutDevice device = SpoutDevice.CreateFor(senderInfo);           // the GPU a sender is on

// Direct3D 12: the application's device, and the queue Spout's copies are submitted to.
using SpoutDevice device = SpoutDevice.FromD3D12Device(myD3D12Device, myCommandQueue);

// OpenGL: the context current on the calling thread.
using SpoutDevice device = SpoutDevice.ForOpenGL();
```

`SpoutDevice.GetAdapters()` lists the GPUs. Textures are typed by API: `D3D11Texture` (an
`ID3D11Texture2D*`), `D3D12Texture` (an `ID3D12Resource*` and the state it is in) and `OpenGLTexture`
(a texture name). An OpenGL device is used on the thread its context is current on.

Every factory also takes an `ILoggerFactory`. The device's senders and receivers log through it: a
sender publishing, resizing and leaving, a receiver connecting to and losing senders, and every fault
(a lock taken from a crashed process, a sender on another GPU, a dropped frame).

## Send

```csharp
using SpoutSender sender = new("My Output", device);

// Copy a texture of yours into the shared texture on the GPU:
sender.Send(new D3D11Texture(texture));                                          // Direct3D 11
sender.Send(new D3D12Texture(resource, D3D12ResourceState.PixelShaderResource)); // Direct3D 12
sender.Send(new OpenGLTexture(textureName));                                     // OpenGL

// Or render into the shared texture directly, with no copy:
if (sender.TryBeginFrame(1920, 1080, SpoutFormat.Bgra8Unorm, out SpoutSenderFrame frame))
{
    using (frame)
    {
        Render(frame.Texture); // or frame.D3D12Texture, frame.OpenGLTexture, by the device's API
        frame.Publish();
    }
}
```

An application that renders frames with compute shaders creates the shared texture writable by shaders
(unordered access) with `new SpoutSenderOptions { ShaderWritable = true }`; receivers open it as any
other. The format must support typed unordered-access stores on the device, which 8-bit RGBA and BGRA do
on current GPUs. `FramesPerSecond` is measured on the options' `TimeProvider`.

OpenGL's first row is the bottom of the image and Direct3D's is the top, so OpenGL textures are flipped
on their way in and out, as the Spout SDK does by default. Pass `flip: false` for a texture that is
already top-down.

The sender joins the Spout registry with its first frame and leaves it when disposed. In OBS, add a
**Spout2 Capture** source and choose "My Output".

## Receive

Frames are borrowed: `SpoutFrame` is the sender's texture under Spout's lock, valid until disposed.
Read it in place (`Texture`, `D3D12Texture` or `OpenGLTexture`, by the device's API), copy it into a
texture of yours (`CopyTo`), or keep a copy (`Retain`):

```csharp
using SpoutReceiver receiver = new(device, new() { SenderName = "OBS" }); // or null: the active sender

await receiver.RunAsync((in SpoutFrame frame) =>
{
    frame.CopyTo(new D3D12Texture(encoderInput)); // GPU copy into a texture of yours
    Console.WriteLine($"#{frame.FrameNumber} {frame.Width}x{frame.Height} {frame.Format}");
}, cancellationToken);
```

`RunAsync` delivers each new frame on a thread of its own, waits for the sender when it stops, and
follows the active sender when no name is given. For a loop of your own, `TryReceive` borrows the current
frame:

```csharp
if (receiver.TryReceive(out SpoutFrame frame) == SpoutReceiveResult.Received)
{
    using (frame)
    {
        using SpoutFrameLease kept = frame.Retain(); // a copy that outlives the borrow, with the same CopyTo
    }
}
```

Spout's lock is a Win32 mutex owned by the receiving thread, so a borrowed frame is disposed on the
thread that received it and never held across an `await`.

The lock orders the CPU only, not the GPU. So Spout2.NET waits for the GPU work on a shared texture
before it releases the lock: its own copies, and your reads or rendering submitted while the frame was
held. A frame is therefore always the frame the sender counted, never the one before. Senders that
publish without waiting, as the Spout SDK's do, can still hand over the previous frame's pixels, though
never a torn frame.

## Senders

```csharp
foreach (SpoutSenderInfo s in SpoutSenders.GetAll())
    Console.WriteLine($"{s.Name} {s.Width}x{s.Height} {s.Format} {s.ExecutablePath}");

string? active = SpoutSenders.Active;
SpoutSenders.TrySetActive("My Output");

await foreach (var senders in SpoutSenders.WatchAsync(TimeSpan.FromSeconds(1), cancellationToken))
    Show(senders); // each time a sender starts, stops or changes
```

## Coming from the Spout SDK

| Spout SDK (`spoutDX`) | Spout2.NET |
| --- | --- |
| `spoutDX`, `spoutDX12`, `spoutGL` | `SpoutDevice.FromD3D11Device`, `FromD3D12Device`, `ForOpenGL` |
| `SetSenderName`, `SendTexture` | `new SpoutSender(name, device)`, `Send` |
| rendering into the sender's shared texture | `TryBeginFrame`, `SpoutSenderFrame.Publish` |
| `ReceiveTexture`, `GetSenderTexture` | `TryReceive` / `RunAsync`, `SpoutFrame.Texture` |
| `IsUpdated` | `SpoutFrame.SenderChanged` |
| `IsFrameNew`, `GetSenderFrame`, `GetSenderFps` | `SpoutFrame.IsNew`, `FrameNumber`, `SpoutSender.FramesPerSecond` |
| `SetFrameSync`, `WaitFrameSync` | `SpoutSenderOptions.SignalFrameSync`, `SpoutReceiverOptions.WaitForFrameSync` |
| `WriteMemoryBuffer`, `ReadMemoryBuffer` | `SpoutSender.WriteMetadata`, `SpoutReceiver.ReadMetadata` |
| `GetSenderCount`, `GetSender`, `GetSenderInfo` | `SpoutSenders.GetAll`, `TryGet` |
| `GetActiveSender`, `SetActiveSender` | `SpoutSenders.Active`, `TrySetActive` |
| `GetSenderAdapter` | `SpoutDevice.CreateFor` |

Frame counting is on in Spout2.NET whatever Spout Settings says, so frames are always numbered and
repeats are never delivered as new.

## Building from source

```sh
git clone --recursive https://github.com/Agash/Spout2.NET
cd Spout2.NET
dotnet build Spout2.NET.slnx
pwsh tests/SpoutPeer/build.ps1   # the upstream SDK as a test peer; needs MSVC
dotnet test --solution Spout2.NET.slnx
```

The Spout SDK in `external/Spout2` is the protocol reference and the interop test peer; nothing from it
ships.

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
