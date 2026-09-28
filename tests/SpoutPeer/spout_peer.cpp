// spout_peer: the upstream Spout SDK (SpoutDX) as an independent sender and receiver, for Spout2.NET's
// interop tests. It is built only for the tests and never shipped.
//
//   spout_peer send <name> <width> <height>     sends frames until stdin closes; prints READY <name>,
//                                               then SENT <frame> for every frame
//   spout_peer receive <name|-> <frames> <ms>   receives <frames> new frames or gives up after <ms>;
//                                               prints META <text> once, then
//                                               FRAME <sender frame> <w> <h> <index> <fnv1a> per
//                                               frame, <index> being the frame index the pixels carry
//
// Frame content is PeerPattern below; the tests compute the same pattern.

#include <d3d11.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <thread>
#include <vector>
#include "SpoutDX.h"

namespace
{
    // BGRA bytes of frame `frame`: the first pixel carries the frame index, the rest a pattern that
    // changes with the index and the position.
    void PeerPattern(unsigned int frame, unsigned int width, unsigned int height, std::vector<unsigned char>& pixels)
    {
        pixels.resize(static_cast<size_t>(width) * height * 4);
        for (unsigned int y = 0; y < height; y++)
        {
            for (unsigned int x = 0; x < width; x++)
            {
                unsigned char* p = &pixels[(static_cast<size_t>(y) * width + x) * 4];
                p[0] = static_cast<unsigned char>(x * 7 + frame * 29);
                p[1] = static_cast<unsigned char>(y * 13 + frame * 31);
                p[2] = static_cast<unsigned char>((x ^ y) + frame * 37);
                p[3] = 255;
            }
        }

        std::memcpy(pixels.data(), &frame, sizeof(frame));
    }

    unsigned long long Fnv1a(const unsigned char* data, size_t length)
    {
        unsigned long long hash = 14695981039346656037ull;
        for (size_t i = 0; i < length; i++)
        {
            hash ^= data[i];
            hash *= 1099511628211ull;
        }

        return hash;
    }

    int Send(const char* name, unsigned int width, unsigned int height)
    {
        spoutDX sender;
        // Frame counting is off unless Spout Settings turned it on; the tests need it on both ends.
        sender.frame.SetFrameCount(true);
        if (!sender.OpenDirectX11())
        {
            std::printf("ERROR directx\n");
            return 2;
        }

        sender.SetSenderName(name);
        D3D11_TEXTURE2D_DESC description{};
        description.Width = width;
        description.Height = height;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        ID3D11Texture2D* texture = nullptr;
        if (FAILED(sender.GetDX11Device()->CreateTexture2D(&description, nullptr, &texture)))
        {
            std::printf("ERROR texture\n");
            return 2;
        }

        std::atomic<bool> stop{false};
        std::thread watcher([&stop] {
            char line[64];
            while (std::fgets(line, sizeof(line), stdin)) {}
            stop = true;
        });

        std::vector<unsigned char> pixels;
        bool ready = false;
        for (unsigned int frame = 1; !stop; frame++)
        {
            PeerPattern(frame, width, height, pixels);
            sender.GetDX11Context()->UpdateSubresource(texture, 0, nullptr, pixels.data(), width * 4, 0);
            if (!sender.SendTexture(texture))
            {
                std::printf("ERROR send\n");
                return 2;
            }

            if (!ready)
            {
                const char meta[] = "spout peer metadata";
                sender.WriteMemoryBuffer(sender.GetName(), meta, static_cast<int>(sizeof(meta)));
                std::printf("READY %s\n", sender.GetName());
                ready = true;
            }

            std::printf("SENT %u\n", frame);
            std::fflush(stdout);
            std::this_thread::sleep_for(std::chrono::milliseconds(16));
        }

        texture->Release();
        sender.ReleaseSender();
        watcher.join();
        return 0;
    }

    int Receive(const char* name, int frames, int milliseconds)
    {
        spoutDX receiver;
        receiver.frame.SetFrameCount(true);
        if (!receiver.OpenDirectX11())
        {
            std::printf("ERROR directx\n");
            return 2;
        }

        if (std::strcmp(name, "-") != 0)
        {
            receiver.SetReceiverName(name);
        }

        std::vector<unsigned char> pixels(4);
        int received = 0;
        bool metadata = false;
        auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(milliseconds);
        while (received < frames && std::chrono::steady_clock::now() < deadline)
        {
            unsigned int width = receiver.GetSenderWidth();
            unsigned int height = receiver.GetSenderHeight();
            if (width > 0 && height > 0)
            {
                pixels.resize(static_cast<size_t>(width) * height * 4);
            }

            if (receiver.ReceiveImage(pixels.data(), width, height))
            {
                // A new or resized sender: the image buffer is sized to the sender on the next pass.
                if (receiver.IsUpdated() || width == 0 || height == 0)
                {
                    continue;
                }

                if (receiver.IsFrameNew())
                {
                    if (!metadata)
                    {
                        char text[4096]{};
                        receiver.ReadMemoryBuffer(receiver.GetSenderName(), text, sizeof(text) - 1);
                        std::printf("META %s\n", text);
                        metadata = true;
                    }

                    unsigned int index = 0;
                    std::memcpy(&index, pixels.data(), sizeof(index));
                    std::printf(
                        "FRAME %ld %u %u %u %llu\n",
                        receiver.GetSenderFrame(),
                        width,
                        height,
                        index,
                        Fnv1a(pixels.data(), static_cast<size_t>(width) * height * 4));
                    std::fflush(stdout);
                    received++;
                }
            }

            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }

        receiver.ReleaseReceiver();
        return received == frames ? 0 : 1;
    }
}

int main(int argc, char** argv)
{
    if (argc == 5 && std::strcmp(argv[1], "send") == 0)
    {
        return Send(argv[2], std::atoi(argv[3]), std::atoi(argv[4]));
    }

    if (argc == 5 && std::strcmp(argv[1], "receive") == 0)
    {
        return Receive(argv[2], std::atoi(argv[3]), std::atoi(argv[4]));
    }

    std::printf("usage: spout_peer send <name> <width> <height> | receive <name|-> <frames> <ms>\n");
    return 64;
}
