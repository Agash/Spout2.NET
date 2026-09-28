using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Spout2.NET.Protocol;

// The registry every Spout application shares (spoutSenderNames in the SDK):
//
// - "SpoutSenderNames": MaxSenders slots of 256 bytes, each a NUL-terminated sender name, sorted
//   byte-wise and ended by the first empty slot.
// - "ActiveSenderName": 256 bytes, the name receivers follow when not given one.
// - "<sender>": the sender's SharedTextureInfo. The map exists while the sender's process holds it,
//   so a name whose map cannot be opened belongs to a sender that is gone, and is pruned.
//
// A named map lives only while some process has it open, so the two registry maps are opened once
// and held for the process's lifetime, as every SDK application holds them through its
// spoutSenderNames object. Opening and closing them per call would let a write vanish with the handle.
internal static class SenderRegistry
{
    private const int DefaultMaxSenders = 64;

    // Spout Settings can raise the limit; every application reads the same value.
    public static int MaxSenders { get; } = ReadMaxSenders();

    private static readonly Lazy<SharedMemory> s_registry = new(() =>
        SharedMemory.CreateOrOpen(SpoutName.RegistryMap, MaxSenders * SpoutName.MaxLength)
    );

    private static readonly Lazy<SharedMemory> s_active = new(() =>
        SharedMemory.CreateOrOpen(SpoutName.ActiveSenderMap, SpoutName.MaxLength)
    );

    public static List<string> GetNames()
    {
        SharedMemory registry = s_registry.Value;
        using SharedMemory.Lock held = registry.Acquire();
        if (!held.IsHeld)
        {
            return [];
        }

        List<string> names = ReadNames(held.Bytes);
        if (names.RemoveAll(static name => !Exists(name)) > 0)
        {
            WriteNames(names, held.Bytes);
        }

        return names;
    }

    public static bool Contains(string name) => GetNames().Contains(name, StringComparer.Ordinal);

    // Adds a sender's name and makes it the active sender, as the SDK does when a sender starts.
    public static void Register(string name)
    {
        SharedMemory registry = s_registry.Value;
        using (SharedMemory.Lock held = registry.Acquire())
        {
            if (!held.IsHeld)
            {
                throw new SpoutException("The Spout sender registry is busy.");
            }

            List<string> names = ReadNames(held.Bytes);
            names.RemoveAll(static name => !Exists(name));
            if (names.Contains(name, StringComparer.Ordinal))
            {
                throw new SpoutException($"A Spout sender named \"{name}\" already exists.");
            }

            if (names.Count >= SlotCount(held.Bytes))
            {
                throw new SpoutException(
                    $"The Spout sender registry is full ({names.Count} senders)."
                );
            }

            names.Add(name);
            WriteNames(names, held.Bytes);
        }

        SetActive(name);
    }

    public static void Release(string name)
    {
        SharedMemory registry = s_registry.Value;
        using SharedMemory.Lock held = registry.Acquire();
        if (!held.IsHeld)
        {
            return;
        }

        List<string> names = ReadNames(held.Bytes);
        if (!names.Remove(name))
        {
            return;
        }

        WriteNames(names, held.Bytes);

        // The SDK hands the active slot to the first remaining sender when the active one leaves.
        if (names.Count > 0 && (GetActiveName() == name || names.Count == 1))
        {
            SetActive(names[0]);
        }
    }

    // The active sender, if it still exists.
    public static string? GetActive()
    {
        string? name = GetActiveName();
        return name is not null && Exists(name) ? name : null;
    }

    public static bool TrySetActive(string name)
    {
        if (!GetNames().Contains(name, StringComparer.Ordinal))
        {
            return false;
        }

        SetActive(name);
        return true;
    }

    public static bool TryReadInfo(string name, out SharedTextureInfo info)
    {
        info = default;
        using SharedMemory? map = SharedMemory.TryOpen(name);
        if (map is null)
        {
            return false;
        }

        using SharedMemory.Lock held = map.Acquire();
        if (!held.IsHeld)
        {
            return false;
        }

        info = MemoryMarshal.Read<SharedTextureInfo>(held.Bytes);
        return true;
    }

    private static bool Exists(string name)
    {
        using SharedMemory? map = SharedMemory.TryOpen(name);
        return map is not null;
    }

    private static string? GetActiveName()
    {
        using SharedMemory.Lock held = s_active.Value.Acquire();
        return held.IsHeld ? SpoutName.Decode(held.Bytes[..SpoutName.MaxLength]) : null;
    }

    private static void SetActive(string name)
    {
        using SharedMemory.Lock held = s_active.Value.Acquire();
        if (held.IsHeld)
        {
            SpoutName.Write(name, held.Bytes[..SpoutName.MaxLength]);
        }
    }

    // Another application may have created the registry with a different limit; the map's view is
    // at least what it asked for, so the smaller of the two bounds the slots.
    private static int SlotCount(ReadOnlySpan<byte> registry) =>
        Math.Min(MaxSenders, registry.Length / SpoutName.MaxLength);

    private static List<string> ReadNames(ReadOnlySpan<byte> registry)
    {
        List<string> names = [];
        int slots = SlotCount(registry);
        for (int i = 0; i < slots; i++)
        {
            string? name = SpoutName.Decode(
                registry.Slice(i * SpoutName.MaxLength, SpoutName.MaxLength)
            );
            if (name is null)
            {
                break;
            }

            names.Add(name);
        }

        return names;
    }

    private static void WriteNames(List<string> names, Span<byte> registry)
    {
        names.Sort(SpoutName.CompareOrdinalBytes);
        int slots = SlotCount(registry);
        int count = Math.Min(names.Count, slots);
        for (int i = 0; i < count; i++)
        {
            SpoutName.Write(names[i], registry.Slice(i * SpoutName.MaxLength, SpoutName.MaxLength));
        }

        if (count < slots)
        {
            registry[count * SpoutName.MaxLength] = 0;
        }
    }

    private static int ReadMaxSenders()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Leading Edge\Spout");
        return key?.GetValue("MaxSenders") is int value && value > 0 ? value : DefaultMaxSenders;
    }
}
