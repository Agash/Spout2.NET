using System.Runtime.InteropServices;

namespace Spout2.NET.Tests;

// An OpenGL context on a hidden window, current on the creating thread, as an OpenGL application has
// one; and the few OpenGL 1.1 calls the tests use to put pixels into textures and read them back.
internal sealed unsafe partial class OpenGLContext : IDisposable
{
    public const uint Texture2D = 0x0DE1;
    private const uint Rgba8 = 0x8058;
    private const uint Bgra = 0x80E1;
    private const uint UnsignedByte = 0x1401;
    private const uint TextureMinFilter = 0x2801;
    private const uint Nearest = 0x2600;

    private readonly nint _window;
    private readonly nint _dc;
    private readonly nint _context;

    private OpenGLContext(nint window, nint dc, nint context)
    {
        _window = window;
        _dc = dc;
        _context = context;
    }

    public static OpenGLContext Create()
    {
        nint window = CreateWindowExW(
            0,
            "STATIC",
            "Spout2.NET tests",
            0x00CF0000,
            0,
            0,
            16,
            16,
            0,
            0,
            0,
            0
        );
        nint dc = GetDC(window);
        PixelFormatDescriptor format = new()
        {
            Size = (ushort)sizeof(PixelFormatDescriptor),
            Version = 1,
            Flags = 0x4 | 0x20 | 0x1, // draw to window, support OpenGL, double buffer
            ColorBits = 32,
            DepthBits = 24,
        };
        _ = SetPixelFormat(dc, ChoosePixelFormat(dc, &format), &format);
        nint context = wglCreateContext(dc);
        if (context == 0 || !wglMakeCurrent(dc, context))
        {
            _ = ReleaseDC(window, dc);
            _ = DestroyWindow(window);
            throw new AssertInconclusiveException(
                "No OpenGL context could be created on this machine."
            );
        }

        return new OpenGLContext(window, dc, context);
    }

    public string Renderer => Marshal.PtrToStringAnsi(glGetString(0x1F01)) ?? string.Empty;

    public uint CreateTexture(int width, int height, ReadOnlySpan<byte> bgra = default)
    {
        uint name;
        glGenTextures(1, &name);
        glBindTexture(Texture2D, name);
        glTexParameteri(Texture2D, TextureMinFilter, (int)Nearest);
        fixed (byte* data = bgra)
        {
            glTexImage2D(
                Texture2D,
                0,
                (int)Rgba8,
                width,
                height,
                0,
                Bgra,
                UnsignedByte,
                bgra.IsEmpty ? null : data
            );
        }

        glBindTexture(Texture2D, 0);
        return name;
    }

    public void Upload(uint texture, int width, int height, ReadOnlySpan<byte> bgra)
    {
        glBindTexture(Texture2D, texture);
        fixed (byte* data = bgra)
        {
            glTexSubImage2D(Texture2D, 0, 0, 0, width, height, Bgra, UnsignedByte, data);
        }

        glBindTexture(Texture2D, 0);
    }

    // Level 0 as BGRA rows, first row first (OpenGL's first row is the bottom of the image).
    public byte[] Read(uint texture, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        glFinish();
        glBindTexture(Texture2D, texture);
        fixed (byte* data = pixels)
        {
            glGetTexImage(Texture2D, 0, Bgra, UnsignedByte, data);
        }

        glBindTexture(Texture2D, 0);
        return pixels;
    }

    public void DeleteTexture(uint texture) => glDeleteTextures(1, &texture);

    public void MakeCurrent() => _ = wglMakeCurrent(_dc, _context);

    public static void ReleaseCurrent() => _ = wglMakeCurrent(0, 0);

    public void Dispose()
    {
        _ = wglMakeCurrent(0, 0);
        _ = wglDeleteContext(_context);
        _ = ReleaseDC(_window, _dc);
        _ = DestroyWindow(_window);
    }

    // Rows reversed: the same image with the other first row.
    public static byte[] Flip(byte[] pixels, int width, int height)
    {
        byte[] flipped = new byte[pixels.Length];
        int row = width * 4;
        for (int y = 0; y < height; y++)
        {
            pixels.AsSpan(y * row, row).CopyTo(flipped.AsSpan((height - 1 - y) * row));
        }

        return flipped;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort Size;
        public ushort Version;
        public uint Flags;
        public byte PixelType;
        public byte ColorBits;
        public byte RedBits,
            RedShift,
            GreenBits,
            GreenShift,
            BlueBits,
            BlueShift,
            AlphaBits,
            AlphaShift;
        public byte AccumBits,
            AccumRedBits,
            AccumGreenBits,
            AccumBlueBits,
            AccumAlphaBits;
        public byte DepthBits;
        public byte StencilBits;
        public byte AuxBuffers;
        public byte LayerType;
        public byte Reserved;
        public uint LayerMask,
            VisibleMask,
            DamageMask;
    }

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param
    );

    [LibraryImport("user32")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("gdi32")]
    private static partial int ChoosePixelFormat(nint dc, PixelFormatDescriptor* format);

    [LibraryImport("gdi32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPixelFormat(
        nint dc,
        int format,
        PixelFormatDescriptor* descriptor
    );

    [LibraryImport("opengl32")]
    private static partial nint wglCreateContext(nint dc);

    [LibraryImport("opengl32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool wglMakeCurrent(nint dc, nint context);

    [LibraryImport("opengl32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool wglDeleteContext(nint context);

    [LibraryImport("opengl32")]
    private static partial void glGenTextures(int count, uint* textures);

    [LibraryImport("opengl32")]
    private static partial void glDeleteTextures(int count, uint* textures);

    [LibraryImport("opengl32")]
    private static partial void glBindTexture(uint target, uint texture);

    [LibraryImport("opengl32")]
    private static partial void glTexParameteri(uint target, uint name, int value);

    [LibraryImport("opengl32")]
    private static partial void glTexImage2D(
        uint target,
        int level,
        int internalFormat,
        int width,
        int height,
        int border,
        uint format,
        uint type,
        void* data
    );

    [LibraryImport("opengl32")]
    private static partial void glTexSubImage2D(
        uint target,
        int level,
        int x,
        int y,
        int width,
        int height,
        uint format,
        uint type,
        void* data
    );

    [LibraryImport("opengl32")]
    private static partial void glGetTexImage(
        uint target,
        int level,
        uint format,
        uint type,
        void* data
    );

    [LibraryImport("opengl32")]
    private static partial void glFinish();

    [LibraryImport("opengl32")]
    private static partial nint glGetString(uint name);
}
