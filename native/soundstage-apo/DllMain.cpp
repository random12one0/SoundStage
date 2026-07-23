// Soundstage APO — the COM plumbing: class factory, DLL exports, and self-registration.
//
// Windows loads this DLL into the audio engine and asks it, through COM, for an instance of our
// processing object. That means we need the standard four exports plus a class factory. There is no
// framework here on purpose — the whole plugin is this file and SoundstageApo.cpp, so there is
// nothing between our DSP and the audio path that we didn't write.
#include "SoundstageApo.h"

#include <olectl.h>
#include <strsafe.h>
#include <exception>
#include <new>

std::atomic<long> g_dllRefs{0};
static HMODULE g_module = nullptr;

namespace {

const wchar_t* const kFriendlyName = L"Soundstage Audio Effect";
const wchar_t* const kClsidText = L"{6F3C9A21-4E7B-4B36-9E1D-2A55C0D8E401}";

class SoundstageClassFactory final : public IClassFactory {
public:
    SoundstageClassFactory() : refs_(1) { g_dllRefs.fetch_add(1, std::memory_order_relaxed); }
    ~SoundstageClassFactory() { g_dllRefs.fetch_sub(1, std::memory_order_relaxed); }

    STDMETHOD(QueryInterface)(REFIID riid, void** ppv) override {
        if (!ppv) { return E_POINTER; }
        if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IClassFactory))) {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }

    STDMETHOD_(ULONG, AddRef)() override { return refs_.fetch_add(1) + 1; }
    STDMETHOD_(ULONG, Release)() override {
        const ULONG left = refs_.fetch_sub(1) - 1;
        if (left == 0) { delete this; }
        return left;
    }

    STDMETHOD(CreateInstance)(IUnknown* outer, REFIID riid, void** ppv) override {
        if (!ppv) { return E_POINTER; }
        *ppv = nullptr;

        // The audio engine always aggregates. Rejecting `outer` here — the obvious-looking thing to
        // do, and what most COM boilerplate does — makes every attempt to open a stream on the
        // device fail with CLASS_E_NOAGGREGATION.
        //
        // COM's rule for the aggregated case: the only interface an aggregator may ask for at
        // creation time is IUnknown, because it needs our non-delegating unknown to hold on to.
        if (outer && !IsEqualIID(riid, __uuidof(IUnknown))) { return E_NOINTERFACE; }

        // Nothing may escape this function. We are called from inside the audio service, across a
        // COM boundary, in a process whose death silences all system audio — so a C++ exception
        // leaking out here is not a bug report, it is a dead audio stack. Turn any failure into an
        // HRESULT and let Windows fall back to no effects.
        SoundstageApo* apo = nullptr;
        try {
            apo = new (std::nothrow) SoundstageApo(outer);
        } catch (const std::exception& e) {
            SoundstageLog("[factory] construction threw: %s", e.what());
            return E_FAIL;
        } catch (...) {
            SoundstageLog("[factory] construction threw (unknown)");
            return E_FAIL;
        }
        if (!apo) {
            SoundstageLog("[factory] out of memory");
            return E_OUTOFMEMORY;
        }
        SoundstageLog("[factory] instance constructed (aggregated=%s)", outer ? "yes" : "no");

        // Go through the non-delegating unknown: asking the delegating one would forward straight
        // back out to the aggregator and never return our own interface.
        IUnknown* nd = apo->NonDelegatingUnknown();
        const HRESULT hr = nd->QueryInterface(riid, ppv);
        nd->Release();   // balances the reference the constructor started with
        return hr;
    }

    STDMETHOD(LockServer)(BOOL lock) override {
        if (lock) { g_dllRefs.fetch_add(1); } else { g_dllRefs.fetch_sub(1); }
        return S_OK;
    }

private:
    std::atomic<ULONG> refs_;
};

/// Write one string value, creating the key. Registration is a handful of these; a helper keeps the
/// error handling in one place rather than repeated eight times.
LONG SetKeyValue(HKEY root, const wchar_t* subKey, const wchar_t* name, const wchar_t* value) {
    HKEY key = nullptr;
    LONG r = RegCreateKeyExW(root, subKey, 0, nullptr, REG_OPTION_NON_VOLATILE,
                             KEY_WRITE, nullptr, &key, nullptr);
    if (r != ERROR_SUCCESS) { return r; }

    r = RegSetValueExW(key, name, 0, REG_SZ,
                       reinterpret_cast<const BYTE*>(value),
                       static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return r;
}

/// Write one DWORD value, creating the key.
LONG SetKeyDword(HKEY root, const wchar_t* subKey, const wchar_t* name, DWORD value) {
    HKEY key = nullptr;
    LONG r = RegCreateKeyExW(root, subKey, 0, nullptr, REG_OPTION_NON_VOLATILE,
                             KEY_WRITE, nullptr, &key, nullptr);
    if (r != ERROR_SUCCESS) { return r; }

    r = RegSetValueExW(key, name, 0, REG_DWORD,
                       reinterpret_cast<const BYTE*>(&value), sizeof(value));
    RegCloseKey(key);
    return r;
}

// IID_IAudioProcessingObject. The audio engine reads this from the registry to know what the object
// can do before it ever creates one, so the value has to be spelled out here as well as returned
// from GetRegistrationProperties.
const wchar_t* const kApoInterfaceIid = L"{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}";

/// Where the audio engine looks up processing objects. This is NOT the same as COM registration, and
/// that distinction cost real debugging time: a plugin can be a perfectly valid, correctly registered
/// COM server that Windows will still never load, because the engine consults this list first and
/// silently skips anything missing from it. No error is logged when that happens.
const wchar_t* const kApoRegBase = L"SOFTWARE\\Classes\\AudioEngine\\AudioProcessingObjects";

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
        // Fires the moment anything maps this DLL. It is the only way to distinguish "Windows never
        // loaded us" from "Windows loaded us and then rejected the object" — from outside audiodg
        // the two look identical.
        SoundstageLog("[load]   DLL mapped into pid=%lu", GetCurrentProcessId());
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv);

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv) {
    if (!ppv) { return E_POINTER; }
    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, CLSID_SoundstageApo)) { return CLASS_E_CLASSNOTAVAILABLE; }

    auto* factory = new (std::nothrow) SoundstageClassFactory();
    if (!factory) { return E_OUTOFMEMORY; }

    const HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow() {
    return g_dllRefs.load(std::memory_order_acquire) == 0 ? S_OK : S_FALSE;
}

/// Register the COM class. This is only half of what makes the plugin run — the other half is
/// attaching it to a playback device, which the install script does, because that part is per
/// endpoint and needs to know which device the user actually wants processed.
STDAPI DllRegisterServer() {
    wchar_t path[MAX_PATH] = {};
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) { return SELFREG_E_CLASS; }

    wchar_t key[256] = {};
    StringCchPrintfW(key, ARRAYSIZE(key), L"CLSID\\%s", kClsidText);
    if (SetKeyValue(HKEY_CLASSES_ROOT, key, nullptr, kFriendlyName) != ERROR_SUCCESS) {
        return SELFREG_E_CLASS;
    }

    StringCchPrintfW(key, ARRAYSIZE(key), L"CLSID\\%s\\InprocServer32", kClsidText);
    if (SetKeyValue(HKEY_CLASSES_ROOT, key, nullptr, path) != ERROR_SUCCESS) {
        return SELFREG_E_CLASS;
    }

    // Both — the audio engine loads us in its own apartment.
    if (SetKeyValue(HKEY_CLASSES_ROOT, key, L"ThreadingModel", L"Both") != ERROR_SUCCESS) {
        return SELFREG_E_CLASS;
    }

    // ---- the second registration: the audio engine's own APO list ----
    //
    // These values mirror what GetRegistrationProperties returns. Windows reads them before creating
    // the object, to decide whether the plugin is worth loading at all.
    wchar_t apoKey[320] = {};
    StringCchPrintfW(apoKey, ARRAYSIZE(apoKey), L"%s\\%s", kApoRegBase, kClsidText);

    if (SetKeyValue(HKEY_LOCAL_MACHINE, apoKey, L"FriendlyName", L"Soundstage") != ERROR_SUCCESS) {
        return SELFREG_E_CLASS;
    }
    SetKeyValue(HKEY_LOCAL_MACHINE, apoKey, L"Copyright", L"Soundstage");
    SetKeyValue(HKEY_LOCAL_MACHINE, apoKey, L"APOInterface0", kApoInterfaceIid);

    SetKeyDword(HKEY_LOCAL_MACHINE, apoKey, L"MajorVersion", 1);
    SetKeyDword(HKEY_LOCAL_MACHINE, apoKey, L"MinorVersion", 0);

    // 13 = SAMPLESPERFRAME | FRAMESPERSECOND | BITSPERSAMPLE must match, i.e. APO_FLAG_DEFAULT: we
    // do not convert format, so the engine must hand us the same format on both sides.
    SetKeyDword(HKEY_LOCAL_MACHINE, apoKey, L"Flags", 13);

    SetKeyDword(HKEY_LOCAL_MACHINE, apoKey, L"MinInputConnections", 1);
    SetKeyDword(HKEY_LOCAL_MACHINE, apoKey, L"MaxInputConnections", 1);
    SetKeyDword(HKEY_LOCAL_MACHINE, apoKey, L"MinOutputConnections", 1);
    SetKeyDword(HKEY_LOCAL_MACHINE, apoKey, L"MaxOutputConnections", 1);
    SetKeyDword(HKEY_LOCAL_MACHINE, apoKey, L"MaxInstances", 0xFFFFFFFF);
    SetKeyDword(HKEY_LOCAL_MACHINE, apoKey, L"NumAPOInterfaces", 1);

    return S_OK;
}

STDAPI DllUnregisterServer() {
    wchar_t apoKey[320] = {};
    StringCchPrintfW(apoKey, ARRAYSIZE(apoKey), L"%s\\%s", kApoRegBase, kClsidText);
    RegDeleteKeyW(HKEY_LOCAL_MACHINE, apoKey);

    wchar_t key[256] = {};
    StringCchPrintfW(key, ARRAYSIZE(key), L"CLSID\\%s\\InprocServer32", kClsidText);
    RegDeleteKeyW(HKEY_CLASSES_ROOT, key);

    StringCchPrintfW(key, ARRAYSIZE(key), L"CLSID\\%s", kClsidText);
    RegDeleteKeyW(HKEY_CLASSES_ROOT, key);
    return S_OK;
}
