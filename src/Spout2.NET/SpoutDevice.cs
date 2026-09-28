using System.Collections.Immutable;
using Spout2.NET.Direct3D;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
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
public sealed unsafe class SpoutDevice : IDisposable
{
    private readonly ComPtr<ID3D11Device> _device;
    private readonly ComPtr<ID3D11DeviceContext> _context;

    private SpoutDevice(ComPtr<ID3D11Device> device)
    {
        _device = device;
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

            using ComPtr<IDXGIDevice> dxgi =
                device.As<IDXGIDevice>()
                ?? throw new SpoutException("The device is not a DXGI device.");
            IDXGIAdapter* adapter;
            dxgi.Pointer->GetAdapter(&adapter);
            using ComPtr<IDXGIAdapter> owned = ComPtr<IDXGIAdapter>.Attach(adapter);
            DXGI_ADAPTER_DESC description = owned.Pointer->GetDesc();
            AdapterLuid =
                ((long)description.AdapterLuid.HighPart << 32) | description.AdapterLuid.LowPart;
            AdapterName = description.Description.ToString();
        }
        catch
        {
            _context?.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>The <c>ID3D11Device*</c>, valid while this instance is.</summary>
    public nint NativePointer => (nint)_device.Pointer;

    /// <summary>The locally unique identifier of the GPU the device is on, as DXGI reports it.</summary>
    public long AdapterLuid { get; }

    /// <summary>The GPU's name, as DXGI reports it.</summary>
    public string AdapterName { get; }

    internal ID3D11Device* Device => _device.Pointer;

    internal ID3D11DeviceContext* Context => _context.Pointer;

    /// <summary>Creates a hardware device on a GPU.</summary>
    /// <param name="adapterLuid">The GPU's LUID, or null for the system's default GPU.</param>
    /// <returns>The device.</returns>
    /// <exception cref="ArgumentException">No GPU has that LUID.</exception>
    public static SpoutDevice Create(long? adapterLuid = null)
    {
        using ComPtr<IDXGIAdapter1>? adapter = adapterLuid is long luid ? FindAdapter(luid) : null;
        ID3D11Device* device;
        D3D_FEATURE_LEVEL level;
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
                out level,
                null
            )
            .ThrowOnFailure();
        return new(ComPtr<ID3D11Device>.Attach(device));
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
        _context.Dispose();
        _device.Dispose();
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
