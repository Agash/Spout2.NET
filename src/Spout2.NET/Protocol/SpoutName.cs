using System.Text;

namespace Spout2.NET.Protocol;

// Spout stores sender names as ANSI strings in 256-byte slots and names its kernel objects after
// them through the Win32 A functions, which convert with the system ANSI code page. A name is kept
// here in both forms: the bytes the shared registry holds, and the string .NET's named objects take
// (the W functions), which is the same kernel name for the same text.
internal static class SpoutName
{
    // Slot size in the registry, terminator included.
    public const int MaxLength = 256;

    // The registry of sender names, the active sender's name, and each map's guard mutex suffix.
    public const string RegistryMap = "SpoutSenderNames";
    public const string ActiveSenderMap = "ActiveSenderName";

    private static readonly Encoding s_ansi = CreateAnsi(EncoderFallback.ExceptionFallback);

    // For text that only describes (an executable path): unrepresentable characters become '?'.
    private static readonly Encoding s_lossyAnsi = CreateAnsi(EncoderFallback.ReplacementFallback);

    public static string AccessMutex(string sender) => sender + "_SpoutAccessMutex";

    public static string CountSemaphore(string sender) => sender + "_Count_Semaphore";

    public static string SyncEvent(string sender) => sender + "_Sync_Event";

    public static string MetadataMap(string sender) => sender + "_map";

    public static string MapMutex(string map) => map + "_mutex";

    // Checks a name the application gave: it must fit a registry slot with its terminator and hold
    // no character the ANSI code page cannot represent, since that name would reach other Spout
    // applications altered.
    public static byte[] Encode(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        byte[] bytes;
        try
        {
            bytes = s_ansi.GetBytes(name);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException(
                "The name has characters the system code page cannot represent.",
                nameof(name),
                error
            );
        }

        if (bytes.Length >= MaxLength)
        {
            throw new ArgumentException(
                $"A Spout sender name is at most {MaxLength - 1} bytes in the system code page.",
                nameof(name)
            );
        }

        if (bytes.Contains((byte)0))
        {
            throw new ArgumentException("A Spout sender name cannot contain NUL.", nameof(name));
        }

        return bytes;
    }

    // Reads a NUL-terminated name from a slot; an empty slot reads as null.
    public static string? Decode(ReadOnlySpan<byte> slot)
    {
        int end = slot.IndexOf((byte)0);
        ReadOnlySpan<byte> text = end < 0 ? slot : slot[..end];
        return text.IsEmpty ? null : s_ansi.GetString(text);
    }

    // Writes a name into a slot with its terminator. The name was validated by Encode.
    public static void Write(string name, Span<byte> slot)
    {
        slot.Clear();
        int written = s_ansi.GetBytes(name, slot[..(MaxLength - 1)]);
        slot[written] = 0;
    }

    // Writes descriptive text into a fixed field, truncated to fit with its terminator. Returns
    // whether all of it fit.
    public static bool TryWrite(string text, Span<byte> field)
    {
        field.Clear();
        byte[] bytes = s_lossyAnsi.GetBytes(text);
        int count = Math.Min(bytes.Length, field.Length - 1);
        bytes.AsSpan(0, count).CopyTo(field);
        return count == bytes.Length;
    }

    // The registry keeps its names sorted as std::set<std::string> sorts them: byte by byte.
    public static int CompareOrdinalBytes(string left, string right) =>
        s_ansi.GetBytes(left).AsSpan().SequenceCompareTo(s_ansi.GetBytes(right));

    private static Encoding CreateAnsi(EncoderFallback fallback)
    {
        // Code page 0 is the system ANSI code page, which is what the A functions and the SDK use.
        // The provider does not carry UTF-8, which is the ANSI code page on systems set to use it.
        int codePage = CodePagesEncodingProvider.Instance.GetEncoding(0)?.CodePage ?? 65001;
        return codePage == 65001
            ? Encoding.GetEncoding(65001, fallback, DecoderFallback.ReplacementFallback)
            : CodePagesEncodingProvider.Instance.GetEncoding(
                codePage,
                fallback,
                DecoderFallback.ReplacementFallback
            )!;
    }
}
