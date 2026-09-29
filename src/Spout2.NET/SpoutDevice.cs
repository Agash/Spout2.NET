using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spout2.NET.Direct3D;
using Spout2.NET.OpenGL;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;

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
public sealed unsafe partial class SpoutDevice : IDisposable
{
    private readonly ILogger<SpoutDevice> _logger;
    private readonly ComPtr<ID3D11Device> _device;
    private readonly ComPtr<ID3D11DeviceContext> _context;
    private readonly Lock _idleGate = new();
    private ComPtr<ID3D11Query>? _idle;

    private SpoutDevice(
        ComPtr<ID3D11Device> device,
        ILoggerFactory? loggerFactory,
        SpoutGraphicsApi api = SpoutGraphicsApi.Direct3D11,
        D3D12Copier? d3d12 = null,
        long? adapterLuid = null,
        OpenGLBridge? openGL = null
    )
    {
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = LoggerFactory.CreateLogger<SpoutDevice>();
        _device = device;
        Api = api;
        D3D12 = d3d12;
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
            LogCreated(Api, AdapterName, AdapterLuid);
        }
        catch
        {
            _context?.Dispose();
            d3d12?.Dispose();
            openGL?.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The <c>ID3D11Device*</c> Spout shares on, valid while this instance is: the application's own,
    /// one Spout2.NET created, or the one on the GPU of an application's Direct3D 12 or OpenGL device.
    /// </summary>
    public nint NativePointer => (nint)_device.Pointer;

    /// <summary>The graphics API the application uses this device with.</summary>
    public SpoutGraphicsApi Api { get; }

    /// <summary>The locally unique identifier of the GPU the device is on, as DXGI reports it.</summary>
    public long AdapterLuid { get; }

    /// <summary>The GPU's name, as DXGI reports it.</summary>
    public string AdapterName { get; }

    // Where the device's senders and receivers log.
    internal ILoggerFactory LoggerFactory { get; }

    internal ID3D11Device* Device => _device.Pointer;

    internal ID3D11DeviceContext* Context => _context.Pointer;

    internal D3D12Copier? D3D12 { get; }

    internal OpenGLBridge? OpenGL { get; }

    /// <summary>Creates a hardware device on a GPU.</summary>
    /// <param name="adapterLuid">The GPU's LUID, or null for the system's default GPU.</param>
    /// <param name="loggerFactory">Where the device, and its senders and receivers, log.</param>
    /// <returns>The device.</returns>
    /// <exception cref="ArgumentException">No GPU has that LUID.</exception>
    public static SpoutDevice Create(
        long? adapterLuid = null,
        ILoggerFactory? loggerFactory = null
    ) => new(CreateD3D11(adapterLuid), loggerFactory);

    /// <summary>
    /// Shares from and to the OpenGL context current on the calling thread, through
    /// <c>WGL_NV_DX_interop2</c>, as the Spout SDK's OpenGL classes do. OpenGL work then happens on the
    /// thread the context is current on; use the device from that thread only.
    /// </summary>
    /// <param name="adapterLuid">
    /// The GPU the context renders on, or null to find it: the interop opens only a Direct3D 11 device
    /// on the context's own GPU, so each is tried in turn.
    /// </param>
    /// <param name="loggerFactory">Where the device, and its senders and receivers, log.</param>
    /// <returns>The device.</returns>
    /// <exception cref="InvalidOperationException">No OpenGL context is current.</exception>
    /// <exception cref="SpoutException">The context has no <c>WGL_NV_DX_interop2</c>.</exception>
    public static SpoutDevice ForOpenGL(
        long? adapterLuid = null,
        ILoggerFactory? loggerFactory = null
    )
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
                return new(device, loggerFactory, SpoutGraphicsApi.OpenGL, null, null, bridge);
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
    /// <param name="loggerFactory">Where the device, and its senders and receivers, log.</param>
    /// <returns>The device.</returns>
    /// <exception cref="SpoutException">
    /// No GPU can open the sender's texture: it shares CPU memory, or it has stopped.
    /// </exception>
    public static SpoutDevice CreateFor(
        SpoutSenderInfo sender,
        ILoggerFactory? loggerFactory = null
    )
    {
        if (sender.ShareHandle == 0)
        {
            throw new SpoutException($"Sender \"{sender.Name}\" shares CPU memory, not a texture.");
        }

        foreach (SpoutAdapter adapter in GetAdapters())
        {
            SpoutDevice device = Create(adapter.Luid, loggerFactory);
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
    /// Shares from and to a Direct3D 12 application natively: Spout's shared textures are opened on the
    /// application's device and every copy is recorded on its queue. Spout's textures themselves are
    /// created by Direct3D 11, which only can give them the DXGI shared handle Spout publishes, so the
    /// device also holds a Direct3D 11 device on the same GPU for that and for Spout's lock.
    /// </summary>
    /// <param name="device">The application's <c>ID3D12Device*</c>. This instance holds a reference.</param>
    /// <param name="commandQueue">
    /// The <c>ID3D12CommandQueue*</c> (a direct queue) the copies are submitted to, ordered after the
    /// application's own work on it. This instance holds a reference.
    /// </param>
    /// <param name="loggerFactory">Where the device, and its senders and receivers, log.</param>
    /// <returns>The device.</returns>
    public static SpoutDevice FromD3D12Device(
        nint device,
        nint commandQueue,
        ILoggerFactory? loggerFactory = null
    )
    {
        if (device == 0)
        {
            throw new ArgumentNullException(nameof(device));
        }

        if (commandQueue == 0)
        {
            throw new ArgumentNullException(nameof(commandQueue));
        }

        LUID adapter = ((ID3D12Device*)device)->GetAdapterLuid();
        long luid = ((long)adapter.HighPart << 32) | adapter.LowPart;
        D3D12Copier copier = new((ID3D12Device*)device, (ID3D12CommandQueue*)commandQueue);
        try
        {
            return new(CreateD3D11(luid), loggerFactory, SpoutGraphicsApi.Direct3D12, copier, luid);
        }
        catch
        {
            copier.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Wraps a device the application already uses, so frames it renders are shared, and frames it
    /// receives are opened, without leaving its GPU.
    /// </summary>
    /// <param name="device">The <c>ID3D11Device*</c>. This instance holds its own reference.</param>
    /// <param name="loggerFactory">Where the device, and its senders and receivers, log.</param>
    /// <returns>The device.</returns>
    public static SpoutDevice FromD3D11Device(nint device, ILoggerFactory? loggerFactory = null) =>
        device == 0
            ? throw new ArgumentNullException(nameof(device))
            : new(ComPtr<ID3D11Device>.AddRef((ID3D11Device*)device), loggerFactory);

    /// <inheritdoc/>
    public void Dispose()
    {
        OpenGL?.Dispose();
        _idle?.Dispose();
        D3D12?.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    // Returns when the GPU has finished the work submitted on the device's context so far. Spout's lock
    // orders only the CPU: a copy still queued when the lock is released can run after the other side's
    // next one, and deliver the previous frame or the next. So every Spout2.NET copy under the lock is
    // waited for before the lock is released.
    internal void WaitForGpu()
    {
        lock (_idleGate)
        {
            if (_idle is null)
            {
                D3D11_QUERY_DESC description = new() { Query = D3D11_QUERY.D3D11_QUERY_EVENT };
                ID3D11Query* query;
                Device->CreateQuery(&description, &query);
                _idle = ComPtr<ID3D11Query>.Attach(query);
            }

            ID3D11Asynchronous* idle = (ID3D11Asynchronous*)_idle.Pointer;
            Context->End(idle);
            BOOL done = false;

            // GetData flushes the context and reports S_FALSE until the GPU reaches the query.
            while (Context->GetData(idle, &done, (uint)sizeof(BOOL), 0).Value == 1)
            {
                _ = Thread.Yield();
            }
        }
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

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Spout device for {Api} on {AdapterName} (LUID {AdapterLuid:X16})"
    )]
    private partial void LogCreated(SpoutGraphicsApi api, string adapterName, long adapterLuid);
}
