# Third-party notices

## Spout2

- Project: https://github.com/leadedge/Spout2
- License: BSD 2-Clause
- Copyright (c) 2014-2025, Lynn Jarvis. All rights reserved.

Spout2.NET implements the Spout protocol in C#. The Spout SDK, a git submodule under `external/Spout2`,
is the reference for that protocol (the sender registry and texture information layout, the object
names, the frame count and access lock semantics, the sender memory buffer), and it is built into a test
peer (`tests/SpoutPeer`) that the interop tests run against. No Spout SDK code is compiled into the
Spout2.NET package. The SDK's license text travels with the submodule.
