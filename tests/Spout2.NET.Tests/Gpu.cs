using Spout2.NET.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Spout2.NET.Tests;

// Test-side Direct3D work: textures filled from bytes, and bytes read back from textures, through the
// library's CsWin32 bindings.
internal static unsafe class Gpu
{
    // BGRA bytes of frame `frame`, as the SDK peer draws them (spout_peer.cpp PeerPattern): the first
    // pixel carries the frame index.
    public static byte[] Pattern(uint frame, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = ((y * width) + x) * 4;
                pixels[p] = (byte)((x * 7) + (frame * 29));
                pixels[p + 1] = (byte)((y * 13) + (frame * 31));
                pixels[p + 2] = (byte)((x ^ y) + (frame * 37));
                pixels[p + 3] = 255;
            }
        }

        BitConverter.TryWriteBytes(pixels, frame);
        return pixels;
    }

    public static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in data)
        {
            hash = (hash ^ b) * 1099511628211;
        }

        return hash;
    }

    public static uint FrameIndex(ReadOnlySpan<byte> pixels) => BitConverter.ToUInt32(pixels);

    public static ComPtr<ID3D11Texture2D> CreateTexture(
        SpoutDevice device,
        int width,
        int height,
        SpoutFormat format = SpoutFormat.Bgra8Unorm,
        D3D11_RESOURCE_MISC_FLAG misc = 0
    )
    {
        D3D11_TEXTURE2D_DESC description = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = (DXGI_FORMAT)format,
            SampleDesc = new() { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags =
                D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE
                | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
            MiscFlags = misc,
        };
        ID3D11Texture2D* texture;
        device.Device->CreateTexture2D(&description, null, &texture);
        return ComPtr<ID3D11Texture2D>.Attach(texture);
    }

    public static void Upload(
        SpoutDevice device,
        nint texture,
        ReadOnlySpan<byte> pixels,
        int width,
        int bytesPerPixel = 4
    )
    {
        fixed (byte* data = pixels)
        {
            device.Context->UpdateSubresource(
                (ID3D11Resource*)texture,
                0,
                null,
                data,
                (uint)(width * bytesPerPixel),
                0
            );
        }
    }

    public static ComPtr<ID3D11Texture2D> Filled(
        SpoutDevice device,
        uint frame,
        int width,
        int height
    )
    {
        ComPtr<ID3D11Texture2D> texture = CreateTexture(device, width, height);
        Upload(device, (nint)texture.Pointer, Pattern(frame, width, height), width);
        return texture;
    }

    // A device on the machine's first hardware GPU, or on the software adapter (WARP) where there is
    // none, as on CI runners. Spout works the same on either.
    public static SpoutDevice Device(
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null
    )
    {
        SpoutAdapter[] adapters = [.. SpoutDevice.GetAdapters()];
        SpoutAdapter chosen = adapters.FirstOrDefault(static a => !a.IsSoftware)
            is { Luid: not 0 } gpu
            ? gpu
            : adapters.First(static a => a.IsSoftware);
        return SpoutDevice.Create(chosen.Luid, loggerFactory);
    }

    public static int BytesPerPixel(SpoutFormat format) =>
        format switch
        {
            SpoutFormat.Rgba16Float or SpoutFormat.Rgba16Unorm => 8,
            SpoutFormat.Rgba32Float => 16,
            _ => 4,
        };

    // Reads a texture back to tightly packed bytes through a staging copy.
    public static byte[] Read(SpoutDevice device, nint texture)
    {
        D3D11_TEXTURE2D_DESC source = SharedTextures.Describe((ID3D11Texture2D*)texture);
        D3D11_TEXTURE2D_DESC description = source with
        {
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };
        ID3D11Texture2D* staging;
        device.Device->CreateTexture2D(&description, null, &staging);
        using ComPtr<ID3D11Texture2D> owned = ComPtr<ID3D11Texture2D>.Attach(staging);
        device.Context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texture);
        D3D11_MAPPED_SUBRESOURCE mapped;
        device.Context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        try
        {
            int rowBytes = (int)source.Width * BytesPerPixel((SpoutFormat)source.Format);
            byte[] pixels = new byte[rowBytes * (int)source.Height];
            for (int y = 0; y < source.Height; y++)
            {
                new ReadOnlySpan<byte>(
                    (byte*)mapped.pData + (y * mapped.RowPitch),
                    rowBytes
                ).CopyTo(pixels.AsSpan(y * rowBytes));
            }

            return pixels;
        }
        finally
        {
            device.Context->Unmap((ID3D11Resource*)staging, 0);
        }
    }

    public static D3D11Texture D3D11(this ComPtr<ID3D11Texture2D> texture) => new(texture.Address);

    // A sender name no other test run uses.
    public static string UniqueName(string what) =>
        $"Spout2.NET {what} {Environment.ProcessId}-{Guid.NewGuid():N}";
}
