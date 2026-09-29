using System.Runtime.InteropServices;
using Windows.Win32;

namespace Spout2.NET.OpenGL;

// The OpenGL and WGL_NV_DX_interop2 entry points Spout's OpenGL path uses (spoutGL). OpenGL 1.1 comes
// from opengl32.dll; everything newer is per context, from wglGetProcAddress with the context current,
// so the table is loaded once per context.
internal sealed unsafe partial class GL
{
    public const uint Texture2D = 0x0DE1;
    public const uint TextureWidth = 0x1000;
    public const uint TextureHeight = 0x1001;
    public const uint ReadFramebuffer = 0x8CA8;
    public const uint DrawFramebuffer = 0x8CA9;
    public const uint ColorAttachment0 = 0x8CE0;
    public const uint FramebufferComplete = 0x8CD5;
    public const uint ColorBufferBit = 0x4000;
    public const uint Nearest = 0x2600;
    public const uint AccessReadWrite = 0x0001; // WGL_ACCESS_READ_WRITE_NV

    private GL(nint context) => Context = context;

    public nint Context { get; }

    public delegate* unmanaged[Stdcall]<void*, nint> DXOpenDevice { get; private init; }

    public delegate* unmanaged[Stdcall]<nint, int> DXCloseDevice { get; private init; }

    public delegate* unmanaged[Stdcall]<nint, void*, uint, uint, uint, nint> DXRegisterObject
    {
        get;
        private init;
    }

    public delegate* unmanaged[Stdcall]<nint, nint, int> DXUnregisterObject { get; private init; }

    public delegate* unmanaged[Stdcall]<nint, int, nint*, int> DXLockObjects { get; private init; }

    public delegate* unmanaged[Stdcall]<nint, int, nint*, int> DXUnlockObjects
    {
        get;
        private init;
    }

    public delegate* unmanaged[Stdcall]<int, uint*, void> GenFramebuffers { get; private init; }

    public delegate* unmanaged[Stdcall]<int, uint*, void> DeleteFramebuffers { get; private init; }

    public delegate* unmanaged[Stdcall]<uint, uint, void> BindFramebuffer { get; private init; }

    public delegate* unmanaged[Stdcall]<uint, uint, uint, uint, int, void> FramebufferTexture2D
    {
        get;
        private init;
    }

    public delegate* unmanaged[Stdcall]<uint, uint> CheckFramebufferStatus { get; private init; }

    public delegate* unmanaged[Stdcall]<
        int,
        int,
        int,
        int,
        int,
        int,
        int,
        int,
        uint,
        uint,
        void> BlitFramebuffer { get; private init; }

    // Loads the table for the calling thread's current context, or returns null when the context lacks
    // the NV_DX interop, which the Spout OpenGL path cannot do without.
    public static GL? Load()
    {
        nint context = (nint)Win32.wglGetCurrentContext().Value;
        if (context == 0)
        {
            throw new InvalidOperationException("No OpenGL context is current on this thread.");
        }

        GL gl = new(context)
        {
            DXOpenDevice = (delegate* unmanaged[Stdcall]<void*, nint>)Proc("wglDXOpenDeviceNV"),
            DXCloseDevice = (delegate* unmanaged[Stdcall]<nint, int>)Proc("wglDXCloseDeviceNV"),
            DXRegisterObject = (delegate* unmanaged[Stdcall]<
                nint,
                void*,
                uint,
                uint,
                uint,
                nint>)Proc("wglDXRegisterObjectNV"),
            DXUnregisterObject = (delegate* unmanaged[Stdcall]<nint, nint, int>)Proc(
                "wglDXUnregisterObjectNV"
            ),
            DXLockObjects = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Proc(
                "wglDXLockObjectsNV"
            ),
            DXUnlockObjects = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Proc(
                "wglDXUnlockObjectsNV"
            ),
            GenFramebuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)Proc(
                "glGenFramebuffers"
            ),
            DeleteFramebuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)Proc(
                "glDeleteFramebuffers"
            ),
            BindFramebuffer = (delegate* unmanaged[Stdcall]<uint, uint, void>)Proc(
                "glBindFramebuffer"
            ),
            FramebufferTexture2D = (delegate* unmanaged[Stdcall]<
                uint,
                uint,
                uint,
                uint,
                int,
                void>)Proc("glFramebufferTexture2D"),
            CheckFramebufferStatus = (delegate* unmanaged[Stdcall]<uint, uint>)Proc(
                "glCheckFramebufferStatus"
            ),
            BlitFramebuffer = (delegate* unmanaged[Stdcall]<
                int,
                int,
                int,
                int,
                int,
                int,
                int,
                int,
                uint,
                uint,
                void>)Proc("glBlitFramebuffer"),
        };
        return
            gl.DXOpenDevice is null
            || gl.DXRegisterObject is null
            || gl.DXLockObjects is null
            || gl.BlitFramebuffer is null
            || gl.GenFramebuffers is null
            ? null
            : gl;
    }

    public bool IsCurrent => (nint)Win32.wglGetCurrentContext().Value == Context;

    public void RequireCurrent()
    {
        if (!IsCurrent)
        {
            throw new InvalidOperationException(
                "The OpenGL context the Spout device was made for is not current on this thread."
            );
        }
    }

    [LibraryImport("opengl32")]
    public static partial void glGenTextures(int count, uint* textures);

    [LibraryImport("opengl32")]
    public static partial void glDeleteTextures(int count, uint* textures);

    [LibraryImport("opengl32")]
    public static partial void glBindTexture(uint target, uint texture);

    [LibraryImport("opengl32")]
    public static partial void glGetTexLevelParameteriv(
        uint target,
        int level,
        uint name,
        int* value
    );

    [LibraryImport("opengl32")]
    public static partial uint glGetError();

    [LibraryImport("opengl32")]
    public static partial void glFinish();

    private static void* Proc(string name) => (void*)(nint)Win32.wglGetProcAddress(name).Value;
}
