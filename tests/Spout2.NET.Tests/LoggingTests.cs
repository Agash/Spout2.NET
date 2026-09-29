using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Spout2.NET.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace Spout2.NET.Tests;

/// <summary>
/// What senders and receivers log through the device's logger factory: the lifecycle an operator
/// needs to follow (published, connected, lost, left) and the faults, each once.
/// </summary>
[TestClass]
public sealed class LoggingTests
{
    private const int Width = 32;
    private const int Height = 16;

    [TestMethod]
    public void SenderAndReceiver_LogTheirLifecycle()
    {
        CapturingLoggerFactory logs = new();
        using SpoutDevice device = Gpu.Device(logs);
        string name = Gpu.UniqueName("logging");
        SpoutSender sender = new(name, device);
        using (ComPtr<ID3D11Texture2D> source = Gpu.Filled(device, 1, Width, Height))
        {
            Assert.IsTrue(sender.Send(source.D3D11()));
        }

        using (ComPtr<ID3D11Texture2D> larger = Gpu.Filled(device, 2, Width * 2, Height))
        {
            Assert.IsTrue(sender.Send(larger.D3D11()));
        }

        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Receive(receiver);
        sender.Dispose();
        Assert.AreEqual(SpoutReceiveResult.NoSender, receiver.TryReceive(out _));
        Assert.AreEqual(SpoutReceiveResult.NoSender, receiver.TryReceive(out _));

        Assert.AreEqual(1, logs.Count(1, "SpoutDevice"));
        LogEntry published = logs.Single(10);
        Assert.AreEqual(LogLevel.Information, published.Level);
        StringAssert.Contains(published.Message, name);
        StringAssert.Contains(published.Message, $"{Width}x{Height}");
        StringAssert.Contains(logs.Single(11).Message, $"{Width * 2}x{Height}");
        StringAssert.Contains(logs.Single(15).Message, "2 frames");
        StringAssert.Contains(logs.Single(30).Message, name);
        Assert.AreEqual(LogLevel.Information, logs.Single(32).Level);
        Assert.IsFalse(logs.Entries.Any(static e => e.Level >= LogLevel.Warning));
    }

    [TestMethod]
    public void Receiver_LogsASenderItCannotOpenOnce()
    {
        SpoutAdapter[] gpus = [.. SpoutDevice.GetAdapters().Where(static a => !a.IsSoftware)];
        SpoutAdapter software = SpoutDevice.GetAdapters().FirstOrDefault(static a => a.IsSoftware);
        if (gpus.Length == 0 || software.Luid == 0)
        {
            Assert.Inconclusive("Needs a GPU and the software adapter.");
        }

        using SpoutDevice sending = SpoutDevice.Create(gpus[0].Luid);
        string name = Gpu.UniqueName("logging other gpu");
        using SpoutSender sender = new(name, sending);
        using (ComPtr<ID3D11Texture2D> source = Gpu.Filled(sending, 1, Width, Height))
        {
            Assert.IsTrue(sender.Send(source.D3D11()));
        }

        CapturingLoggerFactory logs = new();
        using SpoutDevice receiving = SpoutDevice.Create(software.Luid, logs);
        using SpoutReceiver receiver = new(receiving, new() { SenderName = name });
        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(SpoutReceiveResult.OtherAdapter, receiver.TryReceive(out _));
        }

        LogEntry refused = logs.Single(33);
        Assert.AreEqual(LogLevel.Warning, refused.Level);
        StringAssert.Contains(refused.Message, name);
    }

    [TestMethod]
    public async Task RunAsync_LogsTheFailureItFaultsWith()
    {
        CapturingLoggerFactory logs = new();
        using SpoutDevice device = Gpu.Device(logs);
        string name = Gpu.UniqueName("logging run");
        using SpoutSender sender = new(name, device);
        using (ComPtr<ID3D11Texture2D> source = Gpu.Filled(device, 1, Width, Height))
        {
            Assert.IsTrue(sender.Send(source.D3D11()));
        }

        using SpoutReceiver receiver = new(device, new() { SenderName = name });
        Task run = receiver.RunAsync(
            static (in SpoutFrame _) => throw new InvalidDataException("handler")
        );
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => run);

        LogEntry failed = logs.Single(39);
        Assert.AreEqual(LogLevel.Error, failed.Level);
        Assert.IsInstanceOfType<InvalidDataException>(failed.Exception);
    }

    private static void Receive(SpoutReceiver receiver)
    {
        Assert.AreEqual(SpoutReceiveResult.Received, receiver.TryReceive(out SpoutFrame frame));
        frame.Dispose();
    }

    private sealed record LogEntry(
        string Category,
        int EventId,
        LogLevel Level,
        string Message,
        Exception? Exception
    );

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries;

        public LogEntry Single(int eventId) => Entries.Single(e => e.EventId == eventId);

        public int Count(int eventId, string category) =>
            Entries.Count(e =>
                e.EventId == eventId && e.Category.EndsWith(category, StringComparison.Ordinal)
            );

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            ) =>
                entries.Enqueue(
                    new(category, eventId.Id, logLevel, formatter(state, exception), exception)
                );
        }
    }
}
