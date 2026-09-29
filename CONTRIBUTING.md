# Contributing

Thanks for your interest in Spout2.NET.

## Building

```sh
git clone --recursive https://github.com/Agash/Spout2.NET
cd Spout2.NET
dotnet build Spout2.NET.slnx
pwsh tests/SpoutPeer/build.ps1
dotnet test --solution Spout2.NET.slnx
```

The build targets .NET 11 and treats warnings as errors.

## How it is built

The protocol's pieces live in
`src/Spout2.NET/Protocol` (the shared registry, the texture information record, the access lock, the
frame counter, the sender memory buffer), Direct3D 11 in `src/Spout2.NET/Direct3D`, and the public API
at the top level. Windows APIs come from CsWin32: list them in `src/Spout2.NET/NativeMethods.txt` rather
than declaring them by hand. Kernel objects use .NET's own named `Mutex`, `Semaphore`, `EventWaitHandle`
and `MemoryMappedFile`, which are the same Win32 objects the Spout SDK creates.

The Spout SDK in `external/Spout2` is the reference. A change to protocol behaviour starts from the SDK
source that does the same thing, and says in a comment which SDK function it follows.

## Tests

The tests need a Direct3D 11 device; on a machine without a GPU they run on WARP, Windows' software
rasterizer. `InteropTests` run the upstream SDK in another process (`tests/SpoutPeer`, built with MSVC by
`build.ps1`) and check that each side receives the other byte-exact; they fail when the peer is not
built rather than skipping, so a green run always includes them.

## Pull requests

Keep changes focused. Make sure the build is clean and the non-GPU tests pass.

## License

By contributing you agree that your contributions are licensed under the MIT License.

## House rules

- **Warnings are errors.** `TreatWarningsAsErrors` is on. Fix the diagnostic rather than suppressing
  it; a `NoWarn` or `#pragma` needs a comment saying why the rule genuinely does not apply.
- **Nullable reference types are enabled** everywhere. No `!` without a reason.
- **All I/O is async**, with a `CancellationToken` accepted and propagated. No `.Result`,
  `.GetAwaiter().GetResult()`, or `Thread.Sleep`.
- **Public API carries XML documentation.**
- **The package is trim- and AOT-clean.** `IsAotCompatible` is set, so the trim and AOT analyzers run
  on every build. Serialization goes through a source-generated `JsonSerializerContext`, never the
  reflection-based `JsonSerializer` overloads.

## Tests

- Name tests `{Method}_{Scenario}_{ExpectedResult}`.
- Prefer the purpose-built MSTest assertions (`Assert.HasCount`, `Assert.Contains`,
  `Assert.AreSequenceEqual`) over hand-rolled equality checks; the analyzers will point you at them.
- No `Thread.Sleep`. Use `TaskCompletionSource`, channels, or a fake clock.
- New behaviour needs a test. Bug fixes need a test that fails before the fix.

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

```
fix(webhooks): reject a signature computed over the decoded body
```

Keep the subject under 50 characters and in the imperative mood. Add a body only when the reason for
the change would not be obvious to the next reader — explain *why*, not *what*.

One logical change per commit. Rebase rather than merge when updating a branch.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating you are
expected to uphold it.

## Reporting security issues

Please do not open a public issue. See [SECURITY.md](SECURITY.md).
