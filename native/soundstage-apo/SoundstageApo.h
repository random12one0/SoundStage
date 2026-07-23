// Soundstage APO — our own audio plugin, so no virtual cable is needed.
//
// An "Audio Processing Object" is the slot Windows provides for code that sits inside the audio
// engine for a particular playback device. Windows hands us a buffer of samples on their way to the
// speakers, we process them in place, and they carry on. That is the whole contract.
//
// This is NOT the arrangement Soundstage used before. The previous version wrote text into Equalizer
// APO's config file and let *that* program do the processing — a remote control for someone else's
// tool, with all the flakiness of a file watcher in the middle. Here the DSP is ours: the same
// EngineChain that the desktop app uses, running directly in the path. No third-party install, no
// config files, no virtual cable, no default-device juggling.
//
// What the environment demands of us, and why the code looks the way it does:
//
//   * We run inside audiodg.exe, on a real-time thread, against a hard deadline. Miss it and the
//     user hears crackling. So APOProcess allocates nothing, takes no locks, and calls nothing that
//     could block. Every buffer the engine needs is reserved in LockForProcess.
//     (Measured: the full chain at 7.1 costs about 3% of one core, so the budget is not the worry.)
//
//   * A crash here takes down all system audio, not just our app. Hence the defensive checks on
//     every buffer and the "if anything looks wrong, copy input to output" fallback — silence or a
//     bug should degrade to untouched audio, never to a dead audio service.
//
//   * Windows may call us with a different format than we expect, or with no data at all. Both are
//     normal, and both are handled rather than assumed away.
#pragma once

#include <windows.h>
#include <unknwn.h>
#include <audioenginebaseapo.h>
#include <audioclient.h>
#include <mmreg.h>

#include <atomic>
#include <memory>
#include <vector>

#include "soundstage/EngineChain.h"

// {6F3C9A21-4E7B-4B36-9E1D-2A55C0D8E401} — the APO's class id. Fixed forever: it is what the
// endpoint's registration points at, so changing it would orphan every existing install.
extern "C" const GUID CLSID_SoundstageApo;

/// Shared settings between the desktop app and the plugin.
///
/// The app runs as the user; the APO runs inside a protected audio process. They cannot simply call
/// each other, so the app publishes its settings to a small shared-memory block and the APO reads
/// them at the top of each buffer. One writer, one reader, and a sequence counter so the reader can
/// never act on a half-written update — a torn read here would be an audible glitch.
/// The settings themselves — plain data, so the reader can copy the whole lot in one go and then
/// re-check the sequence. Kept separate from the counter because an atomic isn't copyable.
struct SoundstageSettings {
    int    masterOn;
    double outputGainDb;

    int    eqOn;
    int    eqBandCount;
    struct { int type; double freq; double gainDb; double q; } eqBands[36];

    int    bassOn;      double bassAmount, bassCrossover, bassDrive;
    int    compOn;      double compThresholdDb, compRatio, compKneeDb, compMakeupDb, compAttackMs, compReleaseMs;
    int    nightOn;     double nightThresholdDb, nightRatio, nightMakeupDb, nightAttackMs, nightReleaseMs;
    int    widthOn;     double width;
    int    reverbOn;    double rvSize, rvDecay, rvDamping, rvPreDelayMs, rvWidth, rvMix,
                               rvDiffusion, rvLowCutHz, rvHighCutHz, rvEarly, rvModulation;
    int    upmixOn;     double upmixAmount, upmixCenter, upmixLfe;
    int    subFeedOn;
    int    bassMgmtOn;  double bmCrossover, bmSubGain; int bmSmallMask;
    int    limiterOn;   double limCeilingDb, limReleaseMs;
    double channelTrimDb[8];
};

struct SoundstageSharedState {
    std::atomic<unsigned> sequence;   // even = stable, odd = a write is in progress
    unsigned              version;
    SoundstageSettings    settings;
};

/// Where the shared block lives.
///
/// This is a file on disk rather than a named shared-memory object, and that choice is deliberate.
/// Named objects are scoped to a session: the app runs in the user's interactive session, but
/// audiodg.exe — the process we live inside — runs in session 0. A `Local\` name created by the app
/// is therefore invisible to us, and `Global\` requires SeCreateGlobalPrivilege, which an ordinary
/// user account does not have. A file has no session scope at all, so both sides simply map it.
///
/// It costs nothing at run time: after the mapping is set up, reads and writes are plain memory
/// accesses against the page cache. Nothing touches the disk on the audio path.
#define SOUNDSTAGE_SHARED_PATH L"C:\\ProgramData\\Soundstage\\engine-state.bin"
#define SOUNDSTAGE_SHARED_VERSION 1u

class SoundstageApo final
    : public IAudioProcessingObject
    , public IAudioProcessingObjectConfiguration
    , public IAudioProcessingObjectRT
    , public IAudioSystemEffects
{
public:
    SoundstageApo();
    virtual ~SoundstageApo();

    // ---- IUnknown ----
    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override;
    STDMETHOD_(ULONG, AddRef)() override;
    STDMETHOD_(ULONG, Release)() override;

    // ---- IAudioProcessingObject ----
    STDMETHOD(Reset)() override;
    STDMETHOD(GetLatency)(HNSTIME* pTime) override;
    STDMETHOD(GetRegistrationProperties)(APO_REG_PROPERTIES** ppRegProps) override;
    STDMETHOD(Initialize)(UINT32 cbDataSize, BYTE* pbyData) override;
    STDMETHOD(IsInputFormatSupported)(IAudioMediaType* pOppositeFormat,
                                      IAudioMediaType* pRequestedInputFormat,
                                      IAudioMediaType** ppSupportedInputFormat) override;
    STDMETHOD(IsOutputFormatSupported)(IAudioMediaType* pOppositeFormat,
                                       IAudioMediaType* pRequestedOutputFormat,
                                       IAudioMediaType** ppSupportedOutputFormat) override;
    STDMETHOD(GetInputChannelCount)(UINT32* pu32ChannelCount) override;

    // ---- IAudioProcessingObjectConfiguration ----
    STDMETHOD(LockForProcess)(UINT32 u32NumInputConnections,
                              APO_CONNECTION_DESCRIPTOR** ppInputConnections,
                              UINT32 u32NumOutputConnections,
                              APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override;
    STDMETHOD(UnlockForProcess)() override;

    // ---- IAudioProcessingObjectRT ----
    STDMETHOD_(void, APOProcess)(UINT32 u32NumInputConnections,
                                 APO_CONNECTION_PROPERTY** ppInputConnections,
                                 UINT32 u32NumOutputConnections,
                                 APO_CONNECTION_PROPERTY** ppOutputConnections) override;
    STDMETHOD_(UINT32, CalcInputFrames)(UINT32 u32OutputFrameCount) override;
    STDMETHOD_(UINT32, CalcOutputFrames)(UINT32 u32InputFrameCount) override;

private:
    /// Pull the app's latest settings into the engine. Called once per buffer, off the shared block,
    /// using a seqlock so a partial write is skipped rather than half-applied.
    void SyncSettings();

    void OpenSharedState();
    void CloseSharedState();

    std::atomic<ULONG> refs_;

    soundstage::EngineChain chain_;
    bool   locked_ = false;
    UINT32 channels_ = 2;
    UINT32 sampleRate_ = 48000;
    UINT32 maxFrames_ = 0;

    // Reserved in LockForProcess so the real-time path never allocates.
    std::vector<float> scratchIn_;
    std::vector<float> scratchOut_;

    HANDLE  sharedFile_ = nullptr;
    HANDLE  sharedMapping_ = nullptr;
    SoundstageSharedState* shared_ = nullptr;
    unsigned lastSequence_ = 0xFFFFFFFFu;
};
