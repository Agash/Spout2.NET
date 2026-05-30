# Contributing

Thanks for your interest in Spout2.NET.

## Building

```sh
git clone --recursive https://github.com/Agash/Spout2.NET
cd Spout2.NET
pwsh native/build-native.ps1
dotnet build Spout2.NET.slnx
dotnet test --filter "TestCategory!=RequiresGpu"
```

The build treats warnings as errors and targets .NET 10 (and .NET 11 preview). If you do not have
the .NET 11 preview SDK installed, build the `net10.0` target only.

## Native shim

The C++ and DirectX work lives in a small native shim under `native/`, which statically links the
Spout2 SDK (a git submodule). Its flat C ABI is declared in `native/include/spout_shim.h`; the
managed side P/Invokes it, with DirectX objects passed as opaque pointers. If you change the native
surface, update the header, the implementation, the managed `SpoutNative` declarations, and rebuild
with `native/build-native.ps1`. The native shim builds on Windows with MSVC (the Visual Studio C++
workload).

## Tests

Value-type tests run anywhere. Tests tagged `RequiresGpu` exercise the shim and a Direct3D 11 device
(a real GPU); they report Inconclusive when none is available.

## Pull requests

Keep changes focused. Make sure the build is clean and the non-GPU tests pass.

## License

By contributing you agree that your contributions are licensed under the MIT License.
