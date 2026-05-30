using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Spout2.NET.Tests")]

namespace Spout2.NET;

internal static class AssemblyInitializer
{
    // A single SetDllImportResolver call (microseconds) is the canonical AOT-safe way to locate
    // the bundled native shim. CA2255 warns against [ModuleInitializer] in libraries; the trade-off
    // is acceptable as there is no other entry point a consumer must call.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(AssemblyInitializer).Assembly,
            static (name, asm, path) =>
            {
                if (name is not Interop.SpoutNative.Lib) return 0;
                // Packaged as runtimes/win-x64/native/spout_shim.dll (resolved by the runtime),
                // or copied next to the assembly for local builds and tests.
                if (NativeLibrary.TryLoad("spout_shim.dll", asm, path, out nint h)) return h;
                if (NativeLibrary.TryLoad("spout_shim", asm, path, out h)) return h;
                return 0;
            });
    }
}
