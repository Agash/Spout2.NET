# Third-party notices

Spout2.NET distributes a native helper that statically includes parts of the Spout2 SDK.

## Spout2

- Project: https://github.com/leadedge/Spout2
- License: BSD 2-Clause
- Copyright (c) 2020-2024, Lynn Jarvis. All rights reserved.

The Spout2 SDK is included as a git submodule under `native/vendor/Spout2`. Only its BSD-licensed
DirectX and utility sources (`SpoutDX`, `SpoutCopy`, `SpoutDirectX`, `SpoutFrameCount`,
`SpoutSenderNames`, `SpoutSharedMemory`, `SpoutUtils`) are compiled into the bundled native binary
(`spout_shim.dll`); the OpenGL path is not included. The full license text is in that submodule's
`LICENSE` file.

The BSD 2-Clause license permits redistribution in binary form provided its copyright notice and
disclaimer are reproduced. This notice satisfies that requirement; the verbatim text travels with
the submodule.
