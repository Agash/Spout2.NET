using Spout2.NET.Direct3D;
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
    /// The sender's texture (<c>ID3D11Texture2D*</c>) on the receiver's device, not AddRef'd: valid
    /// until the frame is disposed. Read it; the sender writes it.
    /// </summary>
    public nint Texture => (nint)Connection.Texture.Pointer;

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
    /// Copies the frame on the GPU into a texture of the application's, for example an encoder's
    /// input surface.
    /// </summary>
    /// <param name="destination">
    /// The <c>ID3D11Texture2D*</c>, on the receiver's device, of the frame's size and a
    /// copy-compatible format.
    /// </param>
    public void CopyTo(nint destination)
    {
        SpoutReceiver receiver = _receiver!;
        D3D11_TEXTURE2D_DESC target = SharedTextures.DescribeOwn(
            receiver.Device,
            destination,
            nameof(destination)
        );
        if (target.Width != (uint)Width || target.Height != (uint)Height)
        {
            throw new ArgumentException(
                $"The destination is {target.Width}x{target.Height}; the frame is {Width}x{Height}.",
                nameof(destination)
            );
        }

        SharedTextures.Copy(
            receiver.Device,
            (ID3D11Texture2D*)destination,
            Connection.Texture.Pointer
        );
    }

    /// <summary>
    /// Keeps the frame past the borrow: copies it on the GPU into a texture the receiver owns and pools.
    /// </summary>
    /// <returns>The copy, returned to the pool when disposed.</returns>
    public SpoutFrameLease Retain() =>
        _receiver!.Retain(Connection, FrameNumber, ObservedAtNanoseconds);

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
    private readonly TexturePool _pool;
    private ComPtr<ID3D11Texture2D>? _texture;

    internal SpoutFrameLease(
        TexturePool pool,
        ComPtr<ID3D11Texture2D> texture,
        SpoutSenderInfo sender,
        long frameNumber,
        long observedAt
    )
    {
        _pool = pool;
        _texture = texture;
        Sender = sender;
        FrameNumber = frameNumber;
        ObservedAtNanoseconds = observedAt;
    }

    /// <summary>The sender the frame came from.</summary>
    public SpoutSenderInfo Sender { get; }

    /// <summary>The copy (<c>ID3D11Texture2D*</c>) on the receiver's device, valid until disposed.</summary>
    public nint Texture
    {
        get
        {
            ObjectDisposedException.ThrowIf(_texture is null, this);
            return (nint)_texture.Pointer;
        }
    }

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

    /// <summary>Returns the copy to the receiver's pool.</summary>
    public void Dispose()
    {
        ComPtr<ID3D11Texture2D>? texture = Interlocked.Exchange(ref _texture, null);
        if (texture is not null)
        {
            _pool.Return(texture, Width, Height, Format);
        }
    }
}
