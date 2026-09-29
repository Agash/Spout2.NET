using Windows.Win32.Graphics.Direct3D11;

namespace Spout2.NET.OpenGL;

// OpenGL textures reach Spout's Direct3D 11 shared textures through WGL_NV_DX_interop2 (spoutGL): an
// interop device opened on the Direct3D 11 device, a GL texture registered for each shared texture, and
// the pair locked around every GL access. Copies are framebuffer blits, which convert between the GL
// texture's storage and the shared texture's and flip rows: Direct3D's first row is the top, OpenGL's
// the bottom, so a Spout frame is flipped on its way in and out of OpenGL, as spoutGL does by default.
internal sealed unsafe class OpenGLBridge : IDisposable
{
    private readonly nint _device;
    private uint _readFramebuffer;
    private uint _drawFramebuffer;

    private OpenGLBridge(GL gl, nint device)
    {
        Gl = gl;
        _device = device;
    }

    public GL Gl { get; }

    public static OpenGLBridge? TryOpen(GL gl, ID3D11Device* device)
    {
        nint interop = gl.DXOpenDevice(device);
        return interop == 0 ? null : new OpenGLBridge(gl, interop);
    }

    // A GL texture sharing memory with a Direct3D 11 texture on the bridge's device.
    public Link LinkTo(ID3D11Texture2D* texture)
    {
        Gl.RequireCurrent();
        uint name;
        GL.glGenTextures(1, &name);
        nint registered = Gl.DXRegisterObject(
            _device,
            texture,
            name,
            GL.Texture2D,
            GL.AccessReadWrite
        );
        if (registered == 0)
        {
            GL.glDeleteTextures(1, &name);
            throw new SpoutException(
                "The shared texture could not be linked to an OpenGL texture."
            );
        }

        return new Link(this, name, registered);
    }

    public (int Width, int Height) Size(OpenGLTexture texture)
    {
        Gl.RequireCurrent();
        int width;
        int height;
        _ = GL.glGetError();
        GL.glBindTexture(texture.Target, texture.Name);
        GL.glGetTexLevelParameteriv(texture.Target, 0, GL.TextureWidth, &width);
        GL.glGetTexLevelParameteriv(texture.Target, 0, GL.TextureHeight, &height);
        GL.glBindTexture(texture.Target, 0);
        return GL.glGetError() != 0 || width <= 0 || height <= 0
            ? throw new ArgumentException(
                $"OpenGL texture {texture.Name} has no size.",
                nameof(texture)
            )
            : (width, height);
    }

    // Copies one texture's level 0 into another's with a framebuffer blit, flipping rows when asked.
    public void Blit(
        OpenGLTexture source,
        OpenGLTexture destination,
        int width,
        int height,
        bool flip
    )
    {
        Gl.RequireCurrent();
        if (_readFramebuffer == 0)
        {
            uint* names = stackalloc uint[2];
            Gl.GenFramebuffers(2, names);
            _readFramebuffer = names[0];
            _drawFramebuffer = names[1];
        }

        Gl.BindFramebuffer(GL.ReadFramebuffer, _readFramebuffer);
        Gl.FramebufferTexture2D(
            GL.ReadFramebuffer,
            GL.ColorAttachment0,
            source.Target,
            source.Name,
            0
        );
        Gl.BindFramebuffer(GL.DrawFramebuffer, _drawFramebuffer);
        Gl.FramebufferTexture2D(
            GL.DrawFramebuffer,
            GL.ColorAttachment0,
            destination.Target,
            destination.Name,
            0
        );
        try
        {
            if (
                Gl.CheckFramebufferStatus(GL.ReadFramebuffer) != GL.FramebufferComplete
                || Gl.CheckFramebufferStatus(GL.DrawFramebuffer) != GL.FramebufferComplete
            )
            {
                throw new SpoutException(
                    "An OpenGL texture cannot be attached to a framebuffer to copy it."
                );
            }

            Gl.BlitFramebuffer(
                0,
                0,
                width,
                height,
                0,
                flip ? height : 0,
                width,
                flip ? 0 : height,
                GL.ColorBufferBit,
                GL.Nearest
            );
        }
        finally
        {
            Gl.FramebufferTexture2D(GL.ReadFramebuffer, GL.ColorAttachment0, source.Target, 0, 0);
            Gl.FramebufferTexture2D(
                GL.DrawFramebuffer,
                GL.ColorAttachment0,
                destination.Target,
                0,
                0
            );
            Gl.BindFramebuffer(GL.ReadFramebuffer, 0);
            Gl.BindFramebuffer(GL.DrawFramebuffer, 0);
        }
    }

    public void Dispose()
    {
        // GL objects can only be released with their context current; elsewhere they go with the context.
        if (!Gl.IsCurrent)
        {
            return;
        }

        if (_readFramebuffer != 0)
        {
            uint* names = stackalloc uint[] { _readFramebuffer, _drawFramebuffer };
            Gl.DeleteFramebuffers(2, names);
        }

        _ = Gl.DXCloseDevice(_device);
    }

    internal sealed class Link(OpenGLBridge bridge, uint name, nint registered) : IDisposable
    {
        private nint _registered = registered;
        private bool _locked;

        public OpenGLTexture Texture { get; } = new(name);

        public bool TryLock()
        {
            bridge.Gl.RequireCurrent();
            nint registeredObject = _registered;
            _locked = bridge.Gl.DXLockObjects(bridge._device, 1, &registeredObject) != 0;
            return _locked;
        }

        public void Unlock()
        {
            if (_locked)
            {
                nint registeredObject = _registered;
                _ = bridge.Gl.DXUnlockObjects(bridge._device, 1, &registeredObject);
                _locked = false;
            }
        }

        public void Dispose()
        {
            if (_registered == 0 || !bridge.Gl.IsCurrent)
            {
                return;
            }

            Unlock();
            _ = bridge.Gl.DXUnregisterObject(bridge._device, _registered);
            _registered = 0;
            uint texture = Texture.Name;
            GL.glDeleteTextures(1, &texture);
        }
    }
}
