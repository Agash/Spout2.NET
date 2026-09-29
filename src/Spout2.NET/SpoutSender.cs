using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Spout2.NET.Direct3D;
using Spout2.NET.OpenGL;
using Spout2.NET.Protocol;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Direct3D12;

namespace Spout2.NET;

/// <summary>How a sender publishes.</summary>
public sealed record SpoutSenderOptions
{
    /// <summary>
    /// Signal Spout's frame-sync event with every frame, for a receiver that paces itself on this
    /// sender. The event wakes one waiting receiver per frame, so it suits one receiver.
    /// </summary>
    public bool SignalFrameSync { get; init; }
}

/// <summary>
/// Shares frames with other applications under a name, as a Direct3D 11 shared texture: a Spout
/// sender. OBS, Resolume, TouchDesigner and other Spout applications see it by that name.
/// </summary>
/// <remarks>
/// The sender appears in the Spout registry with its first frame, when its size and format are known,
/// and leaves it when disposed. A sender is used from one thread at a time.
/// </remarks>
public sealed unsafe partial class SpoutSender : IDisposable
{
    private readonly ILogger<SpoutSender> _logger;
    private readonly SpoutSenderOptions _options;
    private ComPtr<ID3D11Texture2D>? _texture;

    // The shared texture opened on the application's Direct3D 12 device, on a device for Direct3D 12.
    private ComPtr<ID3D12Resource>? _sharedD3D12;
    private SharedMemory? _info;
    private TextureAccess? _access;
    private OpenGLBridge.Link? _glLink;
    private FrameCounter? _counter;
    private EventWaitHandle? _sync;
    private MetadataBuffer? _metadata;
    private long _lastPublish;
    private bool _frameOpen;
    private int _frameThread;
    private bool _disposed;

    /// <summary>Creates a sender.</summary>
    /// <param name="name">
    /// The name other applications see. It must fit Spout's 255 bytes in the system code page, and
    /// no other sender may be using it.
    /// </param>
    /// <param name="device">The device the application's frames are on.</param>
    /// <param name="options">How the sender publishes.</param>
    /// <exception cref="SpoutException">Another sender already uses the name.</exception>
    public SpoutSender(string name, SpoutDevice device, SpoutSenderOptions? options = null)
    {
        _ = SpoutName.Encode(name);
        ArgumentNullException.ThrowIfNull(device);
        if (SenderRegistry.Contains(name))
        {
            throw new SpoutException($"A Spout sender named \"{name}\" already exists.");
        }

        Name = name;
        Device = device;
        _options = options ?? new();
        _logger = device.LoggerFactory.CreateLogger<SpoutSender>();
    }

    /// <summary>The sender's name.</summary>
    public string Name { get; }

    /// <summary>The device the shared texture is on.</summary>
    public SpoutDevice Device { get; }

    /// <summary>Whether the sender is in the registry, which it enters with its first frame.</summary>
    public bool IsPublished => _texture is not null;

    /// <summary>The shared texture's width, or 0 before the first frame.</summary>
    public int Width { get; private set; }

    /// <summary>The shared texture's height, or 0 before the first frame.</summary>
    public int Height { get; private set; }

    /// <summary>The shared texture's format.</summary>
    public SpoutFormat Format { get; private set; }

    /// <summary>
    /// The shared texture on <see cref="Device"/>, valid until the next frame that changes the size or
    /// format; a null pointer before the first frame.
    /// </summary>
    public D3D11Texture SharedTexture => new(_texture is null ? 0 : _texture.Address);

    /// <summary>Frames published so far.</summary>
    public long FrameNumber { get; private set; }

    /// <summary>The publishing rate, measured over recent frames; 0 before two frames.</summary>
    public double FramesPerSecond { get; private set; }

    /// <summary>
    /// Publishes a copy of a Direct3D 11 texture as the next frame, copied on the GPU into the shared
    /// texture, which follows the texture's size and format.
    /// </summary>
    /// <param name="texture">The texture, on <see cref="Device"/>.</param>
    /// <returns>
    /// Whether the frame was published; false when a receiver held the shared texture for Spout's whole
    /// timeout (67 ms), and the frame was dropped.
    /// </returns>
    public bool Send(D3D11Texture texture)
    {
        ThrowIfUnusable();
        D3D11_TEXTURE2D_DESC description = SharedTextures.DescribeOwn(
            Device,
            texture.NativePointer,
            nameof(texture)
        );
        return Publish(
            description,
            shared =>
                SharedTextures.Copy(
                    Device,
                    (ID3D11Texture2D*)shared,
                    (ID3D11Texture2D*)texture.NativePointer
                )
        );
    }

    /// <summary>
    /// Publishes a copy of a Direct3D 12 texture as the next frame, copied on the GPU into the shared
    /// texture by a command list on the device's queue, after the application's work on it.
    /// </summary>
    /// <param name="texture">
    /// The texture, on the <c>ID3D12Device</c> <see cref="Device"/> was made from
    /// (<see cref="SpoutDevice.FromD3D12Device"/>), in <see cref="D3D12Texture.State"/>; it is left in
    /// that state.
    /// </param>
    /// <returns>
    /// Whether the frame was published; false when a receiver held the shared texture for Spout's whole
    /// timeout (67 ms), and the frame was dropped.
    /// </returns>
    public bool Send(D3D12Texture texture)
    {
        ThrowIfUnusable();
        Device.RequireApi(SpoutGraphicsApi.Direct3D12, nameof(texture));
        D3D12Copier copier = Device.D3D12!;
        (int width, int height, SpoutFormat format) = copier.Describe(texture);
        D3D11_TEXTURE2D_DESC frame = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = (Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT)format,
        };
        return Publish(
            frame,
            _ =>
                copier.Copy(
                    _sharedD3D12!.Pointer,
                    D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
                    (ID3D12Resource*)texture.Resource,
                    (D3D12_RESOURCE_STATES)texture.State
                )
        );
    }

    /// <summary>
    /// Publishes a copy of an OpenGL texture as the next frame: a framebuffer blit into the OpenGL texture
    /// linked to the shared texture, which follows the texture's size and is 8-bit BGRA. Call it on the
    /// thread the device's OpenGL context is current on.
    /// </summary>
    /// <param name="texture">A texture of the device's OpenGL context.</param>
    /// <param name="flip">
    /// Flip rows on the way, so an image OpenGL shows upright is upright to Direct3D receivers, as the
    /// Spout SDK's OpenGL senders do by default.
    /// </param>
    /// <returns>
    /// Whether the frame was published; false when a receiver held the shared texture for Spout's whole
    /// timeout (67 ms), and the frame was dropped.
    /// </returns>
    public bool Send(OpenGLTexture texture, bool flip = true)
    {
        ThrowIfUnusable();
        Device.RequireApi(SpoutGraphicsApi.OpenGL, nameof(texture));
        OpenGLBridge gl = Device.OpenGL!;
        (int width, int height) = gl.Size(texture);
        D3D11_TEXTURE2D_DESC frame = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = (Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT)SpoutFormat.Bgra8Unorm,
        };
        return Publish(
            frame,
            _ =>
            {
                if (!_glLink!.TryLock())
                {
                    throw new SpoutException("The shared texture could not be locked for OpenGL.");
                }

                try
                {
                    gl.Blit(texture, _glLink.Texture, width, height, flip);

                    // Finished before the texture goes back to Direct3D, whose wait comes next.
                    GL.glFinish();
                }
                finally
                {
                    _glLink.Unlock();
                }
            }
        );
    }

    /// <summary>
    /// Takes the shared texture for the application to render the next frame into directly, with no
    /// copy. The frame is published by <see cref="SpoutSenderFrame.Publish"/>; disposing it unpublished
    /// discards it. Receivers wait while the frame is open, so keep it short.
    /// </summary>
    /// <param name="width">The frame's width; the shared texture is resized to it.</param>
    /// <param name="height">The frame's height.</param>
    /// <param name="format">The frame's format.</param>
    /// <param name="frame">The open frame.</param>
    /// <returns>Whether the texture was taken; false when a receiver held it for Spout's whole timeout.</returns>
    public bool TryBeginFrame(int width, int height, SpoutFormat format, out SpoutSenderFrame frame)
    {
        ThrowIfUnusable();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (format == SpoutFormat.Unknown)
        {
            throw new ArgumentException("A frame needs a format.", nameof(format));
        }

        EnsureTexture(width, height, format);
        if (!TryEnterAccess())
        {
            frame = default;
            return false;
        }

        if (_glLink is not null && !_glLink.TryLock())
        {
            _access!.Exit();
            LogOpenGLLockFailed(Name);
            frame = default;
            return false;
        }

        _frameOpen = true;
        _frameThread = Environment.CurrentManagedThreadId;
        frame = new SpoutSenderFrame(this);
        return true;
    }

    /// <summary>
    /// Replaces the sender's shared memory buffer, which receivers read with
    /// <see cref="SpoutReceiver.ReadMetadata"/>. Spout applications use it for text such as a source
    /// description; the buffer's size is fixed by its first write, at least 4 KiB.
    /// </summary>
    /// <param name="data">The data, at most the buffer's size.</param>
    public void WriteMetadata(ReadOnlySpan<byte> data)
    {
        ThrowIfUnusable();
        if (!IsPublished)
        {
            throw new InvalidOperationException(
                "The sender has no metadata before its first frame."
            );
        }

        _metadata ??= MetadataBuffer.Create(Name, Math.Max(data.Length, 4096));
        _metadata.Write(data);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_texture is not null)
        {
            SenderRegistry.Release(Name);
            LogLeft(Name, FrameNumber);
        }

        _metadata?.Dispose();
        _sync?.Dispose();
        _counter?.Dispose();
        _access?.Dispose();
        _info?.Dispose();
        _glLink?.Dispose();
        _sharedD3D12?.Dispose();
        _texture?.Dispose();
    }

    /// <summary>
    /// The OpenGL texture linked to the shared texture, on a device for OpenGL; valid until the next frame
    /// that changes the size.
    /// </summary>
    internal OpenGLTexture? LinkedTexture => _glLink?.Texture;

    // The shared texture on the application's Direct3D 12 device, on a device for Direct3D 12.
    internal nint? SharedD3D12 => _sharedD3D12?.Address;

    internal void EndFrame(bool publish)
    {
        if (!_frameOpen)
        {
            return;
        }

        // Spout's lock is a Win32 mutex, owned by the thread that took it. A ref struct keeps the
        // frame on that thread only where the compiler forbids ref structs across await, which
        // runtime-async code does not; so the thread is checked here.
        if (Environment.CurrentManagedThreadId != _frameThread)
        {
            throw new InvalidOperationException(
                "A Spout frame must be published or disposed on the thread that opened it; do not await while holding it."
            );
        }

        try
        {
            // Unlocking the OpenGL link hands the texture back to Direct3D with the GL work finished.
            if (_glLink is not null)
            {
                if (publish)
                {
                    GL.glFinish();
                }

                _glLink.Unlock();
            }

            if (publish)
            {
                WaitForGpu(applicationWork: true);
                Published();
            }
        }
        finally
        {
            _frameOpen = false;
            _access!.Exit();
        }
    }

    // Sizes the shared texture to the frame, takes Spout's lock, copies the frame in, and publishes it.
    private bool Publish(D3D11_TEXTURE2D_DESC frame, Action<nint> copy)
    {
        EnsureTexture((int)frame.Width, (int)frame.Height, (SpoutFormat)frame.Format);
        if (!TryEnterAccess())
        {
            return false;
        }

        try
        {
            copy((nint)_texture!.Pointer);
            WaitForGpu(applicationWork: false);
            Published();
        }
        finally
        {
            _access!.Exit();
        }

        return true;
    }

    private void Published()
    {
        _ = _counter!.Increment();
        FrameNumber++;
        long now = Stopwatch.GetTimestamp();
        if (_lastPublish != 0)
        {
            double interval = Stopwatch.GetElapsedTime(_lastPublish, now).TotalSeconds;
            if (interval > 0)
            {
                // The SDK smooths the rate the same way: 5% of each new measurement.
                double rate = 1 / interval;
                FramesPerSecond =
                    FramesPerSecond == 0 ? rate : (0.95 * FramesPerSecond) + (0.05 * rate);
            }
        }

        _lastPublish = now;
        _sync?.Set();
    }

    // Creates the shared texture and registers the sender on the first frame, and recreates the
    // texture when the size or format changes, as spoutDX::CheckSender does.
    private void EnsureTexture(int width, int height, SpoutFormat format)
    {
        if (_texture is not null && width == Width && height == Height && format == Format)
        {
            return;
        }

        ComPtr<ID3D11Texture2D> texture = SharedTextures.CreateShared(
            Device,
            width,
            height,
            format,
            out uint shareHandle
        );
        bool first = _texture is null;
        ComPtr<ID3D12Resource>? sharedD3D12 = null;
        OpenGLBridge.Link? glLink = null;
        try
        {
            sharedD3D12 = Device.D3D12?.Open(shareHandle);
            glLink = Device.OpenGL?.LinkTo(texture.Pointer);
            if (first)
            {
                _info = SharedMemory.CreateOrOpen(Name, SharedTextureInfo.Size);
                WriteInfo(width, height, format, shareHandle);
                _access = TextureAccess.For(Name, texture);
                _counter = FrameCounter.OpenOrCreate(Name);
                if (_options.SignalFrameSync)
                {
                    _sync = new EventWaitHandle(
                        false,
                        EventResetMode.AutoReset,
                        SpoutName.SyncEvent(Name)
                    );
                }

                SenderRegistry.Register(Name);
                LogRegistered(Name, width, height, format, Device.AdapterName);
            }
            else
            {
                WriteInfo(width, height, format, shareHandle);
                LogResized(Name, width, height, format);
            }
        }
        catch
        {
            glLink?.Dispose();
            sharedD3D12?.Dispose();
            texture.Dispose();
            if (first)
            {
                _sync?.Dispose();
                _counter?.Dispose();
                _access?.Dispose();
                _info?.Dispose();
                _sync = null;
                _counter = null;
                _access = null;
                _info = null;
            }

            throw;
        }

        _glLink?.Dispose();
        _glLink = glLink;
        _sharedD3D12?.Dispose();
        _sharedD3D12 = sharedD3D12;

        _texture?.Dispose();
        _texture = texture;
        Width = width;
        Height = height;
        Format = format;
    }

    private void WriteInfo(int width, int height, SpoutFormat format, uint shareHandle)
    {
        SharedTextureInfo info = new()
        {
            ShareHandle = shareHandle,
            Width = (uint)width,
            Height = (uint)height,
            Format = (uint)format,
        };

        // The SDK records the sending executable's path, which Spout tools show beside the name.
        _ = SpoutName.TryWrite(Environment.ProcessPath ?? string.Empty, info.Description);
        using SharedMemory.Lock held = _info!.Acquire();
        if (!held.IsHeld)
        {
            throw new SpoutException("The sender's registry entry is busy.");
        }

        MemoryMarshal.Write(held.Bytes, in info);
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOpen)
        {
            throw new InvalidOperationException("A frame is open; publish or dispose it first.");
        }
    }

    // Waits for the GPU work on the shared texture before the frame is counted and Spout's lock is
    // released (SpoutDevice.WaitForGpu says why). A Direct3D 12 copy has waited already; a frame the
    // application rendered on its Direct3D 12 queue is waited for there.
    private void WaitForGpu(bool applicationWork)
    {
        if (Device.D3D12 is { } d3d12)
        {
            if (applicationWork)
            {
                d3d12.Drain();
            }
        }
        else
        {
            Device.WaitForGpu();
        }
    }

    private bool TryEnterAccess()
    {
        switch (_access!.TryEnter())
        {
            case AccessResult.Held:
                return true;
            case AccessResult.Abandoned:
                LogAbandoned(Name);
                return true;
            default:
                LogDropped(Name);
                return false;
        }
    }

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "Spout sender \"{Name}\" published {Width}x{Height} {Format} on {AdapterName}"
    )]
    private partial void LogRegistered(
        string name,
        int width,
        int height,
        SpoutFormat format,
        string adapterName
    );

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Debug,
        Message = "Spout sender \"{Name}\" resized its shared texture to {Width}x{Height} {Format}"
    )]
    private partial void LogResized(string name, int width, int height, SpoutFormat format);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Debug,
        Message = "Spout sender \"{Name}\" dropped a frame: a receiver held the shared texture for Spout's whole timeout"
    )]
    private partial void LogDropped(string name);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "Spout sender \"{Name}\" took the shared texture's lock from a process that died holding it"
    )]
    private partial void LogAbandoned(string name);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Warning,
        Message = "Spout sender \"{Name}\" dropped a frame: the shared texture could not be locked for OpenGL"
    )]
    private partial void LogOpenGLLockFailed(string name);

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Information,
        Message = "Spout sender \"{Name}\" left after {Frames} frames"
    )]
    private partial void LogLeft(string name, long frames);
}

/// <summary>
/// The shared texture, held for rendering the next frame (<see cref="SpoutSender.TryBeginFrame"/>).
/// Receivers wait until it is published or discarded. Scoped to the call that opened it: Spout's
/// lock is a Win32 mutex, which belongs to the thread that took it.
/// </summary>
public readonly ref struct SpoutSenderFrame : IDisposable
{
    private readonly SpoutSender? _sender;

    internal SpoutSenderFrame(SpoutSender sender) => _sender = sender;

    /// <summary>The shared texture to render into, on the sender's device.</summary>
    public D3D11Texture Texture => Sender.SharedTexture;

    /// <summary>
    /// The OpenGL texture linked to the shared texture, to render into on a device for OpenGL. Its rows
    /// are in Direct3D order: the first is the top of the image.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not for OpenGL.</exception>
    public OpenGLTexture OpenGLTexture =>
        Sender.LinkedTexture
        ?? throw new InvalidOperationException("The sender's device is not for OpenGL.");

    /// <summary>
    /// The shared texture as a resource of the application's Direct3D 12 device, to render into on a
    /// device for Direct3D 12, in <see cref="D3D12ResourceState.Common"/>: transition it back to that
    /// state by the end of the work submitted before <see cref="Publish"/>, which waits for that work.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not for Direct3D 12.</exception>
    public D3D12Texture D3D12Texture =>
        Sender.SharedD3D12 is { } resource
            ? new(resource)
            : throw new InvalidOperationException("The sender's device is not for Direct3D 12.");

    /// <summary>The frame's width.</summary>
    public int Width => Sender.Width;

    /// <summary>The frame's height.</summary>
    public int Height => Sender.Height;

    /// <summary>The frame's format.</summary>
    public SpoutFormat Format => Sender.Format;

    private SpoutSender Sender =>
        _sender ?? throw new InvalidOperationException("The frame was not opened.");

    /// <summary>Publishes what was rendered as the next frame and releases the texture.</summary>
    public void Publish() => Sender.EndFrame(publish: true);

    /// <summary>Releases the texture, discarding the frame unless it was published.</summary>
    public void Dispose() => _sender?.EndFrame(publish: false);
}
