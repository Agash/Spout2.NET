# Spout2.NET

.NET bindings for [Spout2](https://spout.zeal.co), the Windows framework for sharing video frames
between applications in real time. Publish frames for other apps to pick up, or receive frames
another app is sharing, zero-copy as DirectX 11 shared textures. Works with OBS, Resolume,
TouchDesigner, and other tools that speak Spout.

## Requirements

- Windows x64 with a DirectX 11 GPU.
- .NET 10 (or .NET 11).
- DirectX objects are passed as native pointers; [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)
  is the easy way to create them (`ID3D11Device.NativePointer`, `ID3D11Texture2D.NativePointer`).

## Install

```sh
dotnet add package Spout2.NET
```

The package bundles the native helper it needs; there is nothing else to install.

## Publish

```csharp
using Spout2.NET;

// device = your ID3D11Device.NativePointer (from Vortice)
using var sender = new SpoutSender("My Output", device);

// texture = an ID3D11Texture2D.NativePointer
sender.Send(texture);
```

In OBS, add a **Spout2 Capture** source and choose "My Output".

## Receive

```csharp
using Spout2.NET;

using var receiver = new SpoutReceiver(device);

if (receiver.Receive() && receiver.Texture != 0)
{
    if (receiver.IsUpdated)
    {
        int w = receiver.SenderWidth, h = receiver.SenderHeight; // size changed
    }
    // wrap receiver.Texture as a (non-owning) ID3D11Texture2D and read / copy / encode it
}
```

List senders with `SpoutSenders`.

## Building from source

```sh
git clone --recursive https://github.com/Agash/Spout2.NET
cd Spout2.NET
pwsh native/build-native.ps1
dotnet build Spout2.NET.slnx
dotnet test
```

The native helper compiles the Spout2 SDK (a git submodule) and a small shim into one self-contained
DLL; the managed library is plain `net10.0` / `net11.0`.

## License

MIT. See [LICENSE](LICENSE). Bundled Spout2 code is BSD 2-Clause; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
