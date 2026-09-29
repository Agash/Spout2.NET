using System.Diagnostics;
using System.Runtime.InteropServices;
using Spout2.NET.Direct3D;
using Spout2.NET.OpenGL;
using Spout2.NET.Protocol;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;

namespace Spout2.NET;

/// <summary>What <see cref="SpoutReceiver.TryReceive"/> found.</summary>
public enum SpoutReceiveResult
{
    /// <summary>A frame, borrowed until it is disposed.</summary>
    Received,

    /// <summary>There is no sender to receive from: none of the name, or no active sender.</summary>
    NoSender,

    /// <summary>The sender held its texture for Spout's whole timeout (67 ms); try again.</summary>
    Busy,

    /// <summary>
    /// The sender's texture is on another GPU. Spout shares textures only between devices on the
    /// same GPU; create the receiver's device on the sender's GPU.
    /// </summary>
    OtherAdapter,

    /// <summary>The sender shares pixels through CPU memory, with no texture to open.</summary>
    CpuSender,
}

/// <summary>Handles a frame a receiver delivers; the frame is valid until the handler returns.</summary>
/// <param name="frame">The frame.</param>
public delegate void SpoutFrameHandler(in SpoutFrame frame);

/// <summary>How a receiver receives.</summary>
public sealed record SpoutReceiverOptions
{
    /// <summary>
    /// The sender to receive from, or null to follow the active sender, which changes when the user
    /// picks another in Spout's tools or a new sender starts.
    /// </summary>
    public string? SenderName { get; init; }

    /// <summary>
    /// In <see cref="SpoutReceiver.RunAsync"/>, wake on the sender's frame-sync event instead of
    /// polling its frame count, for a sender that signals it (<see cref="SpoutSenderOptions.SignalFrameSync"/>).
    /// The event wakes one receiver per frame, so this suits one receiver per sender.
    /// </summary>
    public bool WaitForFrameSync { get; init; }

    /// <summary>
    /// In <see cref="SpoutReceiver.RunAsync"/>, how often the sender's frame count is read while
    /// connected. Spout has no frame notification for several receivers; a count read is one kernel
    /// call, so the default of 1 ms costs little and adds at most 1 ms of latency.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// In <see cref="SpoutReceiver.RunAsync"/>, how often to deliver frames from a sender that does not
    /// count its frames, whose new frames cannot be told apart from repeats.
    /// </summary>
    public TimeSpan UncountedFrameInterval { get; init; } = TimeSpan.FromSeconds(1.0 / 60);

    /// <summary>In <see cref="SpoutReceiver.RunAsync"/>, how often to look for a sender while none is found.</summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// Receives frames another application shares through Spout: a Spout receiver. Frames are the
/// sender's own texture, opened on this receiver's device and borrowed under Spout's lock, so reading
/// one costs no copy; <see cref="SpoutFrame.Retain"/> copies a frame to keep it past the borrow.
/// </summary>
/// <remarks>A receiver is used from one thread at a time.</remarks>
public sealed unsafe class SpoutReceiver : IDisposable
{
    private readonly SpoutReceiverOptions _options;
    private readonly TexturePool _pool;
    private Connection? _connection;
    private bool _frameOpen;
    private int _frameThread;
    private bool _running;
    private bool _disposed;

    /// <summary>Creates a receiver.</summary>
    /// <param name="device">The device frames are opened on; it must be on the sender's GPU.</param>
    /// <param name="options">Which sender, and how <see cref="RunAsync"/> waits for frames.</param>
    public SpoutReceiver(SpoutDevice device, SpoutReceiverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        _options = options ?? new();
        if (_options.SenderName is not null)
        {
            _ = SpoutName.Encode(_options.SenderName);
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _options.ReconnectInterval,
            TimeSpan.Zero
        );
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _options.UncountedFrameInterval,
            TimeSpan.Zero
        );
        Device = device;
        _pool = new TexturePool(device);
    }

    /// <summary>The device frames are opened on.</summary>
    public SpoutDevice Device { get; }

    /// <summary>The sender received from last, or null when not connected.</summary>
    public SpoutSenderInfo? Sender => _connection?.Info;

    /// <summary>Whether the last receive found a sender.</summary>
    public bool IsConnected => _connection is not null;

    /// <summary>
    /// Whether the connected sender has published a frame this receiver has not received. Reads the
    /// sender's frame count only; true while not connected or for a sender that does not count frames.
    /// </summary>
    public bool HasNewFrame
    {
        get
        {
            Connection? connection = _connection;
            if (connection is null)
            {
                return true;
            }

            long count = connection.Counter.Read();
            return count <= 0 || count != connection.LastFrame;
        }
    }

    /// <summary>
    /// Borrows the sender's current frame. The frame holds Spout's lock on the sender's texture until
    /// it is disposed, and the sender cannot publish meanwhile, so dispose it promptly and on the
    /// same thread.
    /// </summary>
    /// <param name="frame">The frame, when the result is <see cref="SpoutReceiveResult.Received"/>.</param>
    /// <returns>What was found.</returns>
    public SpoutReceiveResult TryReceive(out SpoutFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOpen)
        {
            throw new InvalidOperationException("A frame is still borrowed; dispose it first.");
        }

        frame = default;
        string? name = _options.SenderName ?? SenderRegistry.GetActive();
        if (name is null || !SenderRegistry.TryReadInfo(name, out SharedTextureInfo info))
        {
            Disconnect();
            return SpoutReceiveResult.NoSender;
        }

        SpoutSenderInfo sender = SpoutSenderInfo.From(name, info);
        if (sender.ShareHandle == 0)
        {
            Disconnect();
            return SpoutReceiveResult.CpuSender;
        }

        bool changed = false;
        if (_connection is not { } current || current.Info != sender)
        {
            Disconnect();
            ComPtr<ID3D11Texture2D>? texture = SharedTextures.TryOpenShared(
                Device,
                sender.ShareHandle,
                out HRESULT result
            );
            if (texture is null)
            {
                return result == HRESULT.E_INVALIDARG
                    ? SpoutReceiveResult.OtherAdapter
                    : throw new SpoutException(
                        $"The texture of sender \"{name}\" could not be opened ({result})."
                    );
            }

            _connection = new Connection(sender, texture, Device.OpenGL);
            changed = true;
        }

        Connection connection = _connection;
        if (!connection.Access.TryEnter())
        {
            return SpoutReceiveResult.Busy;
        }

        // On a device for OpenGL the frame is also locked for OpenGL, on this thread's context.
        if (connection.OpenGL is { } link && !link.TryLock())
        {
            connection.Access.Exit();
            return SpoutReceiveResult.Busy;
        }

        long count = connection.Counter.Read();
        bool isNew = changed || count <= 0 || count != connection.LastFrame;
        connection.LastFrame = count;
        _frameOpen = true;
        _frameThread = Environment.CurrentManagedThreadId;
        frame = new SpoutFrame(
            this,
            connection,
            count > 0 ? count : -1,
            isNew,
            changed,
            MonotonicNanoseconds()
        );
        return SpoutReceiveResult.Received;
    }

    /// <summary>
    /// Delivers the sender's frames to <paramref name="handler"/> as they are published, on a thread
    /// of the receiver's own, until cancelled. Frames the sender has not changed since the last one are
    /// not delivered. A sender that stops is waited for; a new active sender is followed.
    /// </summary>
    /// <param name="handler">Called with each new frame; the frame is valid until it returns.</param>
    /// <param name="cancellationToken">Stops receiving.</param>
    /// <returns>
    /// Completes when cancelled, or faults with the exception the handler or a receive threw.
    /// </returns>
    public Task RunAsync(SpoutFrameHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            throw new InvalidOperationException("The receiver is already running.");
        }

        _running = true;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => Run(handler, completion, cancellationToken))
        {
            IsBackground = true,
            Name = "Spout receiver",
        };
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// Copies the connected sender's shared memory buffer (<see cref="SpoutSender.WriteMetadata"/>).
    /// Spout's buffer carries no length: the copy is the whole buffer, up to the destination's size,
    /// and text in it is NUL-terminated.
    /// </summary>
    /// <param name="destination">Where to copy.</param>
    /// <returns>The bytes copied; 0 when not connected or the sender has no buffer.</returns>
    public int ReadMetadata(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Connection? connection = _connection;
        if (connection is null)
        {
            return 0;
        }

        connection.Metadata ??= MetadataBuffer.TryOpen(connection.Info.Name);
        return connection.Metadata?.Read(destination) ?? 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disconnect();
        _pool.Dispose();
    }

    internal void EndFrame(Connection connection)
    {
        if (!_frameOpen)
        {
            return;
        }

        // The lock is a Win32 mutex, owned by the receiving thread; see SpoutSender.EndFrame.
        if (Environment.CurrentManagedThreadId != _frameThread)
        {
            throw new InvalidOperationException(
                "A Spout frame must be disposed on the thread that received it; do not await while holding it."
            );
        }

        _frameOpen = false;
        connection.OpenGL?.Unlock();
        connection.Access.Exit();
    }

    internal SpoutFrameLease Retain(Connection connection, long frameNumber, long observedAt)
    {
        SpoutSenderInfo sender = connection.Info;
        ComPtr<ID3D11Texture2D> texture = _pool.Rent(sender.Width, sender.Height, sender.Format);
        SharedTextures.Copy(Device, texture.Pointer, connection.Texture.Pointer);
        return new SpoutFrameLease(_pool, texture, sender, frameNumber, observedAt);
    }

    private void Run(
        SpoutFrameHandler handler,
        TaskCompletionSource completion,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using SafeHandle timer = HighResolutionTimer.Create();
            while (!cancellationToken.IsCancellationRequested)
            {
                bool delivered = false;
                bool counted = true;
                if (!IsConnected || HasNewFrame)
                {
                    SpoutReceiveResult result = TryReceive(out SpoutFrame frame);
                    if (result == SpoutReceiveResult.Received)
                    {
                        using (frame)
                        {
                            counted = frame.FrameNumber >= 0;
                            if (frame.IsNew)
                            {
                                handler(in frame);
                                delivered = true;
                            }
                        }
                    }
                }

                if (delivered && counted)
                {
                    continue;
                }

                if (delivered)
                {
                    // Every read of an uncounted sender looks new; pace them instead of re-delivering.
                    HighResolutionTimer.Wait(
                        timer,
                        _options.UncountedFrameInterval,
                        cancellationToken
                    );
                    continue;
                }

                if (IsConnected && _options.WaitForFrameSync && WaitForSync(cancellationToken))
                {
                    continue;
                }

                HighResolutionTimer.Wait(
                    timer,
                    IsConnected ? _options.PollInterval : _options.ReconnectInterval,
                    cancellationToken
                );
            }

            completion.TrySetResult();
        }
        catch (Exception error)
        {
            // Rethrown through the task: the caller awaits RunAsync and sees the failure there.
            completion.TrySetException(error);
        }
        finally
        {
            _running = false;
        }
    }

    // Waits for the sender's frame-sync event. False when the sender does not signal one, so the
    // loop falls back to reading the frame count.
    private bool WaitForSync(CancellationToken cancellationToken)
    {
        if (
            !EventWaitHandle.TryOpenExisting(
                SpoutName.SyncEvent(_connection!.Info.Name),
                out EventWaitHandle? sync
            )
        )
        {
            return false;
        }

        using (sync)
        {
            _ = WaitHandle.WaitAny(
                [sync, cancellationToken.WaitHandle],
                _options.ReconnectInterval
            );
        }

        return true;
    }

    private void Disconnect()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private static long MonotonicNanoseconds() =>
        (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000_000 / Stopwatch.Frequency);

    // The receiver's hold on one sender: its texture opened on this device, its lock and its count.
    internal sealed class Connection : IDisposable
    {
        public Connection(
            SpoutSenderInfo info,
            ComPtr<ID3D11Texture2D> texture,
            OpenGLBridge? openGL
        )
        {
            Info = info;
            Texture = texture;
            try
            {
                Access = TextureAccess.For(info.Name, texture);
                Counter = FrameCounter.OpenOrCreate(info.Name);
                OpenGL = openGL?.LinkTo(texture.Pointer);
            }
            catch
            {
                Counter?.Dispose();
                Access?.Dispose();
                texture.Dispose();
                throw;
            }
        }

        // The OpenGL texture linked to the sender's, on a device for OpenGL.
        public OpenGLBridge.Link? OpenGL { get; }

        public SpoutSenderInfo Info { get; }

        public ComPtr<ID3D11Texture2D> Texture { get; }

        public TextureAccess Access { get; }

        public FrameCounter Counter { get; }

        public MetadataBuffer? Metadata { get; set; }

        public long LastFrame { get; set; } = -1;

        public void Dispose()
        {
            Metadata?.Dispose();
            OpenGL?.Dispose();
            Counter.Dispose();
            Access.Dispose();
            Texture.Dispose();
        }
    }
}
