using System.Diagnostics;
using System.Runtime.InteropServices;
using Spout2.NET.Direct3D;
using Spout2.NET.Protocol;
using Windows.Win32.Graphics.Direct3D11;

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
public sealed unsafe class SpoutSender : IDisposable
{
    private readonly SpoutSenderOptions _options;
    private ComPtr<ID3D11Texture2D>? _texture;
    private SharedMemory? _info;
    private TextureAccess? _access;
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
    /// The shared texture (<c>ID3D11Texture2D*</c>), valid until the next frame that changes the size
    /// or format; 0 before the first frame.
    /// </summary>
    public nint Texture => _texture is null ? 0 : (nint)_texture.Pointer;

    /// <summary>Frames published so far.</summary>
    public long FrameNumber { get; private set; }

    /// <summary>The publishing rate, measured over recent frames; 0 before two frames.</summary>
    public double FramesPerSecond { get; private set; }

    /// <summary>
    /// Publishes a copy of a texture as the next frame, copied on the GPU into the shared texture. The
    /// shared texture follows the texture's size and format.
    /// </summary>
    /// <param name="texture">The <c>ID3D11Texture2D*</c>, on <see cref="Device"/>.</param>
    /// <returns>
    /// Whether the frame was published; false when a receiver held the texture for Spout's whole
    /// timeout (67 ms), and the frame was dropped.
    /// </returns>
    public bool Send(nint texture)
    {
        ThrowIfUnusable();
        D3D11_TEXTURE2D_DESC description = SharedTextures.DescribeOwn(
            Device,
            texture,
            nameof(texture)
        );
        EnsureTexture(
            (int)description.Width,
            (int)description.Height,
            (SpoutFormat)description.Format
        );
        if (!_access!.TryEnter())
        {
            return false;
        }

        try
        {
            SharedTextures.Copy(Device, _texture!.Pointer, (ID3D11Texture2D*)texture);
            Published();
        }
        finally
        {
            _access.Exit();
        }

        return true;
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
        if (!_access!.TryEnter())
        {
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
        }

        _metadata?.Dispose();
        _sync?.Dispose();
        _counter?.Dispose();
        _access?.Dispose();
        _info?.Dispose();
        _texture?.Dispose();
    }

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
            if (publish)
            {
                Device.Context->Flush();
                Published();
            }
        }
        finally
        {
            _frameOpen = false;
            _access!.Exit();
        }
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
        try
        {
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
            }
            else
            {
                WriteInfo(width, height, format, shareHandle);
            }
        }
        catch
        {
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

    /// <summary>The <c>ID3D11Texture2D*</c> to render into, on the sender's device.</summary>
    public nint Texture => Sender.Texture;

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
