# Summary

<!-- What does this change and why? -->

## Checklist

- [ ] `dotnet build` is clean (warnings are errors)
- [ ] `dotnet test --filter "TestCategory!=RequiresGpu"` passes
- [ ] If protocol behaviour changed, it follows the Spout SDK source it mirrors and the interop tests against `tests/SpoutPeer` pass
- [ ] Public API changes are documented
