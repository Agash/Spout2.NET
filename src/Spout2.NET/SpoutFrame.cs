using Spout2.NET.Direct3D;
using Spout2.NET.OpenGL;
using Windows.Win32.Graphics.Direct3D11;

namespace Spout2.NET;

/// <summary>
/// A frame borrowed from a sender: the sender's own shared texture, opened on the receiver's device,
/// under Spout's lock. The sender cannot publish until the frame is disposed. Scoped to the call that
/// received it, since the lock is a Win32 mutex owned by the receiving thread; keep a frame past that
/// with <see cref="Retain"/>.
/// </summary>
public readonly unsafe ref struct SpoutFrame : IDisposable
{
    private readonly SpoutReceiver? _receiver;
    private readonly SpoutReceiver.Connection? _connection;

    internal SpoutFrame(
        SpoutReceiver receiver,
        SpoutReceiver.Connection connection,
        long frameNumber,
        bool isNew,
        bool senderChanged,
        long observedAt
    )
    {
        _receiver = receiver;
        _connection = connection;
        FrameNumber = frameNumber;
        IsNew = isNew;
        SenderChanged = senderChanged;
        ObservedAtNanoseconds = observedAt;
    }

    /// <summary>The sender.</summary>
    public SpoutSenderInfo Sender => Connection.Info;

    /// <summary>
    /// The sender's texture on the receiver's device, not AddRef'd: valid until the frame is disposed.
    /// Read it; the sender writes it. On a Direct3D 12 device it is a Direct3D 11 on 12 texture: copy it
    /// with <see cref="CopyTo(D3D12Texture)"/>.
    /// </summary>
    public D3D11Texture Texture => new(Connection.Texture.Address);

    /// <summary>The frame's width.</summary>
    public int Width => Connection.Info.Width;

    /// <summary>The frame's height.</summary>
    public int Height => Connection.Info.Height;

    /// <summary>The frame's format.</summary>
    public SpoutFormat Format => Connection.Info.Format;

    /// <summary>The sender's frame count at this frame, or -1 for a sender that does not count frames.</summary>
    public long FrameNumber { get; }

    /// <summary>Whether the sender published this frame since the receiver's previous one.</summary>
    public bool IsNew { get; }

    /// <summary>
    /// Whether the sender, its size or its format changed since the receiver's previous frame; the
    /// texture is then a different object.
    /// </summary>
    public bool SenderChanged { get; }

    /// <summary>
    /// When the receiver took the frame, in nanoseconds of the monotonic clock
    /// (<see cref="System.Diagnostics.Stopwatch"/>). Spout carries no capture time, so this is when the
    /// frame was observed, not when it was produced.
    /// </summary>
    public long ObservedAtNanoseconds { get; }

    private SpoutReceiver.Connection Connection =>
        _connection ?? throw new InvalidOperationException("The frame was not received.");

    /// <summary>
    /// Copies the frame on the GPU into a Direct3D 11 texture of the application's, for example an
    /// encoder's input surface.
    /// </summary>
    /// <param name="destination">
    /// A texture on the receiver's device, of the frame's size and a copy-compatible format.
    /// </param>
    public void CopyTo(D3D11Texture destination)
    {
        SpoutReceiver receiver = _receiver!;
        D3D11_TEXTURE2D_DESC target = SharedTextures.DescribeOwn(
            receiver.Device,
            destination.NativePointer,
            nameof(destination)
        );
        CheckSize(target, nameof(destination));
        SharedTextures.Copy(
            receiver.Device,
            (ID3D11Texture2D*)destination.NativePointer,
            Connection.Texture.Pointer
        );
    }

    /// <summary>
    /// Copies the frame on the GPU into a Direct3D 12 texture of the application's, through Direct3D 11
    /// on 12, submitted to the device's command queue.
    /// </summary>
    /// <param name="destination">
    /// A texture on the <c>ID3D12Device</c> the receiver's device was made from, of the frame's size and a
    /// copy-compatible format; it is left in the state it was handed over in.
    /// </param>
    public void CopyTo(D3D12Texture destination)
    {
        SpoutReceiver receiver = _receiver!;
        receiver.Device.RequireApi(SpoutGraphicsApi.Direct3D12, nameof(destination));
        CheckSize(receiver.Device.D3D12!.Describe(destination), nameof(destination));
        receiver.Device.D3D12.CopyTo(receiver.Device, destination, Connection.Texture.Pointer);
    }

    /// <summary>
    /// The OpenGL texture linked to the sender's, on a device for OpenGL: read it on the context's thread
    /// while the frame is held, with no copy. Its rows are in Direct3D order: the first is the top of the
    /// image.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not for OpenGL.</exception>
    public OpenGLTexture OpenGLTexture =>
        Connection.OpenGL?.Texture
        ?? throw new InvalidOperationException("The receiver's device is not for OpenGL.");

    /// <summary>
    /// Copies the frame into an OpenGL texture of the device's context with a framebuffer blit, on the
    /// thread the context is current on.
    /// </summary>
    /// <param name="destination">A texture of the frame's size.</param>
    /// <param name="flip">
    /// Flip rows on the way, so an image upright to Direct3D is upright in OpenGL, as the Spout SDK's
    /// OpenGL receivers do by default.
    /// </param>
    public void CopyTo(OpenGLTexture destination, bool flip = true)
    {
        SpoutReceiver receiver = _receiver!;
        receiver.Device.RequireApi(SpoutGraphicsApi.OpenGL, nameof(destination));
        OpenGLBridge gl = receiver.Device.OpenGL!;
        (int width, int height) = gl.Size(destination);
        if (width != Width || height != Height)
        {
            throw new ArgumentException(
                $"The destination is {width}x{height}; the frame is {Width}x{Height}.",
                nameof(destination)
            );
        }

        gl.Blit(OpenGLTexture, destination, Width, Height, flip);
    }

    /// <summary>
    /// Keeps the frame past the borrow: copies it on the GPU into a texture the receiver owns and pools.
    /// </summary>
    /// <returns>The copy, returned to the pool when disposed.</returns>
    public SpoutFrameLease Retain() =>
        _receiver!.Retain(Connection, FrameNumber, ObservedAtNanoseconds);

    private void CheckSize(D3D11_TEXTURE2D_DESC target, string parameter)
    {
        if (target.Width != (uint)Width || target.Height != (uint)Height)
        {
            throw new ArgumentException(
                $"The destination is {target.Width}x{target.Height}; the frame is {Width}x{Height}.",
                parameter
            );
        }
    }

    /// <summary>Releases the sender's texture.</summary>
    public void Dispose()
    {
        if (_connection is not null)
        {
            _receiver!.EndFrame(_connection);
        }
    }
}

/// <summary>A copy of a frame, kept past its borrow (<see cref="SpoutFrame.Retain"/>).</summary>
public sealed unsafe class SpoutFrameLease : IDisposable
{
    private readonly SpoutDevice _device;
    private readonly TexturePool _pool;
    private ComPtr<ID3D11Texture2D>? _texture;
    private OpenGLBridge.Link? _openGL;

    internal SpoutFrameLease(
        SpoutDevice device,
        TexturePool pool,
        ComPtr<ID3D11Texture2D> texture,
        SpoutSenderInfo sender,
        long frameNumber,
        long observedAt
    )
    {
        _device = device;
        _pool = pool;
        _texture = texture;
        Sender = sender;
        FrameNumber = frameNumber;
        ObservedAtNanoseconds = observedAt;
    }

    /// <summary>The sender the frame came from.</summary>
    public SpoutSenderInfo Sender { get; }

    /// <summary>The copy on the receiver's device, valid until disposed.</summary>
    public D3D11Texture Texture => new(Held.Address);

    /// <summary>The frame's width.</summary>
    public int Width => Sender.Width;

    /// <summary>The frame's height.</summary>
    public int Height => Sender.Height;

    /// <summary>The frame's format.</summary>
    public SpoutFormat Format => Sender.Format;

    /// <summary>The sender's frame count at the frame, or -1 when it does not count frames.</summary>
    public long FrameNumber { get; }

    /// <summary>When the frame was observed, in nanoseconds of the monotonic clock.</summary>
    public long ObservedAtNanoseconds { get; }

    private ComPtr<ID3D11Texture2D> Held
    {
        get
        {
            ObjectDisposedException.ThrowIf(_texture is null, this);
            return _texture;
        }
    }

    /// <summary>Copies the frame on the GPU into a Direct3D 11 texture of the application's.</summary>
    /// <param name="destination">
    /// A texture on the receiver's device, of the frame's size and a copy-compatible format.
    /// </param>
    public void CopyTo(D3D11Texture destination)
    {
        ComPtr<ID3D11Texture2D> texture = Held;
        D3D11_TEXTURE2D_DESC target = SharedTextures.DescribeOwn(
            _device,
            destination.NativePointer,
            nameof(destination)
        );
        CheckSize((int)target.Width, (int)target.Height, nameof(destination));
        SharedTextures.Copy(_device, (ID3D11Texture2D*)destination.NativePointer, texture.Pointer);
    }

    /// <summary>
    /// Copies the frame on the GPU into a Direct3D 12 texture of the application's, through Direct3D 11
    /// on 12, submitted to the device's command queue.
    /// </summary>
    /// <param name="destination">
    /// A texture on the <c>ID3D12Device</c> the receiver's device was made from, of the frame's size and a
    /// copy-compatible format; it is left in the state it was handed over in.
    /// </param>
    public void CopyTo(D3D12Texture destination)
    {
        ComPtr<ID3D11Texture2D> texture = Held;
        _device.RequireApi(SpoutGraphicsApi.Direct3D12, nameof(destination));
        D3D11_TEXTURE2D_DESC target = _device.D3D12!.Describe(destination);
        CheckSize((int)target.Width, (int)target.Height, nameof(destination));
        _device.D3D12.CopyTo(_device, destination, texture.Pointer);
    }

    /// <summary>
    /// Copies the frame into an OpenGL texture of the device's context with a framebuffer blit, on the
    /// thread the context is current on.
    /// </summary>
    /// <param name="destination">A texture of the frame's size.</param>
    /// <param name="flip">
    /// Flip rows on the way, so an image upright to Direct3D is upright in OpenGL, as the Spout SDK's
    /// OpenGL receivers do by default.
    /// </param>
    public void CopyTo(OpenGLTexture destination, bool flip = true)
    {
        ComPtr<ID3D11Texture2D> texture = Held;
        _device.RequireApi(SpoutGraphicsApi.OpenGL, nameof(destination));
        OpenGLBridge gl = _device.OpenGL!;
        (int width, int height) = gl.Size(destination);
        CheckSize(width, height, nameof(destination));
        _openGL ??= gl.LinkTo(texture.Pointer);
        if (!_openGL.TryLock())
        {
            throw new SpoutException("The kept frame could not be locked for OpenGL.");
        }

        try
        {
            gl.Blit(_openGL.Texture, destination, Width, Height, flip);
        }
        finally
        {
            _openGL.Unlock();
        }
    }

    /// <summary>Returns the copy to the receiver's pool.</summary>
    public void Dispose()
    {
        ComPtr<ID3D11Texture2D>? texture = Interlocked.Exchange(ref _texture, null);
        if (texture is not null)
        {
            _openGL?.Dispose();
            _pool.Return(texture, Width, Height, Format);
        }
    }

    private void CheckSize(int width, int height, string parameter)
    {
        if (width != Width || height != Height)
        {
            throw new ArgumentException(
                $"The destination is {width}x{height}; the frame is {Width}x{Height}.",
                parameter
            );
        }
    }
}
