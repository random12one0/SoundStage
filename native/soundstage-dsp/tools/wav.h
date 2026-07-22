// Minimal 16-bit PCM WAV writer for the render tool (demo/preview only, not shipped in the engine).
#pragma once

#include <cstdint>
#include <cstdio>
#include <vector>

namespace soundstage {

// Interleaved stereo doubles in [-1,1] → 16-bit PCM stereo WAV.
inline bool writeWavStereo16(const char* path, const std::vector<double>& interleaved, int sampleRate) {
    std::FILE* f = std::fopen(path, "wb");
    if (!f) return false;
    const uint32_t frames = static_cast<uint32_t>(interleaved.size() / 2);
    const uint16_t channels = 2, bits = 16;
    const uint32_t byteRate = sampleRate * channels * bits / 8;
    const uint16_t blockAlign = channels * bits / 8;
    const uint32_t dataBytes = frames * blockAlign;

    auto u32 = [&](uint32_t v){ std::fputc(v & 0xFF, f); std::fputc((v>>8)&0xFF, f); std::fputc((v>>16)&0xFF, f); std::fputc((v>>24)&0xFF, f); };
    auto u16 = [&](uint16_t v){ std::fputc(v & 0xFF, f); std::fputc((v>>8)&0xFF, f); };

    std::fputs("RIFF", f); u32(36 + dataBytes); std::fputs("WAVE", f);
    std::fputs("fmt ", f); u32(16); u16(1); u16(channels); u32(sampleRate); u32(byteRate); u16(blockAlign); u16(bits);
    std::fputs("data", f); u32(dataBytes);
    for (double s : interleaved) {
        if (s > 1.0) s = 1.0; else if (s < -1.0) s = -1.0;
        u16(static_cast<uint16_t>(static_cast<int16_t>(s * 32767.0)));
    }
    std::fclose(f);
    return true;
}

}  // namespace soundstage
