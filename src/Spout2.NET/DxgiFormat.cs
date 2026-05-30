namespace Spout2.NET;

/// <summary>
/// Common DXGI texture formats for Spout senders. Values match the native <c>DXGI_FORMAT</c> enum.
/// Spout's default and most interoperable format is <see cref="B8G8R8A8UNorm"/>.
/// </summary>
public enum DxgiFormat : uint
{
    /// <summary>32-bit BGRA, 8 bits per channel (DXGI_FORMAT_B8G8R8A8_UNORM). Spout default.</summary>
    B8G8R8A8UNorm = 87,

    /// <summary>32-bit RGBA, 8 bits per channel (DXGI_FORMAT_R8G8B8A8_UNORM).</summary>
    R8G8B8A8UNorm = 28,

    /// <summary>10-bit RGB + 2-bit alpha (DXGI_FORMAT_R10G10B10A2_UNORM).</summary>
    R10G10B10A2UNorm = 24,

    /// <summary>16-bit float RGBA (DXGI_FORMAT_R16G16B16A16_FLOAT).</summary>
    R16G16B16A16Float = 10,
}
