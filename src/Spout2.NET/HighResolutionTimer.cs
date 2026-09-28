using System.Runtime.InteropServices;
using Windows.Win32;

namespace Spout2.NET;

// A waitable timer with sub-millisecond resolution (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION). A plain
// sleep or timer on Windows waits whole timer ticks, 15.6 ms by default, which would add up to a
// frame of latency to every received frame.
internal static unsafe class HighResolutionTimer
{
    private const uint TimerAllAccess = 0x1F0003;

    public static SafeHandle Create()
    {
        SafeHandle timer = Win32.CreateWaitableTimerEx(
            null,
            null,
            Win32.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            TimerAllAccess
        );
        if (timer.IsInvalid)
        {
            timer.Dispose();
            throw new SpoutException(
                "A high-resolution timer could not be created.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError())
            );
        }

        return timer;
    }

    // Waits for the interval, returning early when cancelled.
    public static void Wait(
        SafeHandle timer,
        TimeSpan interval,
        CancellationToken cancellationToken
    )
    {
        // A negative due time is relative, in 100 ns units.
        long due = -Math.Max(1, interval.Ticks);
        if (!Win32.SetWaitableTimerEx(timer, in due, 0, null, null, null, 0))
        {
            throw new SpoutException(
                "The high-resolution timer could not be set.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError())
            );
        }

        using TimerWaitHandle wait = new(timer);
        _ = WaitHandle.WaitAny([wait, cancellationToken.WaitHandle]);
    }

    // Lets the timer take part in WaitHandle.WaitAny with the cancellation token's handle.
    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeHandle timer)
        {
            bool added = false;
            timer.DangerousAddRef(ref added);
            Timer = timer;
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(
                timer.DangerousGetHandle(),
                ownsHandle: false
            );
        }

        private SafeHandle Timer { get; }

        protected override void Dispose(bool explicitDisposing)
        {
            base.Dispose(explicitDisposing);
            Timer.DangerousRelease();
        }
    }
}
