using System.Buffers.Text;

namespace Spout2.NET.Protocol;

// A sender's shared memory buffer ("<sender>_map", spoutDX::CreateMemoryBuffer). The first 16 bytes
// hold the buffer's capacity as decimal ASCII; the data follows. The buffer carries no length of its
// own: a writer terminates shorter data with NUL, which suits the text Spout applications put there,
// and readers take the whole capacity.
internal sealed class MetadataBuffer : IDisposable
{
    private const int HeaderSize = 16;

    // spoutDX creates the map 32 bytes larger than the capacity it records.
    private const int Slack = 32;

    private readonly SharedMemory _map;

    private MetadataBuffer(SharedMemory map, int capacity)
    {
        _map = map;
        Capacity = capacity;
    }

    public int Capacity { get; }

    public static MetadataBuffer Create(string sender, int capacity)
    {
        SharedMemory map = SharedMemory.CreateOrOpen(
            SpoutName.MetadataMap(sender),
            capacity + Slack
        );
        using (SharedMemory.Lock held = map.Acquire())
        {
            if (!held.IsHeld)
            {
                map.Dispose();
                throw new SpoutException("The sender's metadata buffer is busy.");
            }

            Span<byte> header = held.Bytes[..HeaderSize];
            header.Clear();
            _ = Utf8Formatter.TryFormat(capacity, header, out _);
        }

        return new(map, capacity);
    }

    public static MetadataBuffer? TryOpen(string sender)
    {
        SharedMemory? map = SharedMemory.TryOpen(SpoutName.MetadataMap(sender));
        if (map is null)
        {
            return null;
        }

        int capacity;
        using (SharedMemory.Lock held = map.Acquire())
        {
            capacity = held.IsHeld ? ReadCapacity(held.Bytes) : 0;
        }

        if (capacity <= 0)
        {
            map.Dispose();
            return null;
        }

        return new(map, capacity);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length > Capacity)
        {
            throw new ArgumentException(
                $"The metadata buffer holds {Capacity} bytes; {data.Length} were given.",
                nameof(data)
            );
        }

        using SharedMemory.Lock held = _map.Acquire();
        if (!held.IsHeld)
        {
            throw new SpoutException("The sender's metadata buffer is busy.");
        }

        Span<byte> body = held.Bytes.Slice(HeaderSize, Capacity);
        data.CopyTo(body);
        if (data.Length < body.Length)
        {
            body[data.Length] = 0;
        }
    }

    public int Read(Span<byte> destination)
    {
        using SharedMemory.Lock held = _map.Acquire();
        if (!held.IsHeld)
        {
            return 0;
        }

        int count = Math.Min(Capacity, destination.Length);
        held.Bytes.Slice(HeaderSize, count).CopyTo(destination);
        return count;
    }

    public void Dispose() => _map.Dispose();

    private static int ReadCapacity(ReadOnlySpan<byte> map)
    {
        ReadOnlySpan<byte> header = map[..(HeaderSize - 1)];
        int end = header.IndexOf((byte)0);
        return Utf8Parser.TryParse(end < 0 ? header : header[..end], out int capacity, out _)
            ? capacity
            : 0;
    }
}
