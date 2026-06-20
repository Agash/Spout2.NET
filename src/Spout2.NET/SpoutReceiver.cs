using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spout2.NET.Interop;

namespace Spout2.NET;

/// <summary>
/// Receives video frames from a Spout sender into a DirectX 11 texture. Call <see cref="Receive"/>
/// on your cadence, then read <see cref="Texture"/> (an <c>ID3D11Texture2D*</c> Spout owns and reuses).
/// When <see cref="IsUpdated"/> is true the sender's size or format changed and the texture was
/// recreated, so refresh anything that depends on its dimensions.
/// </summary>
public sealed partial class SpoutReceiver : IDisposable
{
    private readonly ILogger _logger;
    private nint _handle;
    private nint _texture;
    private bool _wasConnected;

    /// <summary>
    /// Create a receiver on <paramref name="d3d11Device"/> (an <c>ID3D11Device*</c>; pass
    /// <see cref="nint.Zero"/> to let Spout create its own). If <paramref name="senderName"/> is null
    /// or empty, the active sender is received; otherwise the named sender.
    /// </summary>
    /// <param name="d3d11Device">An <c>ID3D11Device*</c>, or <see cref="nint.Zero"/> to let Spout create one.</param>
    /// <param name="senderName">Sender to receive, or null/empty for the active sender.</param>
    /// <param name="loggerFactory">Optional factory for Debug/Trace diagnostics; omit for none.</param>
    public SpoutReceiver(nint d3d11Device, string? senderName = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger($"Spout2.NET.Receiver.{(string.IsNullOrEmpty(senderName) ? "active" : senderName)}");
        _handle = SpoutNative.sp_create();
        if (_handle == 0)
            throw new InvalidOperationException("Failed to create the Spout receiver.");
        if (SpoutNative.sp_open_directx11(_handle, d3d11Device) == 0)
        {
            Dispose();
            throw new InvalidOperationException("Failed to open DirectX 11 for the Spout receiver.");
        }
        SpoutNative.sp_set_receiver_name(_handle, string.IsNullOrEmpty(senderName) ? null : senderName);
        LogCreated(string.IsNullOrEmpty(senderName) ? "(active sender)" : senderName);
    }

    /// <summary>The <c>ID3D11Device*</c> in use (the one supplied, or the one Spout created).</summary>
    public nint Device
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return SpoutNative.sp_get_device(_handle);
        }
    }

    /// <summary>
    /// Pull from the sender, updating <see cref="Texture"/>. Returns true while connected to a sender.
    /// </summary>
    public bool Receive()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        bool ok = SpoutNative.sp_receive_texture(_handle, out _texture) != 0;

        // Log connection transitions and size/format changes (not every frame) so --verbose shows when a
        // sender appears/disappears and when the texture geometry is renegotiated.
        bool connected = IsConnected;
        if (connected && !_wasConnected) LogConnected(SenderWidth, SenderHeight);
        else if (!connected && _wasConnected) LogDisconnected();
        else if (connected && IsUpdated) LogUpdated(SenderWidth, SenderHeight);
        _wasConnected = connected;

        return ok;
    }

    /// <summary>The most recently received texture (<c>ID3D11Texture2D*</c>), owned by Spout. May be zero.</summary>
    public nint Texture => _texture;

    /// <summary>True when the sender's size or format changed on the last <see cref="Receive"/>.</summary>
    public bool IsUpdated => _handle != 0 && SpoutNative.sp_is_updated(_handle) != 0;

    /// <summary>True while connected to a live sender.</summary>
    public bool IsConnected => _handle != 0 && SpoutNative.sp_is_connected(_handle) != 0;

    /// <summary>True when the last received frame is new (not a repeat).</summary>
    public bool IsFrameNew => _handle != 0 && SpoutNative.sp_is_frame_new(_handle) != 0;

    /// <summary>Width of the connected sender, in pixels.</summary>
    public int SenderWidth => _handle == 0 ? 0 : (int)SpoutNative.sp_get_sender_width(_handle);

    /// <summary>Height of the connected sender, in pixels.</summary>
    public int SenderHeight => _handle == 0 ? 0 : (int)SpoutNative.sp_get_sender_height(_handle);

    /// <inheritdoc/>
    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h == 0) return;
        SpoutNative.sp_release_receiver(h);
        SpoutNative.sp_destroy(h);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "receiver created for {Sender}")]
    private partial void LogCreated(string sender);

    [LoggerMessage(Level = LogLevel.Debug, Message = "connected to sender ({Width}x{Height})")]
    private partial void LogConnected(int width, int height);

    [LoggerMessage(Level = LogLevel.Debug, Message = "sender disconnected")]
    private partial void LogDisconnected();

    [LoggerMessage(Level = LogLevel.Debug, Message = "sender geometry/format updated ({Width}x{Height})")]
    private partial void LogUpdated(int width, int height);
}
