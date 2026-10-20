namespace Spout2.NET;

/// <summary>
/// The pixel format of a shared texture. The values are <c>DXGI_FORMAT</c> values, so a format
/// Spout2.NET does not name is still carried through as its DXGI number.
/// </summary>
public enum SpoutFormat : uint
{
    /// <summary>No format, or one the sender did not state.</summary>
    Unknown = 0,

    /// <summary>32-bit float RGBA (<c>DXGI_FORMAT_R32G32B32A32_FLOAT</c>), linear.</summary>
    Rgba32Float = 2,

    /// <summary>16-bit float RGBA (<c>DXGI_FORMAT_R16G16B16A16_FLOAT</c>), linear scRGB.</summary>
    Rgba16Float = 10,

    /// <summary>16-bit normalized RGBA (<c>DXGI_FORMAT_R16G16B16A16_UNORM</c>).</summary>
    Rgba16Unorm = 11,

    /// <summary>10-bit RGB with 2-bit alpha (<c>DXGI_FORMAT_R10G10B10A2_UNORM</c>).</summary>
    Rgb10A2Unorm = 24,

    /// <summary>8-bit RGBA (<c>DXGI_FORMAT_R8G8B8A8_UNORM</c>).</summary>
    Rgba8Unorm = 28,

    /// <summary>8-bit RGBA with sRGB transfer (<c>DXGI_FORMAT_R8G8B8A8_UNORM_SRGB</c>).</summary>
    Rgba8UnormSrgb = 29,

    /// <summary>8-bit BGRA (<c>DXGI_FORMAT_B8G8R8A8_UNORM</c>), Spout's default.</summary>
    Bgra8Unorm = 87,

    /// <summary>8-bit BGR with unused alpha (<c>DXGI_FORMAT_B8G8R8X8_UNORM</c>).</summary>
    Bgrx8Unorm = 88,

    /// <summary>8-bit BGRA with sRGB transfer (<c>DXGI_FORMAT_B8G8R8A8_UNORM_SRGB</c>).</summary>
    Bgra8UnormSrgb = 91,
}

internal static class SpoutFormats
{
    // Direct3D 9 senders (the SDK's SpoutDX9) record D3D9 formats; the SDK reads them as BGRA.
    private const uint D3DFormatA8R8G8B8 = 21;
    private const uint D3DFormatX8R8G8B8 = 22;

    // The formats a frame can move through CPU memory in: every one Spout2.NET names.
    public static int BytesPerPixel(SpoutFormat format) =>
        format switch
        {
            SpoutFormat.Rgba32Float => 16,
            SpoutFormat.Rgba16Float or SpoutFormat.Rgba16Unorm => 8,
            SpoutFormat.Rgb10A2Unorm
            or SpoutFormat.Rgba8Unorm
            or SpoutFormat.Rgba8UnormSrgb
            or SpoutFormat.Bgra8Unorm
            or SpoutFormat.Bgrx8Unorm
            or SpoutFormat.Bgra8UnormSrgb => 4,
            _ => throw new ArgumentException(
                $"Frames in {format} do not move through CPU memory.",
                nameof(format)
            ),
        };

    public static SpoutFormat FromRegistry(uint format) =>
        format is 0 or D3DFormatA8R8G8B8 or D3DFormatX8R8G8B8
            ? SpoutFormat.Bgra8Unorm
            : (SpoutFormat)format;
}
