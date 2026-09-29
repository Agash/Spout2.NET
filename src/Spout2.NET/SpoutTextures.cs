namespace Spout2.NET;

/// <summary>The graphics API a <see cref="SpoutDevice"/> serves.</summary>
public enum SpoutGraphicsApi
{
    /// <summary>Direct3D 11: textures are shared as they are.</summary>
    Direct3D11,

    /// <summary>
    /// Direct3D 12: resources reach Spout's Direct3D 11 shared textures through Direct3D 11 on 12, as
    /// the Spout SDK's SpoutDX12 does, with one GPU copy each way.
    /// </summary>
    Direct3D12,

    /// <summary>
    /// OpenGL: textures are linked to Spout's shared textures through <c>WGL_NV_DX_interop2</c>, as the
    /// Spout SDK's OpenGL classes do.
    /// </summary>
    OpenGL,
}

/// <summary>A Direct3D 11 texture.</summary>
/// <param name="NativePointer">The <c>ID3D11Texture2D*</c>.</param>
public readonly record struct D3D11Texture(nint NativePointer);

/// <summary>A Direct3D 12 texture resource and the state it is in when Spout2.NET uses it.</summary>
/// <param name="Resource">The <c>ID3D12Resource*</c>, a 2D texture.</param>
/// <param name="State">
/// The resource's state (<c>D3D12_RESOURCE_STATES</c>) when handed over; Spout2.NET leaves it in the
/// same state.
/// </param>
public readonly record struct D3D12Texture(
    nint Resource,
    D3D12ResourceState State = D3D12ResourceState.Common
);

/// <summary>An OpenGL texture of the current context.</summary>
/// <param name="Name">The texture name.</param>
/// <param name="Target">The texture target: <see cref="Texture2D"/> or <see cref="TextureRectangle"/>.</param>
public readonly record struct OpenGLTexture(uint Name, uint Target = OpenGLTexture.Texture2D)
{
    /// <summary><c>GL_TEXTURE_2D</c>.</summary>
    public const uint Texture2D = 0x0DE1;

    /// <summary><c>GL_TEXTURE_RECTANGLE</c>.</summary>
    public const uint TextureRectangle = 0x84F5;
}

/// <summary>
/// The states of a Direct3D 12 resource that Spout2.NET can receive it in (<c>D3D12_RESOURCE_STATES</c>).
/// </summary>
[Flags]
public enum D3D12ResourceState : uint
{
    /// <summary><c>D3D12_RESOURCE_STATE_COMMON</c>, also <c>PRESENT</c>.</summary>
    Common = 0,

    /// <summary><c>D3D12_RESOURCE_STATE_RENDER_TARGET</c>.</summary>
    RenderTarget = 0x4,

    /// <summary><c>D3D12_RESOURCE_STATE_UNORDERED_ACCESS</c>.</summary>
    UnorderedAccess = 0x8,

    /// <summary><c>D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE</c>.</summary>
    NonPixelShaderResource = 0x40,

    /// <summary><c>D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE</c>.</summary>
    PixelShaderResource = 0x80,

    /// <summary><c>D3D12_RESOURCE_STATE_COPY_DEST</c>.</summary>
    CopyDest = 0x400,

    /// <summary><c>D3D12_RESOURCE_STATE_COPY_SOURCE</c>.</summary>
    CopySource = 0x800,
}
