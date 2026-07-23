// Soundstage APO — implementation. See SoundstageApo.h for what this is and why it's shaped this way.
#include "SoundstageApo.h"

#include <ks.h>
#include <ksmedia.h>
#include <propkey.h>
#include <new>
#include <cstdarg>
#include <cstdio>

// {6F3C9A21-4E7B-4B36-9E1D-2A55C0D8E401}
extern "C" const GUID CLSID_SoundstageApo =
    { 0x6f3c9a21, 0x4e7b, 0x4b36, { 0x9e, 0x1d, 0x2a, 0x55, 0xc0, 0xd8, 0xe4, 0x01 } };

// {6F3C9A22-4E7B-4B36-9E1D-2A55C0D8E401}
extern "C" const GUID EFFECT_Soundstage =
    { 0x6f3c9a22, 0x4e7b, 0x4b36, { 0x9e, 0x1d, 0x2a, 0x55, 0xc0, 0xd8, 0xe4, 0x01 } };

extern std::atomic<long> g_dllRefs;

namespace {

/// Read a WAVEFORMATEX out of an IAudioMediaType, tolerating the extensible form.
const WAVEFORMATEX* FormatOf(IAudioMediaType* type) {
    if (!type) { return nullptr; }
    const UNCOMPRESSEDAUDIOFORMAT* uncompressed = nullptr;
    // GetAudioFormat gives us the raw WAVEFORMATEX, which is what we actually need.
    (void)uncompressed;
    return type->GetAudioFormat();
}

/// We process 32-bit float, which is what the Windows audio engine uses internally. Anything else
/// is refused rather than mangled — a wrong guess about sample format is loud and horrible.
/// Return v if it is a finite number, otherwise a safe fallback. The shared settings block is
/// written by another process and lives in a file, so over a machine's lifetime it can end up
/// holding a NaN, an infinity, or plain garbage — a half-finished write, a disk hiccup, an older
/// build. None of that may be allowed to reach a filter coefficient.
inline double Finite(double v, double fallback) {
    return (v == v && v != std::numeric_limits<double>::infinity()
                   && v != -std::numeric_limits<double>::infinity())
               ? v : fallback;
}

/// Finite AND within [lo, hi]. Used for every scalar the shared block carries that feeds a gain or a
/// filter coefficient, so no single corrupt value can mute the mix or destabilise a stage — the
/// worst a bad file can do is sound wrong, never silent and never a crash.
inline double Clamp(double v, double lo, double hi, double fallback) {
    v = Finite(v, fallback);
    return v < lo ? lo : (v > hi ? hi : v);
}

bool IsFloat32(const WAVEFORMATEX* fmt) {
    if (!fmt) { return false; }
    if (fmt->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) { return fmt->wBitsPerSample == 32; }
    if (fmt->wFormatTag == WAVE_FORMAT_EXTENSIBLE && fmt->cbSize >= 22) {
        auto ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(fmt);
        return fmt->wBitsPerSample == 32 &&
               IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    }
    return false;
}

}  // namespace

/// Append one line to C:\ProgramData\Soundstage\apo.log.
///
/// This exists because there is no other way to see what happens in here. audiodg.exe is a protected
/// process: you cannot attach a debugger to it, and even an elevated `tasklist /m` cannot enumerate
/// its modules — so "is our plugin actually loaded?" has no answer from outside. The plugin has to
/// say so itself.
///
/// Called ONLY from the setup and teardown paths (Initialize, LockForProcess, UnlockForProcess),
/// never from APOProcess. Opening a file on the real-time thread would cause exactly the dropouts
/// this whole design is arranged to avoid.
void SoundstageLog(const char* fmt, ...) {
    char line[512];
    va_list args;
    va_start(args, fmt);
    const int n = vsnprintf(line, sizeof(line) - 2, fmt, args);
    va_end(args);
    if (n <= 0) { return; }

    line[n] = '\n';
    line[n + 1] = '\0';

    // FILE_APPEND_DATA rather than GENERIC_WRITE: several endpoints can have their own instance of
    // this plugin, and append-only opens let them share the file without trampling each other.
    HANDLE h = CreateFileW(SOUNDSTAGE_LOG_PATH, FILE_APPEND_DATA,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) { return; }

    DWORD written = 0;
    WriteFile(h, line, static_cast<DWORD>(n + 1), &written, nullptr);
    CloseHandle(h);
}

SoundstageApo::SoundstageApo(IUnknown* outer) : refs_(1) {
    inner_.self = this;
    // Not aggregated? Then we are our own controlling object, and the delegating methods below become
    // a straight call into the internal ones. One path, both cases.
    outer_ = outer ? outer : static_cast<IUnknown*>(&inner_);
    g_dllRefs.fetch_add(1, std::memory_order_relaxed);
}

SoundstageApo::~SoundstageApo() {
    CloseTelemetry();
    CloseSharedState();
    g_dllRefs.fetch_sub(1, std::memory_order_relaxed);
}

// ---- IUnknown, delegating ----------------------------------------------------------------------
// These are what callers reach through IAudioProcessingObject and friends. Under aggregation they
// must forward to the controlling object, so that the whole aggregate looks like one COM identity.

STDMETHODIMP SoundstageApo::QueryInterface(REFIID riid, void** ppv) {
    return outer_->QueryInterface(riid, ppv);
}

STDMETHODIMP_(ULONG) SoundstageApo::AddRef() {
    return outer_->AddRef();
}

STDMETHODIMP_(ULONG) SoundstageApo::Release() {
    return outer_->Release();
}

// ---- IUnknown, non-delegating -------------------------------------------------------------------
// The real thing: our reference count, and the only place that knows which interfaces we implement.

HRESULT SoundstageApo::InternalQueryInterface(REFIID riid, void** ppv) {
    if (!ppv) { return E_POINTER; }
    *ppv = nullptr;

    if (IsEqualIID(riid, __uuidof(IUnknown))) {
        // Must be the non-delegating one — COM identity rules require IUnknown to be stable.
        *ppv = static_cast<IUnknown*>(&inner_);
    } else if (IsEqualIID(riid, __uuidof(IAudioProcessingObject))) {
        *ppv = static_cast<IAudioProcessingObject*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioProcessingObjectConfiguration))) {
        *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioProcessingObjectRT))) {
        *ppv = static_cast<IAudioProcessingObjectRT*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioSystemEffects))) {
        *ppv = static_cast<IAudioSystemEffects*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioSystemEffects2))) {
        *ppv = static_cast<IAudioSystemEffects2*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioSystemEffects3))) {
        *ppv = static_cast<IAudioSystemEffects3*>(this);
    } else {
        return E_NOINTERFACE;
    }

    // AddRef through the pointer we are about to return — NOT unconditionally on our own count.
    //
    // This looks like a pedantic distinction and is not. The interfaces above have *delegating*
    // AddRef/Release: calling Release on one of them decrements the outer object. So if we counted
    // those references on ourselves, every acquire would land on our count and every release on the
    // aggregator's — the two drift apart, the aggregate is destroyed while still in use, and
    // audiodg.exe dies with an access violation somewhere that looks nothing like the real cause.
    //
    // Going through *ppv gets it right in both cases automatically: for IUnknown that is our
    // non-delegating AddRef, for everything else it is the delegating one.
    static_cast<IUnknown*>(*ppv)->AddRef();
    return S_OK;
}

ULONG SoundstageApo::InternalAddRef() {
    return refs_.fetch_add(1, std::memory_order_relaxed) + 1;
}

ULONG SoundstageApo::InternalRelease() {
    const ULONG left = refs_.fetch_sub(1, std::memory_order_acq_rel) - 1;
    if (left == 0) { delete this; }
    return left;
}

// ---- IAudioProcessingObject -------------------------------------------------------------------

STDMETHODIMP SoundstageApo::Reset() {
    chain_.reset();
    return S_OK;
}

STDMETHODIMP SoundstageApo::GetLatency(HNSTIME* pTime) {
    if (!pTime) { return E_POINTER; }
    // We process in place, sample for sample. The limiter's lookahead is the only delay we add, and
    // it is deliberately short; report it honestly so the engine can account for it.
    const double lookaheadSeconds = 0.003;
    *pTime = static_cast<HNSTIME>(lookaheadSeconds * 10000000.0);
    return S_OK;
}

STDMETHODIMP SoundstageApo::GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps) {
    if (!ppRegProps) { return E_POINTER; }

    auto* props = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES)));
    if (!props) { return E_OUTOFMEMORY; }
    ZeroMemory(props, sizeof(APO_REG_PROPERTIES));

    props->clsid = CLSID_SoundstageApo;
    props->Flags = APO_FLAG_DEFAULT;
    wcscpy_s(props->szFriendlyName, L"Soundstage");
    wcscpy_s(props->szCopyrightInfo, L"Soundstage");
    props->u32MajorVersion = 1;
    props->u32MinorVersion = 0;
    props->u32MinInputConnections = 1;
    props->u32MaxInputConnections = 1;
    props->u32MinOutputConnections = 1;
    props->u32MaxOutputConnections = 1;
    props->u32MaxInstances = 0xFFFFFFFF;
    props->u32NumAPOInterfaces = 1;
    props->iidAPOInterfaceList[0] = __uuidof(IAudioProcessingObject);

    *ppRegProps = props;
    return S_OK;
}

STDMETHODIMP SoundstageApo::Initialize(UINT32 /*cbDataSize*/, BYTE* /*pbyData*/) {
    OpenSharedState();
    SoundstageLog("[init]   instance created, pid=%lu, settings=%s",
        GetCurrentProcessId(), shared_ ? "connected" : "not found (app not running?)");
    return S_OK;
}

STDMETHODIMP SoundstageApo::IsInputFormatSupported(IAudioMediaType* /*pOppositeFormat*/,
                                                   IAudioMediaType* pRequestedInputFormat,
                                                   IAudioMediaType** ppSupportedInputFormat) {
    if (!pRequestedInputFormat) { return E_POINTER; }
    if (ppSupportedInputFormat) { *ppSupportedInputFormat = nullptr; }

    // Float32 only, in any channel count the engine handles. Refusing rather than adapting keeps the
    // real-time path free of format conversion.
    const WAVEFORMATEX* fmt = FormatOf(pRequestedInputFormat);
    if (!IsFloat32(fmt) || fmt->nChannels < 1 || fmt->nChannels > 8) {
        SoundstageLog("[format] refused: tag=%u bits=%u ch=%u rate=%lu",
                      fmt ? fmt->wFormatTag : 0, fmt ? fmt->wBitsPerSample : 0,
                      fmt ? fmt->nChannels : 0,
                      fmt ? static_cast<unsigned long>(fmt->nSamplesPerSec) : 0UL);
        return APOERR_FORMAT_NOT_SUPPORTED;
    }

    // Remember what the engine offered. This is the only chance we get: Windows asks
    // GetInputChannelCount BEFORE it ever calls LockForProcess, and answering with a stale default
    // is enough for it to decide we are the wrong shape for this endpoint and skip us entirely.
    // That is why the plugin ran on a stereo output but never locked a stream on a 5.1 receiver —
    // it kept saying "2" while the endpoint was asking about six.
    negotiatedChannels_ = fmt->nChannels;

    SoundstageLog("[format] accepted: %u ch @ %lu Hz",
                  fmt->nChannels, static_cast<unsigned long>(fmt->nSamplesPerSec));
    return S_OK;
}

STDMETHODIMP SoundstageApo::IsOutputFormatSupported(IAudioMediaType* pOppositeFormat,
                                                    IAudioMediaType* pRequestedOutputFormat,
                                                    IAudioMediaType** ppSupportedOutputFormat) {
    // We never change the format, so whatever is acceptable on the way in is acceptable on the way
    // out. (An APO cannot change channel count, which is exactly why the engine's upmix works from
    // whatever layout the endpoint is already configured for.)
    return IsInputFormatSupported(pOppositeFormat, pRequestedOutputFormat, ppSupportedOutputFormat);
}

STDMETHODIMP SoundstageApo::GetInputChannelCount(UINT32* pu32ChannelCount) {
    if (!pu32ChannelCount) { return E_POINTER; }
    // Once locked, the real count. Before that, whatever the engine last asked us about — never a
    // hardcoded guess, which is what stopped this plugin working on anything but stereo.
    *pu32ChannelCount = locked_ ? channels_ : negotiatedChannels_;
    return S_OK;
}

// ---- IAudioProcessingObjectConfiguration -------------------------------------------------------

STDMETHODIMP SoundstageApo::LockForProcess(UINT32 u32NumInputConnections,
                                           APO_CONNECTION_DESCRIPTOR** ppInputConnections,
                                           UINT32 u32NumOutputConnections,
                                           APO_CONNECTION_DESCRIPTOR** ppOutputConnections) {
    if (u32NumInputConnections != 1 || u32NumOutputConnections != 1 ||
        !ppInputConnections || !ppOutputConnections ||
        !ppInputConnections[0] || !ppOutputConnections[0]) {
        return E_INVALIDARG;
    }

    const WAVEFORMATEX* inFmt = FormatOf(ppInputConnections[0]->pFormat);
    if (!IsFloat32(inFmt)) {
        SoundstageLog("[lock]   REFUSED: not float32 (tag=%u bits=%u ch=%u)",
            inFmt ? inFmt->wFormatTag : 0, inFmt ? inFmt->wBitsPerSample : 0,
            inFmt ? inFmt->nChannels : 0);
        return APOERR_FORMAT_NOT_SUPPORTED;
    }

    channels_ = inFmt->nChannels;
    sampleRate_ = inFmt->nSamplesPerSec;
    maxFrames_ = ppInputConnections[0]->u32MaxFrameCount;

    // Everything the real-time path could ever need, reserved now. APOProcess must not allocate.
    const size_t widest = static_cast<size_t>(maxFrames_) * 8;
    try {
        scratchIn_.assign(widest, 0.0f);
        scratchOut_.assign(widest, 0.0f);
    } catch (...) {
        return E_OUTOFMEMORY;
    }

    chain_.prepare(static_cast<double>(sampleRate_));
    OpenSharedState();
    OpenTelemetry();   // set up the meter back-channel here, off the real-time path
    SyncSettings();

    locked_ = true;
    SoundstageLog("[lock]   RUNNING: %u ch @ %u Hz, max %u frames/buffer, settings=%s",
        channels_, sampleRate_, maxFrames_, shared_ ? "connected" : "not found");
    return S_OK;
}

STDMETHODIMP SoundstageApo::UnlockForProcess() {
    locked_ = false;

    // The one honest answer to "is it actually doing anything?", reported where it is safe to write
    // a file. Frames > 0 means audio really came through us; peak out differing from peak in means
    // we changed it rather than passing it along.
    const double inDb  = peakIn_  > 0.0f ? 20.0 * log10(static_cast<double>(peakIn_))  : -999.0;
    const double outDb = peakOut_ > 0.0f ? 20.0 * log10(static_cast<double>(peakOut_)) : -999.0;
    SoundstageLog("[stats]  %llu frames, peak in %.2f dBFS, peak out %.2f dBFS, delta %+.2f dB",
                  framesProcessed_, inDb, outDb, outDb - inDb);

    framesProcessed_ = 0;
    peakIn_ = 0.0f;
    peakOut_ = 0.0f;
    surroundHoldFrames_ = 0;

    // Zero the meters so the app doesn't show a stale bar frozen on a speaker after the stream ends.
    if (telemetry_) {
        for (int c = 0; c < 8; ++c) { telemetry_->channelPeak[c] = 0.0f; }
        telemetry_->outPeak = 0.0f;
    }
    for (int c = 0; c < 8; ++c) { telChannelPeak_[c] = 0.0f; }
    telDecim_ = 0;

    CloseTelemetry();
    chain_.reset();
    return S_OK;
}

// ---- IAudioProcessingObjectRT ------------------------------------------------------------------

STDMETHODIMP_(UINT32) SoundstageApo::CalcInputFrames(UINT32 u32OutputFrameCount) {
    return u32OutputFrameCount;   // sample for sample
}

STDMETHODIMP_(UINT32) SoundstageApo::CalcOutputFrames(UINT32 u32InputFrameCount) {
    return u32InputFrameCount;
}

STDMETHODIMP_(void) SoundstageApo::APOProcess(UINT32 u32NumInputConnections,
                                              APO_CONNECTION_PROPERTY** ppInputConnections,
                                              UINT32 u32NumOutputConnections,
                                              APO_CONNECTION_PROPERTY** ppOutputConnections) {
    // Anything unexpected degrades to silence-or-passthrough rather than to a crash: this runs
    // inside the audio service, and taking that down would kill sound for the whole machine.
    if (u32NumInputConnections < 1 || u32NumOutputConnections < 1 ||
        !ppInputConnections || !ppOutputConnections) {
        return;
    }

    APO_CONNECTION_PROPERTY* in = ppInputConnections[0];
    APO_CONNECTION_PROPERTY* out = ppOutputConnections[0];
    if (!in || !out) { return; }

    const UINT32 frames = in->u32ValidFrameCount;

    // A silent buffer still has to be marked as such, or the engine keeps the last thing we wrote.
    if (in->u32BufferFlags == BUFFER_SILENT || frames == 0) {
        out->u32ValidFrameCount = frames;
        out->u32BufferFlags = BUFFER_SILENT;
        return;
    }

    auto* src = reinterpret_cast<const float*>(in->pBuffer);
    auto* dst = reinterpret_cast<float*>(out->pBuffer);
    if (!src || !dst) { return; }

    const UINT32 ch = channels_;

    if (!locked_ || ch < 1 || ch > 8 || frames > maxFrames_) {
        // Not in a state we understand — hand the audio through untouched.
        if (src != dst) { memcpy(dst, src, static_cast<size_t>(frames) * ch * sizeof(float)); }
        out->u32ValidFrameCount = frames;
        out->u32BufferFlags = in->u32BufferFlags;
        return;
    }

    SyncSettings();

    // Every gain change ramps rather than steps, so for the first few milliseconds after a setting
    // arrives the output still reflects the OLD value. A peak taken over the whole stream would be
    // set by that ramp-in and would report almost no change no matter what was asked for. Waiting
    // half a second before measuring compares steady state against steady state.
    const bool measure = framesProcessed_ > sampleRate_ / 2;

    if (measure) {
        const size_t n = static_cast<size_t>(frames) * ch;
        float p = peakIn_;
        for (size_t i = 0; i < n; ++i) {
            const float a = src[i] < 0.0f ? -src[i] : src[i];
            if (a > p) { p = a; }
        }
        peakIn_ = p;
    }

    // Which path to take is NOT "how many channels does the buffer have" — inside an APO that is
    // always the endpoint's full width. Windows has already padded a stereo app up to 6 or 8
    // channels with the surrounds left silent. So the honest question is "is this actually a
    // surround recording, or stereo wearing a 5.1 costume", and the answer is: does anything play on
    // the non-front channels.
    //
    // Getting this wrong is exactly the "my 5.1 is quiet" bug: the multichannel path faithfully
    // preserves those silent surrounds, the upmix never runs, and four of five speakers stay dead.
    //
    // But the decision has to be STICKY, not per-block. A real 5.1 film has quiet passages — a
    // dialogue scene with nothing in the surrounds — and if we flipped to stereo-upmix the instant
    // the surrounds fell silent and back again when they returned, the surrounds would pop in and
    // out as the two code paths (with different filter state) traded off. So: any surround signal
    // tops up a hold timer, and we only fall back to treating it as stereo after the surrounds have
    // been silent for the whole hold window. Converging on the right mode a beat late is inaudible;
    // switching modes every few milliseconds is not.
    bool surroundThisBlock = false;
    if (ch > 2) {
        for (UINT32 n = 0; n < frames && !surroundThisBlock; ++n) {
            const float* frame = src + static_cast<size_t>(n) * ch;
            for (UINT32 c = 2; c < ch; ++c) {   // skip FL/FR; they're expected to be full
                const float a = frame[c] < 0.0f ? -frame[c] : frame[c];
                if (a > 1.0e-4f) { surroundThisBlock = true; break; }
            }
        }
    }

    if (surroundThisBlock) {
        surroundHoldFrames_ = sampleRate_;   // ~1s of "this is really surround" before we reconsider
    } else if (surroundHoldFrames_ > frames) {
        surroundHoldFrames_ -= frames;
    } else {
        surroundHoldFrames_ = 0;
    }

    if (ch > 2 && surroundHoldFrames_ > 0) {
        // A real surround recording — keep every channel, just process it.
        chain_.processBlockMulti(src, static_cast<int>(ch), dst, static_cast<int>(ch),
                                 static_cast<int>(frames));
    } else {
        // Stereo, whatever the buffer width says. Feed the engine the real interleave stride so it
        // reads the right samples — processBlock takes channels 0 and 1 as the front L/R pair, which
        // is exactly what a padded stereo buffer has there — and let the upmix fill the centre, LFE
        // and surrounds on the output side. Passing the true `ch` as the input stride is essential:
        // pass 2 and it would read every third sample of a 6-wide buffer as if it were a frame.
        chain_.processBlock(src, static_cast<int>(ch), dst, static_cast<int>(ch),
                            static_cast<int>(frames));
    }

    if (measure) {
        const size_t n = static_cast<size_t>(frames) * ch;
        float p = peakOut_;
        for (size_t i = 0; i < n; ++i) {
            const float a = dst[i] < 0.0f ? -dst[i] : dst[i];
            if (a > p) { p = a; }
        }
        peakOut_ = p;
    }
    framesProcessed_ += frames;

    // Publish live per-speaker levels back to the app. This is what lets the app meter each speaker
    // even though it is no longer in the audio path — and because it is measured on `dst`, AFTER the
    // upmix, the surrounds an upmix fills actually register.
    //
    // Everything here is real-time safe: a per-channel max over this buffer (plain arithmetic), a
    // decayed peak held in a member, and — only about 20 times a second — a handful of plain stores
    // into an already-mapped page. No allocation, no lock, no syscall.
    if (telemetry_) {
        for (UINT32 c = 0; c < ch && c < 8; ++c) {
            float blockPk = 0.0f;
            for (UINT32 nf = 0; nf < frames; ++nf) {
                const float a = std::fabs(dst[static_cast<size_t>(nf) * ch + c]);
                if (a > blockPk) { blockPk = a; }
            }
            // Decay so it reads like a meter rather than flickering, matching the app's own meters.
            telChannelPeak_[c] = blockPk > telChannelPeak_[c] ? blockPk : telChannelPeak_[c] * 0.8f;
        }

        // Push to the shared page every ~48 ms (a bit over a screen frame); no need to write on every
        // 10 ms buffer, and fewer writes means less chance of the app reading mid-update.
        telDecim_ += frames;
        if (telDecim_ >= sampleRate_ / 20) {
            telDecim_ = 0;
            telemetry_->channels = ch;
            telemetry_->sampleRate = sampleRate_;
            for (int c = 0; c < 8; ++c) {
                telemetry_->channelPeak[c] = (static_cast<UINT32>(c) < ch) ? telChannelPeak_[c] : 0.0f;
            }
            telemetry_->outPeak = peakOut_;
            // Heartbeat LAST, so the app can treat a change in it as "the rest is fresh".
            ++telemetry_->heartbeat;
        }
    }

    out->u32ValidFrameCount = frames;
    out->u32BufferFlags = BUFFER_VALID;
}

// ---- IAudioSystemEffects2 / 3 -------------------------------------------------------------------
//
// These exist so Windows can describe and toggle our processing from its own Sound settings. We
// present the whole chain as ONE effect named Soundstage rather than enumerating EQ, bass, reverb and
// the rest: those are ours to control from our own UI, and exposing a dozen switches Windows could
// flip independently would let the two interfaces disagree about what is on.
//
// Every one of these allocates with CoTaskMemAlloc because the caller frees it with CoTaskMemFree.

STDMETHODIMP SoundstageApo::GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE /*Event*/) {
    if (!ppEffectsIds || !pcEffects) { return E_POINTER; }

    *ppEffectsIds = nullptr;
    *pcEffects = 0;

    auto* ids = static_cast<LPGUID>(CoTaskMemAlloc(sizeof(GUID)));
    if (!ids) { return E_OUTOFMEMORY; }

    ids[0] = EFFECT_Soundstage;
    *ppEffectsIds = ids;
    *pcEffects = 1;
    return S_OK;
}

STDMETHODIMP SoundstageApo::GetControllableSystemEffectsList(AUDIO_SYSTEMEFFECT** effects,
                                                             UINT* numEffects, HANDLE /*event*/) {
    if (!effects || !numEffects) { return E_POINTER; }

    *effects = nullptr;
    *numEffects = 0;

    auto* list = static_cast<AUDIO_SYSTEMEFFECT*>(CoTaskMemAlloc(sizeof(AUDIO_SYSTEMEFFECT)));
    if (!list) { return E_OUTOFMEMORY; }

    ZeroMemory(list, sizeof(AUDIO_SYSTEMEFFECT));
    list[0].id = EFFECT_Soundstage;
    // Windows may switch the whole chain off — that is the same thing our own bypass does, and it
    // would be rude to show a control that does nothing.
    list[0].canSetState = TRUE;
    list[0].state = chain_.enabled() ? AUDIO_SYSTEMEFFECT_STATE_ON : AUDIO_SYSTEMEFFECT_STATE_OFF;

    *effects = list;
    *numEffects = 1;
    return S_OK;
}

STDMETHODIMP SoundstageApo::SetAudioSystemEffectState(GUID effectId, AUDIO_SYSTEMEFFECT_STATE state) {
    if (!IsEqualGUID(effectId, EFFECT_Soundstage)) { return E_INVALIDARG; }

    const bool on = (state == AUDIO_SYSTEMEFFECT_STATE_ON);
    SoundstageLog("[call]   SetAudioSystemEffectState -> %s", on ? "on" : "off");

    // Windows' switch and our own bypass are the same switch. Note the app will overwrite this the
    // next time it publishes settings — which is correct: our UI is the authority.
    chain_.setEnabled(on);
    return S_OK;
}

// ---- shared settings ---------------------------------------------------------------------------

void SoundstageApo::OpenSharedState() {
    if (shared_) { return; }

    // FILE_SHARE_WRITE is required, not optional: the app holds this file open for writing the whole
    // time it runs, so without it every open here would fail with a sharing violation.
    sharedFile_ = CreateFileW(SOUNDSTAGE_SHARED_PATH, GENERIC_READ,
                              FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (sharedFile_ == INVALID_HANDLE_VALUE) {
        sharedFile_ = nullptr;
        return;   // Soundstage has never run on this machine; we stay transparent
    }

    sharedMapping_ = CreateFileMappingW(sharedFile_, nullptr, PAGE_READONLY, 0,
                                        sizeof(SoundstageSharedState), nullptr);
    if (!sharedMapping_) { CloseSharedState(); return; }

    shared_ = static_cast<SoundstageSharedState*>(
        MapViewOfFile(sharedMapping_, FILE_MAP_READ, 0, 0, sizeof(SoundstageSharedState)));
    if (!shared_) { CloseSharedState(); }
}

void SoundstageApo::CloseSharedState() {
    if (shared_) { UnmapViewOfFile(shared_); shared_ = nullptr; }
    if (sharedMapping_) { CloseHandle(sharedMapping_); sharedMapping_ = nullptr; }
    if (sharedFile_) { CloseHandle(sharedFile_); sharedFile_ = nullptr; }
}

void SoundstageApo::OpenTelemetry() {
    if (telemetry_) { return; }

    // Read-WRITE, because we publish into it. The app creates the file at a fixed size with a
    // permissive ACL; if it hasn't (plugin installed, app never run), the open fails and we simply
    // never report telemetry — harmless.
    sharedFile_tel_ = CreateFileW(SOUNDSTAGE_TELEMETRY_PATH, GENERIC_READ | GENERIC_WRITE,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                  nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (sharedFile_tel_ == INVALID_HANDLE_VALUE) { sharedFile_tel_ = nullptr; return; }

    sharedMapping_tel_ = CreateFileMappingW(sharedFile_tel_, nullptr, PAGE_READWRITE, 0,
                                            sizeof(SoundstageTelemetry), nullptr);
    if (!sharedMapping_tel_) { CloseTelemetry(); return; }

    telemetry_ = static_cast<SoundstageTelemetry*>(
        MapViewOfFile(sharedMapping_tel_, FILE_MAP_WRITE, 0, 0, sizeof(SoundstageTelemetry)));
    if (!telemetry_) { CloseTelemetry(); return; }

    telemetry_->channels = channels_;
    telemetry_->sampleRate = sampleRate_;
}

void SoundstageApo::CloseTelemetry() {
    if (telemetry_) { UnmapViewOfFile(telemetry_); telemetry_ = nullptr; }
    if (sharedMapping_tel_) { CloseHandle(sharedMapping_tel_); sharedMapping_tel_ = nullptr; }
    if (sharedFile_tel_) { CloseHandle(sharedFile_tel_); sharedFile_tel_ = nullptr; }
}

void SoundstageApo::SyncSettings() {
    // No retry here, on purpose. SyncSettings runs on the real-time thread, and opening a file is a
    // blocking syscall — doing it per buffer because the app happens not to be installed yet would
    // trade "no effects" for "crackling". The open is attempted only from LockForProcess/Initialize.
    if (!shared_) { return; }

    // Seqlock: an odd counter means the app is mid-write, so skip this buffer rather than act on
    // half an update. Settings arriving one buffer late is inaudible; a torn read is not.
    const unsigned seq = shared_->sequence.load(std::memory_order_acquire);
    if ((seq & 1u) != 0u || seq == lastSequence_) { return; }
    if (shared_->version != SOUNDSTAGE_SHARED_VERSION) { return; }

    SoundstageSettings s = shared_->settings;   // copy, then confirm nothing changed underneath

    if (shared_->sequence.load(std::memory_order_acquire) != seq) { return; }
    lastSequence_ = seq;

    chain_.setEnabled(s.masterOn != 0);
    // Output gain feeds a dB→linear conversion; a non-finite value there would scale the whole mix
    // to NaN. Clamp to a sane ±24 dB window as well — nothing legitimate asks for more.
    chain_.setOutputGainDb(std::max(-24.0, std::min(24.0, Finite(s.outputGainDb, 0.0))));

    chain_.enableEq(s.eqOn != 0);
    // Clamp the band count into range before it reaches the equaliser. A corrupted shared block
    // could carry anything here; a negative or absurd value must not drive a loop or an allocation.
    int bandCount = s.eqBandCount;
    if (bandCount < 0) { bandCount = 0; }
    if (bandCount > 36) { bandCount = 36; }
    chain_.setEqNumBands(bandCount);
    for (int i = 0; i < bandCount; ++i) {
        int type = s.eqBands[i].type;
        if (type < 0 || type > 4) { type = 0; }
        // Sanitise every band parameter. A zero or non-finite Q divides to infinity in the biquad;
        // a non-finite frequency or gain poisons its coefficients. Rather than let a bad band
        // silence itself (NaN coefficients -> the output scrub zeroes it -> that band goes mute), fold
        // anything out of range back to a harmless identity so the rest of the EQ keeps working.
        double freq = Finite(s.eqBands[i].freq, 1000.0);
        double gain = Finite(s.eqBands[i].gainDb, 0.0);
        double q    = Finite(s.eqBands[i].q, 0.707);
        if (freq < 10.0 || freq > 22000.0) { freq = 1000.0; gain = 0.0; }   // out of audible band → identity
        if (q < 0.1 || q > 24.0) { q = 0.707; }
        if (gain < -48.0 || gain > 48.0) { gain = gain < 0.0 ? -48.0 : 48.0; }
        chain_.setEqBand(i, static_cast<soundstage::Equalizer::BandType>(type), freq, gain, q);
    }

    chain_.enableBass(s.bassOn != 0);
    chain_.bass().setAmount(Clamp(s.bassAmount, 0.0, 1.0, 0.0));
    chain_.bass().setCrossover(Clamp(s.bassCrossover, 20.0, 500.0, 90.0));
    chain_.bass().setDrive(Clamp(s.bassDrive, 1.0, 10.0, 1.5));

    // Dynamics: a garbage makeup gain is the classic way a corrupt file silences (huge negative) or
    // blasts (huge positive) the mix, and a zero/negative ratio or time constant destabilises the
    // envelope. Clamp every one to the range a real compressor uses.
    chain_.enableCompressor(s.compOn != 0);
    chain_.compressor().setThresholdDb(Clamp(s.compThresholdDb, -60.0, 0.0, -18.0));
    chain_.compressor().setRatio(Clamp(s.compRatio, 1.0, 20.0, 3.0));
    chain_.compressor().setKneeDb(Clamp(s.compKneeDb, 0.0, 24.0, 6.0));
    chain_.compressor().setMakeupDb(Clamp(s.compMakeupDb, 0.0, 24.0, 0.0));
    chain_.compressor().setAttackMs(Clamp(s.compAttackMs, 0.1, 200.0, 15.0));
    chain_.compressor().setReleaseMs(Clamp(s.compReleaseMs, 1.0, 2000.0, 150.0));

    chain_.enableNight(s.nightOn != 0);
    chain_.nightCompressor().setThresholdDb(Clamp(s.nightThresholdDb, -60.0, 0.0, -28.0));
    chain_.nightCompressor().setRatio(Clamp(s.nightRatio, 1.0, 20.0, 5.0));
    chain_.nightCompressor().setMakeupDb(Clamp(s.nightMakeupDb, 0.0, 24.0, 6.0));
    chain_.nightCompressor().setAttackMs(Clamp(s.nightAttackMs, 0.1, 200.0, 5.0));
    chain_.nightCompressor().setReleaseMs(Clamp(s.nightReleaseMs, 1.0, 2000.0, 250.0));

    chain_.enableWidth(s.widthOn != 0);
    chain_.width().setWidth(Clamp(s.width, 0.0, 2.0, 1.0));

    chain_.enableReverb(s.reverbOn != 0);
    chain_.reverb().setSize(Clamp(s.rvSize, 0.0, 1.0, 0.5));
    chain_.reverb().setDecaySeconds(Clamp(s.rvDecay, 0.1, 20.0, 1.6));
    chain_.reverb().setDamping(Clamp(s.rvDamping, 0.0, 1.0, 0.5));
    chain_.reverb().setPreDelayMs(Clamp(s.rvPreDelayMs, 0.0, 200.0, 20.0));
    chain_.reverb().setWidth(Clamp(s.rvWidth, 0.0, 2.0, 1.0));
    chain_.reverb().setMix(Clamp(s.rvMix, 0.0, 1.0, 0.0));
    chain_.reverb().setDiffusion(Clamp(s.rvDiffusion, 0.0, 1.0, 0.7));
    chain_.reverb().setLowCutHz(Clamp(s.rvLowCutHz, 20.0, 2000.0, 200.0));
    chain_.reverb().setHighCutHz(Clamp(s.rvHighCutHz, 1000.0, 20000.0, 8000.0));
    chain_.reverb().setEarlyLevel(Clamp(s.rvEarly, 0.0, 1.0, 0.4));
    chain_.reverb().setModulation(Clamp(s.rvModulation, 0.0, 1.0, 0.3));

    chain_.enableUpmix(s.upmixOn != 0);
    chain_.setUpmixAmount(Clamp(s.upmixAmount, 0.0, 1.0, 0.7));
    chain_.upmix().setCenterGain(Clamp(s.upmixCenter, 0.0, 2.0, 1.0));
    chain_.upmix().setLfeGain(Clamp(s.upmixLfe, 0.0, 2.0, 1.0));
    chain_.enableSubFeed(s.subFeedOn != 0);

    chain_.bassManager().setEnabled(s.bassMgmtOn != 0);
    chain_.bassManager().setCrossover(Clamp(s.bmCrossover, 40.0, 200.0, 80.0));
    chain_.bassManager().setSubGain(Clamp(s.bmSubGain, 0.0, 4.0, 1.0));
    for (int c = 0; c < 8; ++c) {
        chain_.bassManager().setSmall(c, (s.bmSmallMask & (1 << c)) != 0);
    }

    chain_.enableLimiter(s.limiterOn != 0);
    chain_.limiter().setCeilingDb(Clamp(s.limCeilingDb, -24.0, 0.0, -1.0));
    chain_.limiter().setRelease(Clamp(s.limReleaseMs, 1.0, 1000.0, 80.0));

    // Per-speaker trim: the one most able to silence a channel, since it goes straight to a gain.
    // ±12 dB is the app's own range; anything outside it is corruption, not intent.
    for (int c = 0; c < 8; ++c) {
        chain_.setChannelTrimDb(c, Clamp(s.channelTrimDb[c], -12.0, 12.0, 0.0));
    }
}
