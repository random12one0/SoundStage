// Soundstage APO — the COM plumbing: class factory, DLL exports, and self-registration.
//
// Windows loads this DLL into the audio engine and asks it, through COM, for an instance of our
// processing object. That means we need the standard four exports plus a class factory. There is no
// framework here on purpose — the whole plugin is this file and SoundstageApo.cpp, so there is
// nothing between our DSP and the audio path that we didn't write.
#include "SoundstageApo.h"

#include <olectl.h>
#include <strsafe.h>

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
        if (outer) { return CLASS_E_NOAGGREGATION; }

        auto* apo = new (std::nothrow) SoundstageApo();
        if (!apo) { return E_OUTOFMEMORY; }

        const HRESULT hr = apo->QueryInterface(riid, ppv);
        apo->Release();
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

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

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

    return S_OK;
}

STDAPI DllUnregisterServer() {
    wchar_t key[256] = {};
    StringCchPrintfW(key, ARRAYSIZE(key), L"CLSID\\%s\\InprocServer32", kClsidText);
    RegDeleteKeyW(HKEY_CLASSES_ROOT, key);

    StringCchPrintfW(key, ARRAYSIZE(key), L"CLSID\\%s", kClsidText);
    RegDeleteKeyW(HKEY_CLASSES_ROOT, key);
    return S_OK;
}
