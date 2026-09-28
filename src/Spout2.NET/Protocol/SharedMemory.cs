using System.IO.MemoryMappedFiles;

namespace Spout2.NET.Protocol;

// A named shared memory map with the guard mutex Spout pairs with every map ("<name>_mutex"). Spout
// waits 67 ms for the guard (four frames at 60 Hz) and treats a timeout as "try again".
internal sealed unsafe class SharedMemory : IDisposable
{
    public static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(67);

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _guard;
    private readonly byte* _data;

    private SharedMemory(string name, MemoryMappedFile map)
    {
        _map = map;
        try
        {
            _view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            _guard = new Mutex(false, SpoutName.MapMutex(name));
            byte* data = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref data);
            _data = data + _view.PointerOffset;
            Name = name;
        }
        catch
        {
            _view?.Dispose();
            map.Dispose();
            throw;
        }
    }

    public string Name { get; }

    // The view's size, which the system rounds up to whole pages; the map's own size is what its
    // creator asked for, so readers take the layout's size, not this.
    public long Capacity => _view.Capacity;

    // Creates the map, or opens it when another process already has: Spout's CreateFileMapping
    // semantics, where the creator decides the size.
    public static SharedMemory CreateOrOpen(string name, int size) =>
        new(name, MemoryMappedFile.CreateOrOpen(name, size, MemoryMappedFileAccess.ReadWrite));

    public static SharedMemory? TryOpen(string name)
    {
        try
        {
            return new(name, MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite));
        }
        catch (FileNotFoundException)
        {
            // Deliberately not logged: a missing map is the answer (no such sender), not a fault.
            return null;
        }
    }

    // Takes the guard for reading or writing. Returns an empty lock when another process held it
    // for the whole timeout, which Spout callers treat as "not now".
    public Lock Acquire() => Acquire(LockTimeout);

    public Lock Acquire(TimeSpan timeout)
    {
        bool owned;
        try
        {
            owned = _guard.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // A process died holding the guard; the wait still transferred ownership to us. The data
            // is a few names and sizes that the next writer rewrites whole.
            owned = true;
        }

        return owned ? new Lock(this) : default;
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
        _guard.Dispose();
    }

    // Holding the guard. The guard is a Win32 mutex, owned by the thread that took it, so the lock
    // is a ref struct: it cannot outlive the call or move to another thread across an await.
    public readonly ref struct Lock : IDisposable
    {
        private readonly SharedMemory? _memory;

        internal Lock(SharedMemory memory) => _memory = memory;

        public bool IsHeld => _memory is not null;

        public Span<byte> Bytes =>
            _memory is null
                ? throw new InvalidOperationException("The shared memory guard is not held.")
                : new(_memory._data, (int)_memory._view.Capacity);

        public void Dispose() => _memory?._guard.ReleaseMutex();
    }
}
