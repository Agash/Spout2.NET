using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Spout2.NET.Direct3D;

// The Direct3D 11 side of Spout: shared textures as spoutDirectX creates and opens them.
internal static unsafe class SharedTextures
{
    // A sender's texture, shared by a DXGI shared handle (D3D11_RESOURCE_MISC_SHARED), bindable as a
    // render target and a shader resource, one mip, one slice. This is
    // spoutDirectX::CreateSharedDX11Texture with its defaults, which is what every SDK sender creates
    // and every receiver can open.
    public static ComPtr<ID3D11Texture2D> CreateShared(
        SpoutDevice device,
        int width,
        int height,
        SpoutFormat format,
        out uint shareHandle
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
                D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET
                | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED,
        };
        ID3D11Texture2D* texture;
        device.Device->CreateTexture2D(&description, null, &texture);
        ComPtr<ID3D11Texture2D> owned = ComPtr<ID3D11Texture2D>.Attach(texture);
        try
        {
            shareHandle = ShareHandleOf(owned);
            return owned;
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    // The DXGI shared handle of a texture created with D3D11_RESOURCE_MISC_SHARED. These handles are
    // machine-wide 32-bit values, so any process opens one by its number; Spout's registry stores
    // those 32 bits.
    public static uint ShareHandleOf(ComPtr<ID3D11Texture2D> texture)
    {
        using ComPtr<IDXGIResource> resource =
            texture.As<IDXGIResource>()
            ?? throw new SpoutException("The shared texture is not a DXGI resource.");
        HANDLE handle;
        resource.Pointer->GetSharedHandle(&handle);
        return (uint)(nint)handle.Value;
    }

    // Opens a sender's texture on this device. Fails when the texture lives on another GPU (or the
    // handle is stale), which Direct3D reports as an invalid argument.
    public static ComPtr<ID3D11Texture2D>? TryOpenShared(
        SpoutDevice device,
        uint shareHandle,
        out HRESULT result
    )
    {
        Guid iid = ID3D11Texture2D.IID_Guid;
        void* texture;
        result = device.Device->OpenSharedResource(new HANDLE((nint)shareHandle), &iid, &texture);
        return result.Succeeded ? ComPtr<ID3D11Texture2D>.Attach((ID3D11Texture2D*)texture) : null;
    }

    // A receiver-owned texture to copy a sender's frame into: the same size and format, bindable as a
    // shader resource and a render target, not shared.
    public static ComPtr<ID3D11Texture2D> CreateCopyTarget(
        SpoutDevice device,
        int width,
        int height,
        SpoutFormat format
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
                D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET
                | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        device.Device->CreateTexture2D(&description, null, &texture);
        return ComPtr<ID3D11Texture2D>.Attach(texture);
    }

    public static D3D11_TEXTURE2D_DESC Describe(ID3D11Texture2D* texture)
    {
        D3D11_TEXTURE2D_DESC description;
        texture->GetDesc(&description);
        return description;
    }

    // Checks that a texture the application hands over is on the device and describes a frame.
    public static D3D11_TEXTURE2D_DESC DescribeOwn(
        SpoutDevice device,
        nint texture,
        string parameter
    )
    {
        if (texture == 0)
        {
            throw new ArgumentNullException(parameter);
        }

        ID3D11Texture2D* pointer = (ID3D11Texture2D*)texture;
        ID3D11Device* owner;
        pointer->GetDevice(&owner);
        _ = owner->Release();
        if (owner != device.Device)
        {
            throw new ArgumentException(
                "The texture belongs to another Direct3D device.",
                parameter
            );
        }

        D3D11_TEXTURE2D_DESC description = Describe(pointer);
        if (description.Width == 0 || description.Height == 0)
        {
            throw new ArgumentException("The texture is empty.", parameter);
        }

        return description;
    }

    // Copies a whole texture on the device's immediate context and submits the work, so another
    // device (a receiver, or the sender's reader) sees it once the access lock is released.
    public static void Copy(
        SpoutDevice device,
        ID3D11Texture2D* destination,
        ID3D11Texture2D* source
    )
    {
        device.Context->CopyResource((ID3D11Resource*)destination, (ID3D11Resource*)source);
        device.Context->Flush();
    }
}
