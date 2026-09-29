using System.Collections.Immutable;
using Spout2.NET.Direct3D;
using Spout2.NET.OpenGL;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D11on12;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace Spout2.NET;

/// <summary>A GPU, as DXGI enumerates it.</summary>
/// <param name="Luid">The GPU's locally unique identifier.</param>
/// <param name="Name">The GPU's name.</param>
/// <param name="IsSoftware">Whether it is a software rasterizer (WARP), not a GPU.</param>
public readonly record struct SpoutAdapter(long Luid, string Name, bool IsSoftware);

/// <summary>
/// The Direct3D 11 device a sender shares textures from or a receiver opens them on. Spout shares a
/// texture only between devices on the same GPU, so the device's adapter decides which senders a
/// receiver can open.
/// </summary>
/// <remarks>
/// Spout2.NET copies on the device's immediate context from the thread that sends or receives, so
/// the device is switched to Direct3D's multithread protection (<c>ID3D10Multithread</c>), which
/// serializes the application's own use of the context with Spout2.NET's.
/// </remarks>
public sealed unsafe class SpoutDevice : IDisposable
{
    private readonly ComPtr<ID3D11Device> _device;
    private readonly ComPtr<ID3D11DeviceContext> _context;

    private SpoutDevice(
        ComPtr<ID3D11Device> device,
        SpoutGraphicsApi api = SpoutGraphicsApi.Direct3D11,
        D3D12Bridge? bridge = null,
        long? adapterLuid = null,
        OpenGLBridge? openGL = null
    )
    {
        _device = device;
        Api = api;
        D3D12 = bridge;
        OpenGL = openGL;
        try
        {
            ID3D11DeviceContext* context;
            device.Pointer->GetImmediateContext(&context);
            _context = ComPtr<ID3D11DeviceContext>.Attach(context);
            using (ComPtr<ID3D10Multithread>? multithread = device.As<ID3D10Multithread>())
            {
                if (multithread is not null)
                {
                    _ = multithread.Pointer->SetMultithreadProtected(true);
                }
            }

            AdapterLuid = adapterLuid ?? DxgiLuid(device);
            AdapterName =
                GetAdapters().FirstOrDefault(a => a.Luid == AdapterLuid).Name ?? string.Empty;
        }
        catch
        {
            _context?.Dispose();
            bridge?.Dispose();
            openGL?.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The <c>ID3D11Device*</c> Spout shares on, valid while this instance is: the application's own,
    /// one Spout2.NET created, or the Direct3D 11 on 12 device over the application's Direct3D 12 device.
    /// </summary>
    public nint NativePointer => (nint)_device.Pointer;

    /// <summary>The graphics API the application uses this device with.</summary>
    public SpoutGraphicsApi Api { get; }

    /// <summary>The locally unique identifier of the GPU the device is on, as DXGI reports it.</summary>
    public long AdapterLuid { get; }

    /// <summary>The GPU's name, as DXGI reports it.</summary>
    public string AdapterName { get; }

    internal ID3D11Device* Device => _device.Pointer;

    internal ID3D11DeviceContext* Context => _context.Pointer;

    internal D3D12Bridge? D3D12 { get; }

    internal OpenGLBridge? OpenGL { get; }

    /// <summary>Creates a hardware device on a GPU.</summary>
    /// <param name="adapterLuid">The GPU's LUID, or null for the system's default GPU.</param>
    /// <returns>The device.</returns>
    /// <exception cref="ArgumentException">No GPU has that LUID.</exception>
    public static SpoutDevice Create(long? adapterLuid = null) => new(CreateD3D11(adapterLuid));

    /// <summary>
    /// Shares from and to the OpenGL context current on the calling thread, through
    /// <c>WGL_NV_DX_interop2</c>, as the Spout SDK's OpenGL classes do. OpenGL work then happens on the
    /// thread the context is current on; use the device from that thread only.
    /// </summary>
    /// <param name="adapterLuid">
    /// The GPU the context renders on, or null to find it: the interop opens only a Direct3D 11 device
    /// on the context's own GPU, so each is tried in turn.
    /// </param>
    /// <returns>The device.</returns>
    /// <exception cref="InvalidOperationException">No OpenGL context is current.</exception>
    /// <exception cref="SpoutException">The context has no <c>WGL_NV_DX_interop2</c>.</exception>
    public static SpoutDevice ForOpenGL(long? adapterLuid = null)
    {
        GL gl =
            GL.Load()
            ?? throw new SpoutException(
                "The current OpenGL context has no WGL_NV_DX_interop2, which Spout shares OpenGL textures through."
            );
        long[] candidates = adapterLuid is long luid
            ? [luid]
            : [.. GetAdapters().OrderBy(static a => a.IsSoftware).Select(static a => a.Luid)];
        foreach (long candidate in candidates)
        {
            ComPtr<ID3D11Device> device = CreateD3D11(candidate);
            OpenGLBridge? bridge = OpenGLBridge.TryOpen(gl, device.Pointer);
            if (bridge is not null)
            {
                return new(device, SpoutGraphicsApi.OpenGL, null, null, bridge);
            }

            device.Dispose();
        }

        throw new SpoutException(
            "The OpenGL context opened no GPU's Direct3D 11 device for interop."
        );
    }

    /// <summary>
    /// Creates a device on the GPU a sender's texture lives on, which is the device a receiver of that
    /// sender needs: Spout shares textures only within one GPU. Each GPU is tried in turn, as the Spout
    /// SDK finds a sender's adapter.
    /// </summary>
    /// <param name="sender">The sender, from <see cref="SpoutSenders"/>.</param>
    /// <returns>The device.</returns>
    /// <exception cref="SpoutException">
    /// No GPU can open the sender's texture: it shares CPU memory, or it has stopped.
    /// </exception>
    public static SpoutDevice CreateFor(SpoutSenderInfo sender)
    {
        if (sender.ShareHandle == 0)
        {
            throw new SpoutException($"Sender \"{sender.Name}\" shares CPU memory, not a texture.");
        }

        foreach (SpoutAdapter adapter in GetAdapters())
        {
            SpoutDevice device = Create(adapter.Luid);
            using ComPtr<ID3D11Texture2D>? texture = SharedTextures.TryOpenShared(
                device,
                sender.ShareHandle,
                out _
            );
            if (texture is not null)
            {
                return device;
            }

            device.Dispose();
        }

        throw new SpoutException($"No GPU can open the texture of sender \"{sender.Name}\".");
    }

    /// <summary>The GPUs, and the software rasterizer, in DXGI's order.</summary>
    /// <returns>The adapters.</returns>
    public static ImmutableArray<SpoutAdapter> GetAdapters()
    {
        Win32.CreateDXGIFactory1(out IDXGIFactory1* factory).ThrowOnFailure();
        using ComPtr<IDXGIFactory1> owned = ComPtr<IDXGIFactory1>.Attach(factory);
        ImmutableArray<SpoutAdapter>.Builder adapters =
            ImmutableArray.CreateBuilder<SpoutAdapter>();
        for (uint index = 0; ; index++)
        {
            IDXGIAdapter1* adapter;
            HRESULT result = factory->EnumAdapters1(index, &adapter);
            if (result == HRESULT.DXGI_ERROR_NOT_FOUND)
            {
                return adapters.ToImmutable();
            }

            result.ThrowOnFailure();
            using ComPtr<IDXGIAdapter1> held = ComPtr<IDXGIAdapter1>.Attach(adapter);
            DXGI_ADAPTER_DESC1 description = adapter->GetDesc1();
            adapters.Add(
                new SpoutAdapter(
                    ((long)description.AdapterLuid.HighPart << 32)
                        | description.AdapterLuid.LowPart,
                    description.Description.ToString(),
                    description.Flags.HasFlag(DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE)
                )
            );
        }
    }

    /// <summary>
    /// Shares from and to a Direct3D 12 application: a Direct3D 11 on 12 device over the application's
    /// device, submitting to its queue, as the Spout SDK's SpoutDX12 does. Spout's shared textures are
    /// Direct3D 11 textures, which Direct3D 12 cannot open, so each frame is one GPU copy through it.
    /// </summary>
    /// <param name="device">The application's <c>ID3D12Device*</c>. This instance holds a reference.</param>
    /// <param name="commandQueue">
    /// The <c>ID3D12CommandQueue*</c> (a direct queue) the copies are submitted to, ordered with the
    /// application's own work on it. This instance holds a reference.
    /// </param>
    /// <returns>The device.</returns>
    public static SpoutDevice FromD3D12Device(nint device, nint commandQueue)
    {
        if (device == 0)
        {
            throw new ArgumentNullException(nameof(device));
        }

        if (commandQueue == 0)
        {
            throw new ArgumentNullException(nameof(commandQueue));
        }

        IUnknown* queue = (IUnknown*)commandQueue;
        ID3D11Device* on12;
        ID3D11DeviceContext* context;
        Win32
            .D3D11On12CreateDevice(
                (IUnknown*)device,
                (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                [],
                &queue,
                1,
                0,
                &on12,
                &context,
                out _
            )
            .ThrowOnFailure();
        _ = context->Release();
        ComPtr<ID3D11Device> owned = ComPtr<ID3D11Device>.Attach(on12);
        ComPtr<ID3D11On12Device>? bridge = owned.As<ID3D11On12Device>();
        if (bridge is null)
        {
            owned.Dispose();
            throw new SpoutException("The device is not a Direct3D 11 on 12 device.");
        }

        LUID luid = ((ID3D12Device*)device)->GetAdapterLuid();
        return new(
            owned,
            SpoutGraphicsApi.Direct3D12,
            new D3D12Bridge(bridge),
            ((long)luid.HighPart << 32) | luid.LowPart
        );
    }

    /// <summary>
    /// Wraps a device the application already uses, so frames it renders are shared, and frames it
    /// receives are opened, without leaving its GPU.
    /// </summary>
    /// <param name="device">The <c>ID3D11Device*</c>. This instance holds its own reference.</param>
    /// <returns>The device.</returns>
    public static SpoutDevice FromD3D11Device(nint device) =>
        device == 0
            ? throw new ArgumentNullException(nameof(device))
            : new(ComPtr<ID3D11Device>.AddRef((ID3D11Device*)device));

    /// <inheritdoc/>
    public void Dispose()
    {
        OpenGL?.Dispose();
        D3D12?.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    internal void RequireApi(SpoutGraphicsApi api, string parameter)
    {
        if (Api != api)
        {
            throw new ArgumentException(
                $"The texture is a {api} texture; the device serves {Api}.",
                parameter
            );
        }
    }

    private static ComPtr<ID3D11Device> CreateD3D11(long? adapterLuid)
    {
        using ComPtr<IDXGIAdapter1>? adapter = adapterLuid is long luid ? FindAdapter(luid) : null;
        ID3D11Device* device;
        Win32
            .D3D11CreateDevice(
                adapter is null ? null : (IDXGIAdapter*)adapter.Pointer,
                adapter is null
                    ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE
                    : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                [],
                Win32.D3D11_SDK_VERSION,
                &device,
                out _,
                null
            )
            .ThrowOnFailure();
        return ComPtr<ID3D11Device>.Attach(device);
    }

    private static long DxgiLuid(ComPtr<ID3D11Device> device)
    {
        using ComPtr<IDXGIDevice> dxgi =
            device.As<IDXGIDevice>()
            ?? throw new SpoutException("The device is not a DXGI device.");
        IDXGIAdapter* adapter;
        dxgi.Pointer->GetAdapter(&adapter);
        using ComPtr<IDXGIAdapter> owned = ComPtr<IDXGIAdapter>.Attach(adapter);
        DXGI_ADAPTER_DESC description = owned.Pointer->GetDesc();
        return ((long)description.AdapterLuid.HighPart << 32) | description.AdapterLuid.LowPart;
    }

    private static ComPtr<IDXGIAdapter1> FindAdapter(long luid)
    {
        Win32.CreateDXGIFactory1(out IDXGIFactory1* factory).ThrowOnFailure();
        using ComPtr<IDXGIFactory1> owned = ComPtr<IDXGIFactory1>.Attach(factory);
        for (uint index = 0; ; index++)
        {
            IDXGIAdapter1* adapter;
            HRESULT result = factory->EnumAdapters1(index, &adapter);
            if (result == HRESULT.DXGI_ERROR_NOT_FOUND)
            {
                throw new ArgumentException($"No GPU has the LUID {luid:X16}.", nameof(luid));
            }

            result.ThrowOnFailure();
            ComPtr<IDXGIAdapter1> candidate = ComPtr<IDXGIAdapter1>.Attach(adapter);
            DXGI_ADAPTER_DESC1 description = adapter->GetDesc1();
            if (
                (((long)description.AdapterLuid.HighPart << 32) | description.AdapterLuid.LowPart)
                == luid
            )
            {
                return candidate;
            }

            candidate.Dispose();
        }
    }
}
