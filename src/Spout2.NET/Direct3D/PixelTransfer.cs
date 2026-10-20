using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Spout2.NET.Direct3D;

// Frames to and from CPU memory: an upload into the shared texture, and a readback through a staging
// texture the device keeps for the last shape it read.
internal sealed unsafe class PixelTransfer(SpoutDevice device) : IDisposable
{
    private readonly Lock _gate = new();
    private ComPtr<ID3D11Texture2D>? _staging;
    private (int Width, int Height, SpoutFormat Format) _shape;

    // The bytes one row of the frame takes, and the stride a caller's 0 stands for.
    public static int RowBytes(int width, SpoutFormat format) =>
        width * SpoutFormats.BytesPerPixel(format);

    // The length a buffer of rows at the stride needs: every row, the last without its padding.
    public static int Length(int width, int height, SpoutFormat format, ref int stride)
    {
        int row = RowBytes(width, format);
        if (stride == 0)
        {
            stride = row;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(stride, row);
        return (stride * (height - 1)) + row;
    }

    // UpdateSubresource reads the rows at the stride and copies them in on the GPU timeline; the flush
    // submits it, so a receiver sees it once the lock is released.
    public static void Upload(
        SpoutDevice device,
        ID3D11Texture2D* texture,
        ReadOnlySpan<byte> pixels,
        int stride
    )
    {
        fixed (byte* source = pixels)
        {
            device.Context->UpdateSubresource(
                (ID3D11Resource*)texture,
                0,
                null,
                source,
                (uint)stride,
                0
            );
        }

        device.Context->Flush();
    }

    // Copies the texture into the staging texture and maps it; Map waits for the copy. Rows are copied
    // at the destination's stride, in one block when both sides are packed alike.
    public int Read(
        ID3D11Texture2D* source,
        int width,
        int height,
        SpoutFormat format,
        Span<byte> destination,
        int stride
    )
    {
        int length = Length(width, height, format, ref stride);
        if (destination.Length < length)
        {
            throw new ArgumentException(
                $"The frame needs {length} bytes at a stride of {stride}; the destination has {destination.Length}.",
                nameof(destination)
            );
        }

        int row = RowBytes(width, format);
        lock (_gate)
        {
            if (_staging is null || _shape != (width, height, format))
            {
                _staging?.Dispose();
                _staging = null;
                _staging = CreateStaging(width, height, format);
                _shape = (width, height, format);
            }

            ID3D11Resource* staging = (ID3D11Resource*)_staging.Pointer;
            device.Context->CopyResource(staging, (ID3D11Resource*)source);
            D3D11_MAPPED_SUBRESOURCE mapped;
            device.Context->Map(staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            try
            {
                byte* rows = (byte*)mapped.pData;
                if (mapped.RowPitch == (uint)stride)
                {
                    new ReadOnlySpan<byte>(rows, length).CopyTo(destination);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                    {
                        new ReadOnlySpan<byte>(rows + ((nuint)y * mapped.RowPitch), row).CopyTo(
                            destination.Slice(y * stride, row)
                        );
                    }
                }
            }
            finally
            {
                device.Context->Unmap(staging, 0);
            }
        }

        return length;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _staging?.Dispose();
            _staging = null;
        }
    }

    private ComPtr<ID3D11Texture2D> CreateStaging(int width, int height, SpoutFormat format)
    {
        D3D11_TEXTURE2D_DESC description = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = (DXGI_FORMAT)format,
            SampleDesc = new() { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Texture2D* texture;
        device.Device->CreateTexture2D(&description, null, &texture);
        return ComPtr<ID3D11Texture2D>.Attach(texture);
    }
}
