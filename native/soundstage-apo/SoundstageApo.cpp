// Soundstage APO — implementation. See SoundstageApo.h for what this is and why it's shaped this way.
#include "SoundstageApo.h"

#include <ks.h>
#include <ksmedia.h>
#include <propkey.h>
#include <new>

// {6F3C9A21-4E7B-4B36-9E1D-2A55C0D8E401}
extern "C" const GUID CLSID_SoundstageApo =
    { 0x6f3c9a21, 0x4e7b, 0x4b36, { 0x9e, 0x1d, 0x2a, 0x55, 0xc0, 0xd8, 0xe4, 0x01 } };

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

SoundstageApo::SoundstageApo() : refs_(1) {
    g_dllRefs.fetch_add(1, std::memory_order_relaxed);
}

SoundstageApo::~SoundstageApo() {
    CloseSharedState();
    g_dllRefs.fetch_sub(1, std::memory_order_relaxed);
}

// ---- IUnknown ---------------------------------------------------------------------------------

STDMETHODIMP SoundstageApo::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) { return E_POINTER; }
    *ppv = nullptr;

    if (IsEqualIID(riid, __uuidof(IUnknown))) {
        *ppv = static_cast<IAudioProcessingObject*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioProcessingObject))) {
        *ppv = static_cast<IAudioProcessingObject*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioProcessingObjectConfiguration))) {
        *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioProcessingObjectRT))) {
        *ppv = static_cast<IAudioProcessingObjectRT*>(this);
    } else if (IsEqualIID(riid, __uuidof(IAudioSystemEffects))) {
        *ppv = static_cast<IAudioSystemEffects*>(this);
    } else {
        return E_NOINTERFACE;
    }

    AddRef();
    return S_OK;
}

STDMETHODIMP_(ULONG) SoundstageApo::AddRef() {
    return refs_.fetch_add(1, std::memory_order_relaxed) + 1;
}

STDMETHODIMP_(ULONG) SoundstageApo::Release() {
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
    if (!IsFloat32(fmt)) { return APOERR_FORMAT_NOT_SUPPORTED; }
    if (fmt->nChannels < 1 || fmt->nChannels > 8) { return APOERR_FORMAT_NOT_SUPPORTED; }

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
    *pu32ChannelCount = channels_;
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
    if (!IsFloat32(inFmt)) { return APOERR_FORMAT_NOT_SUPPORTED; }

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
    SyncSettings();

    locked_ = true;
    return S_OK;
}

STDMETHODIMP SoundstageApo::UnlockForProcess() {
    locked_ = false;
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

    // The engine takes a stereo pair in and writes the endpoint's layout out. When the source is
    // already multichannel, the multichannel path keeps every channel instead of folding it down.
    if (ch > 2) {
        chain_.processBlockMulti(src, static_cast<int>(ch), dst, static_cast<int>(ch),
                                 static_cast<int>(frames));
    } else {
        chain_.processBlock(src, static_cast<int>(ch), dst, static_cast<int>(ch),
                            static_cast<int>(frames));
    }

    out->u32ValidFrameCount = frames;
    out->u32BufferFlags = BUFFER_VALID;
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
    chain_.setOutputGainDb(s.outputGainDb);

    chain_.enableEq(s.eqOn != 0);
    chain_.setEqNumBands(s.eqBandCount);
    for (int i = 0; i < s.eqBandCount && i < 36; ++i) {
        int type = s.eqBands[i].type;
        if (type < 0 || type > 4) { type = 0; }
        chain_.setEqBand(i, static_cast<soundstage::Equalizer::BandType>(type),
                         s.eqBands[i].freq, s.eqBands[i].gainDb, s.eqBands[i].q);
    }

    chain_.enableBass(s.bassOn != 0);
    chain_.bass().setAmount(s.bassAmount);
    chain_.bass().setCrossover(s.bassCrossover);
    chain_.bass().setDrive(s.bassDrive);

    chain_.enableCompressor(s.compOn != 0);
    chain_.compressor().setThresholdDb(s.compThresholdDb);
    chain_.compressor().setRatio(s.compRatio);
    chain_.compressor().setKneeDb(s.compKneeDb);
    chain_.compressor().setMakeupDb(s.compMakeupDb);
    chain_.compressor().setAttackMs(s.compAttackMs);
    chain_.compressor().setReleaseMs(s.compReleaseMs);

    chain_.enableNight(s.nightOn != 0);
    chain_.nightCompressor().setThresholdDb(s.nightThresholdDb);
    chain_.nightCompressor().setRatio(s.nightRatio);
    chain_.nightCompressor().setMakeupDb(s.nightMakeupDb);
    chain_.nightCompressor().setAttackMs(s.nightAttackMs);
    chain_.nightCompressor().setReleaseMs(s.nightReleaseMs);

    chain_.enableWidth(s.widthOn != 0);
    chain_.width().setWidth(s.width);

    chain_.enableReverb(s.reverbOn != 0);
    chain_.reverb().setSize(s.rvSize);
    chain_.reverb().setDecaySeconds(s.rvDecay);
    chain_.reverb().setDamping(s.rvDamping);
    chain_.reverb().setPreDelayMs(s.rvPreDelayMs);
    chain_.reverb().setWidth(s.rvWidth);
    chain_.reverb().setMix(s.rvMix);
    chain_.reverb().setDiffusion(s.rvDiffusion);
    chain_.reverb().setLowCutHz(s.rvLowCutHz);
    chain_.reverb().setHighCutHz(s.rvHighCutHz);
    chain_.reverb().setEarlyLevel(s.rvEarly);
    chain_.reverb().setModulation(s.rvModulation);

    chain_.enableUpmix(s.upmixOn != 0);
    chain_.setUpmixAmount(s.upmixAmount);
    chain_.upmix().setCenterGain(s.upmixCenter);
    chain_.upmix().setLfeGain(s.upmixLfe);
    chain_.enableSubFeed(s.subFeedOn != 0);

    chain_.bassManager().setEnabled(s.bassMgmtOn != 0);
    chain_.bassManager().setCrossover(s.bmCrossover);
    chain_.bassManager().setSubGain(s.bmSubGain);
    for (int c = 0; c < 8; ++c) {
        chain_.bassManager().setSmall(c, (s.bmSmallMask & (1 << c)) != 0);
    }

    chain_.enableLimiter(s.limiterOn != 0);
    chain_.limiter().setCeilingDb(s.limCeilingDb);
    chain_.limiter().setRelease(s.limReleaseMs);

    for (int c = 0; c < 8; ++c) {
        chain_.setChannelTrimDb(c, s.channelTrimDb[c]);
    }
}
