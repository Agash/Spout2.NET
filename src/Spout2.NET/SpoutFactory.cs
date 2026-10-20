namespace Spout2.NET;

/// <summary>
/// Makes senders and receivers on one device with the application's clock, and watches the registry on
/// it. <see cref="Microsoft.Extensions.DependencyInjection.SpoutServiceCollectionExtensions.AddSpout"/>
/// registers one.
/// </summary>
/// <param name="device">The device senders and receivers share on.</param>
/// <param name="timeProvider">The clock; <see cref="TimeProvider.System"/> by default.</param>
public sealed class SpoutFactory(SpoutDevice device, TimeProvider? timeProvider = null)
{
    /// <summary>The device senders and receivers share on.</summary>
    public SpoutDevice Device { get; } = device ?? throw new ArgumentNullException(nameof(device));

    /// <summary>The clock senders and receivers are made with.</summary>
    public TimeProvider TimeProvider { get; } = timeProvider ?? TimeProvider.System;

    /// <summary>Creates a sender on <see cref="Device"/>.</summary>
    /// <param name="name">The name other applications see it by.</param>
    /// <param name="options">How it publishes; the factory's clock when null.</param>
    /// <returns>The sender.</returns>
    public SpoutSender CreateSender(string name, SpoutSenderOptions? options = null) =>
        new(name, Device, options ?? new SpoutSenderOptions { TimeProvider = TimeProvider });

    /// <summary>Creates a receiver on <see cref="Device"/>.</summary>
    /// <param name="options">Which sender and how to wait for frames; the factory's clock when null.</param>
    /// <returns>The receiver.</returns>
    public SpoutReceiver CreateReceiver(SpoutReceiverOptions? options = null) =>
        new(Device, options ?? new SpoutReceiverOptions { TimeProvider = TimeProvider });

    /// <summary>The senders each time they change, read on the factory's clock.</summary>
    /// <param name="interval">How often to read the registry.</param>
    /// <param name="cancellationToken">Ends the sequence.</param>
    /// <returns>The senders, each time they change.</returns>
    public IAsyncEnumerable<System.Collections.Immutable.ImmutableArray<SpoutSenderInfo>> WatchSendersAsync(
        TimeSpan interval,
        CancellationToken cancellationToken = default
    ) => SpoutSenders.WatchAsync(interval, TimeProvider, cancellationToken);
}
