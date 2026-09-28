using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Spout2.NET.Protocol;

namespace Spout2.NET;

/// <summary>A sender as the shared Spout registry describes it.</summary>
/// <param name="Name">The sender's name.</param>
/// <param name="Width">The shared texture's width.</param>
/// <param name="Height">The shared texture's height.</param>
/// <param name="Format">The shared texture's format.</param>
/// <param name="ShareHandle">
/// The texture's Direct3D shared handle, which a receiver opens on its own device; 0 when the
/// sender shares through CPU memory instead.
/// </param>
/// <param name="ExecutablePath">The sending application, as it recorded itself.</param>
/// <param name="SharesCpuMemory">The sender shares pixels through memory, not a texture.</param>
/// <param name="UsesOpenGLInterop">The sender is an OpenGL application sharing through the GL/DX interop.</param>
public readonly record struct SpoutSenderInfo(
    string Name,
    int Width,
    int Height,
    SpoutFormat Format,
    uint ShareHandle,
    string? ExecutablePath,
    bool SharesCpuMemory,
    bool UsesOpenGLInterop
)
{
    internal static SpoutSenderInfo From(string name, in SharedTextureInfo info) =>
        new(
            name,
            (int)info.Width,
            (int)info.Height,
            SpoutFormats.FromRegistry(info.Format),
            info.ShareHandle,
            SpoutName.Decode(info.Description),
            info.ShareHandle == 0 || (info.PartnerId & SharedTextureInfo.CpuSharing) != 0,
            (info.PartnerId & SharedTextureInfo.GlDxInterop) != 0
        );
}

/// <summary>
/// The senders on this machine: the registry every Spout application shares, including the active
/// sender that receivers follow when they are not given a name.
/// </summary>
public static class SpoutSenders
{
    /// <summary>The name receivers follow when not given one, or null when there is no sender.</summary>
    public static string? Active => SenderRegistry.GetActive();

    /// <summary>Every sender, sorted as the registry sorts them.</summary>
    /// <returns>The senders.</returns>
    public static ImmutableArray<SpoutSenderInfo> GetAll()
    {
        ImmutableArray<SpoutSenderInfo>.Builder senders =
            ImmutableArray.CreateBuilder<SpoutSenderInfo>();
        foreach (string name in SenderRegistry.GetNames())
        {
            if (SenderRegistry.TryReadInfo(name, out SharedTextureInfo info))
            {
                senders.Add(SpoutSenderInfo.From(name, info));
            }
        }

        return senders.ToImmutable();
    }

    /// <summary>One sender by name.</summary>
    /// <param name="name">The sender's name.</param>
    /// <param name="sender">The sender, when it exists.</param>
    /// <returns>Whether it exists.</returns>
    public static bool TryGet(string name, out SpoutSenderInfo sender)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (SenderRegistry.TryReadInfo(name, out SharedTextureInfo info))
        {
            sender = SpoutSenderInfo.From(name, info);
            return true;
        }

        sender = default;
        return false;
    }

    /// <summary>Makes a sender the one receivers without a name follow.</summary>
    /// <param name="name">The sender's name.</param>
    /// <returns>Whether the sender exists.</returns>
    public static bool TrySetActive(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return SenderRegistry.TrySetActive(name);
    }

    /// <summary>
    /// The senders each time they change: a sender starts or stops, or its size or format changes.
    /// Spout has no change notification, so the registry is read every <paramref name="interval"/>;
    /// the first result is the current set.
    /// </summary>
    /// <param name="interval">How often to read the registry.</param>
    /// <param name="cancellationToken">Ends the sequence.</param>
    /// <returns>The senders, each time they change.</returns>
    public static IAsyncEnumerable<ImmutableArray<SpoutSenderInfo>> WatchAsync(
        TimeSpan interval,
        CancellationToken cancellationToken = default
    )
    {
        // Checked here, not in the iterator, which would only run at the first MoveNextAsync.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return Watch(interval, cancellationToken);
    }

    private static async IAsyncEnumerable<ImmutableArray<SpoutSenderInfo>> Watch(
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using PeriodicTimer timer = new(interval);
        ImmutableArray<SpoutSenderInfo> last = [];
        bool first = true;
        do
        {
            ImmutableArray<SpoutSenderInfo> current = GetAll();
            if (first || !current.SequenceEqual(last))
            {
                first = false;
                last = current;
                yield return current;
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }
}
