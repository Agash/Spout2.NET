using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spout2.NET.Protocol;

// The 280-byte record every Spout sender keeps in a map named after itself (SpoutSenderNames.h).
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct SharedTextureInfo
{
    public const int Size = 280;

    // The DXGI shared handle of the texture (IDXGIResource::GetSharedHandle): a machine-wide value that
    // fits 32 bits, so any process opens the texture from this number alone.
    public uint ShareHandle;
    public uint Width;
    public uint Height;
    public uint Format;
    public uint Usage;
    public DescriptionBuffer Description;
    public uint PartnerId;

    // Top bits of PartnerId: the sender shares through CPU memory, or through the OpenGL/DirectX
    // interop that a GL sender uses.
    public const uint CpuSharing = 0x80000000;
    public const uint GlDxInterop = 0x40000000;

    [InlineArray(256)]
    public struct DescriptionBuffer
    {
        private byte _element;
    }
}
