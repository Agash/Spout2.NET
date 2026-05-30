// spout_shim.cpp
//
// Implementation of the flat C ABI declared in spout_shim.h, over the Spout2 `spoutDX` class.
// Built into spout_shim.dll with the SpoutDX sources statically linked (see build-native script).
// Windows only.

#include <d3d11.h>
#include "SpoutDX.h"
#include "spout_shim.h"

static inline spoutDX* self(sp_spout s) { return reinterpret_cast<spoutDX*>(s); }

extern "C" {

sp_spout sp_create(void) { return new spoutDX(); }

void sp_destroy(sp_spout s) { delete self(s); }

int sp_open_directx11(sp_spout s, void* device)
{
    return self(s)->OpenDirectX11(reinterpret_cast<ID3D11Device*>(device)) ? 1 : 0;
}

void* sp_get_device(sp_spout s) { return self(s)->GetDX11Device(); }

int sp_set_sender_name(sp_spout s, const char* name)
{
    return self(s)->SetSenderName(name) ? 1 : 0;
}

void sp_set_sender_format(sp_spout s, uint32_t format)
{
    self(s)->SetSenderFormat(static_cast<DXGI_FORMAT>(format));
}

int sp_send_texture(sp_spout s, void* texture)
{
    return self(s)->SendTexture(reinterpret_cast<ID3D11Texture2D*>(texture)) ? 1 : 0;
}

void sp_release_sender(sp_spout s) { self(s)->ReleaseSender(); }

void sp_set_receiver_name(sp_spout s, const char* name) { self(s)->SetReceiverName(name); }

int sp_receive_texture(sp_spout s, void** out_texture)
{
    // Use the no-argument overload: Spout creates, resizes, and copies into its own internal
    // texture, handling the update lifecycle. GetSenderTexture returns that texture (owned by Spout).
    spoutDX* sp = self(s);
    int connected = sp->ReceiveTexture() ? 1 : 0;
    if (out_texture) *out_texture = sp->GetSenderTexture();
    return connected;
}

int sp_is_updated(sp_spout s) { return self(s)->IsUpdated() ? 1 : 0; }
int sp_is_connected(sp_spout s) { return self(s)->IsConnected() ? 1 : 0; }
int sp_is_frame_new(sp_spout s) { return self(s)->IsFrameNew() ? 1 : 0; }
uint32_t sp_get_sender_width(sp_spout s) { return self(s)->GetSenderWidth(); }
uint32_t sp_get_sender_height(sp_spout s) { return self(s)->GetSenderHeight(); }

void sp_release_receiver(sp_spout s) { self(s)->ReleaseReceiver(); }

int sp_get_sender_count(sp_spout s) { return self(s)->GetSenderCount(); }

int sp_get_sender(sp_spout s, int index, char* name, int max_size)
{
    return self(s)->GetSender(index, name, max_size) ? 1 : 0;
}

int sp_get_sender_info(sp_spout s, const char* name,
                       uint32_t* width, uint32_t* height,
                       void** share_handle, uint32_t* format)
{
    unsigned int w = 0, h = 0;
    HANDLE handle = nullptr;
    DWORD fmt = 0;
    bool ok = self(s)->GetSenderInfo(name, w, h, handle, fmt);
    if (width) *width = w;
    if (height) *height = h;
    if (share_handle) *share_handle = handle;
    if (format) *format = fmt;
    return ok ? 1 : 0;
}

} // extern "C"
