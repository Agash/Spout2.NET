// spout_shim.h
//
// Flat C ABI over the Spout2 `spoutDX` class. The managed Spout2.NET layer P/Invokes this and
// never touches C++ or DirectX COM types directly: ID3D11Device / ID3D11Texture2D cross the
// boundary as opaque pointers (the caller supplies them from Vortice.Windows). Frames are shared
// zero-copy as DX11 shared textures.
//
// Handles are opaque. Functions returning int use 1 for success and 0 for failure. Strings are
// UTF-8/ANSI char buffers.

#ifndef SPOUT_SHIM_H
#define SPOUT_SHIM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SP_EXPORT __declspec(dllexport)

typedef void* sp_spout;

// ---- Lifecycle ------------------------------------------------------------

SP_EXPORT sp_spout sp_create(void);
SP_EXPORT void sp_destroy(sp_spout s);

// Open DirectX 11 with the caller's ID3D11Device (pass NULL to let Spout create its own).
SP_EXPORT int sp_open_directx11(sp_spout s, void* d3d11_device);

// The ID3D11Device in use (the caller's, or the one Spout created). Borrowed, do not release.
SP_EXPORT void* sp_get_device(sp_spout s);

// ---- Sender (publish for other apps, e.g. OBS) ----------------------------

SP_EXPORT int sp_set_sender_name(sp_spout s, const char* name);
SP_EXPORT void sp_set_sender_format(sp_spout s, uint32_t dxgi_format);

// Publish a frame from a DX11 texture (ID3D11Texture2D*). Zero-copy share. Returns 1 on success.
SP_EXPORT int sp_send_texture(sp_spout s, void* d3d11_texture);

SP_EXPORT void sp_release_sender(sp_spout s);

// ---- Receiver (capture from another app) ----------------------------------

SP_EXPORT void sp_set_receiver_name(sp_spout s, const char* name);

// Receive the latest frame into an ID3D11Texture2D the receiver owns (*out_texture). On the first
// call and whenever the sender size/format changes, the texture is (re)created; check sp_is_updated.
// Returns 1 while connected to a sender.
SP_EXPORT int sp_receive_texture(sp_spout s, void** out_texture);

SP_EXPORT int sp_is_updated(sp_spout s);
SP_EXPORT int sp_is_connected(sp_spout s);
SP_EXPORT int sp_is_frame_new(sp_spout s);
SP_EXPORT uint32_t sp_get_sender_width(sp_spout s);
SP_EXPORT uint32_t sp_get_sender_height(sp_spout s);

SP_EXPORT void sp_release_receiver(sp_spout s);

// ---- Discovery ------------------------------------------------------------

SP_EXPORT int sp_get_sender_count(sp_spout s);

// Copy the name of the sender at index into name (buffer of at least max_size). Returns 1 on success.
SP_EXPORT int sp_get_sender(sp_spout s, int index, char* name, int max_size);

// Width/height/share-handle/DXGI-format for a named sender. Any out pointer may be NULL.
SP_EXPORT int sp_get_sender_info(sp_spout s, const char* name,
                                 uint32_t* width, uint32_t* height,
                                 void** share_handle, uint32_t* format);

#ifdef __cplusplus
}
#endif

#endif // SPOUT_SHIM_H
