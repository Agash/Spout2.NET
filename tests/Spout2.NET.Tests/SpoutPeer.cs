using System.Diagnostics;

namespace Spout2.NET.Tests;

// The upstream Spout SDK in a separate process (tests/SpoutPeer), the independent implementation the
// interop tests check Spout2.NET against. Built by tests/SpoutPeer/build.ps1.
internal sealed class SpoutPeer : IAsyncDisposable
{
    private readonly Process _process;

    private SpoutPeer(Process process) => _process = process;

    public static string Executable { get; } = FindExecutable();

    public static void Require()
    {
        if (!File.Exists(Executable))
        {
            Assert.Fail(
                $"The Spout SDK peer is not built ({Executable}). Run tests/SpoutPeer/build.ps1."
            );
        }
    }

    // Starts an SDK sender and waits for its first frame. It sends until disposed.
    public static async Task<SpoutPeer> StartSenderAsync(
        string name,
        int width,
        int height,
        CancellationToken cancellationToken
    )
    {
        Process process = Start("send", name, width.ToString(), height.ToString());
        SpoutPeer peer = new(process);
        string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        if (line is null || !line.StartsWith("READY ", StringComparison.Ordinal))
        {
            await peer.DisposeAsync();
            Assert.Fail($"The SDK sender did not start: {line}");
        }

        // Keep reading so the pipe never fills and blocks the sender.
        _ = Task.Run(
            () => process.StandardOutput.ReadToEndAsync(CancellationToken.None),
            CancellationToken.None
        );
        return peer;
    }

    // Runs an SDK receiver until it has received `frames` new frames or `timeout` passes.
    public static async Task<PeerReceipt> ReceiveAsync(
        string? name,
        int frames,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        using Process process = Start(
            "receive",
            name ?? "-",
            frames.ToString(),
            ((int)timeout.TotalMilliseconds).ToString()
        );
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        List<PeerFrame> received = [];
        string? metadata = null;
        foreach (
            string line in output.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            string[] parts = line.Split(' ');
            if (parts[0] == "META")
            {
                metadata = line.Length > 5 ? line[5..] : string.Empty;
            }
            else if (parts[0] == "FRAME")
            {
                received.Add(
                    new PeerFrame(
                        long.Parse(parts[1]),
                        int.Parse(parts[2]),
                        int.Parse(parts[3]),
                        uint.Parse(parts[4]),
                        ulong.Parse(parts[5])
                    )
                );
            }
        }

        return new PeerReceipt(process.ExitCode, received, metadata, output);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.StandardInput.Close();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // The sender did not leave on its own; a test must not hang on it.
                _process.Kill();
            }
        }

        _process.Dispose();
    }

    private static Process Start(params string[] arguments)
    {
        ProcessStartInfo start = new(Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException("The SDK peer did not start.");
    }

    private static string FindExecutable()
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        for (
            string? dir = AppContext.BaseDirectory;
            dir is not null;
            dir = Path.GetDirectoryName(dir)
        )
        {
            if (File.Exists(Path.Combine(dir, "Spout2.NET.slnx")))
            {
                return Path.Combine(dir, "tests", "SpoutPeer", "bin", arch, "spout_peer.exe");
            }
        }

        return "spout_peer.exe";
    }
}

// A frame the SDK receiver took: the sender's frame count, the size, the frame index the pixels carry,
// and a hash of the pixels.
internal readonly record struct PeerFrame(
    long SenderFrame,
    int Width,
    int Height,
    uint Index,
    ulong Hash
);

internal sealed record PeerReceipt(
    int ExitCode,
    List<PeerFrame> Frames,
    string? Metadata,
    string Output
);
