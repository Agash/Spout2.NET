namespace Spout2.NET.Protocol;

// Spout's frame count: a named semaphore ("<sender>_Count_Semaphore") created with a count of 1.
// A sender adds one per frame by taking a count and giving two back; anyone reads the count by
// taking one and giving one back, which reports the count before the give. The count is therefore
// the number of frames published, and 0 means the sender does not count frames.
internal sealed class FrameCounter : IDisposable
{
    private readonly Semaphore _semaphore;

    private FrameCounter(Semaphore semaphore) => _semaphore = semaphore;

    // Opens the sender's counter, creating it when this side is first, as both SDK ends do.
    public static FrameCounter OpenOrCreate(string sender) =>
        new(new Semaphore(1, int.MaxValue, SpoutName.CountSemaphore(sender)));

    // Publishes one frame. Returns false when the count could not be taken, which only happens when
    // another process is between its take and give.
    public bool Increment()
    {
        if (!_semaphore.WaitOne(0))
        {
            return false;
        }

        _semaphore.Release(2);
        return true;
    }

    // The number of frames published so far, or -1 when it could not be read.
    public long Read()
    {
        if (!_semaphore.WaitOne(0))
        {
            return -1;
        }

        return _semaphore.Release(1);
    }

    public void Dispose() => _semaphore.Dispose();
}
