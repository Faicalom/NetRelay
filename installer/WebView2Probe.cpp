// Installer-only x64 probe. Static CRT: no .NET or VC++ redistributable needed.
// Never starts the application, touches networking, or changes runtime settings.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <objbase.h>
#include <stdio.h>
#include <wchar.h>

int wmain() {
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(nullptr, path, 32768);
    if (!length || length >= 32768) return 10;
    wchar_t* slash = wcsrchr(path, L'\\');
    if (!slash) return 10;
    *(slash + 1) = 0;
    if (wcscat_s(path, L"WebView2Loader.dll")) return 10;
    HMODULE loader = LoadLibraryExW(path, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!loader) { wprintf(L"LOADER_ERROR=%lu\n", GetLastError()); return 11; }
    using GetVersion = HRESULT (STDAPICALLTYPE*)(PCWSTR, LPWSTR*);
    auto getVersion = reinterpret_cast<GetVersion>(
        GetProcAddress(loader, "GetAvailableCoreWebView2BrowserVersionString"));
    LPWSTR version = nullptr;
    HRESULT hr = getVersion ? getVersion(nullptr, &version) : E_NOINTERFACE;
    // Production runtime only, not a developer's Edge preview installation.
    bool ready = SUCCEEDED(hr) && version && *version &&
        !wcsstr(version, L"beta") && !wcsstr(version, L"dev") &&
        !wcsstr(version, L"canary");
    wprintf(L"WEBVIEW2_HRESULT=0x%08lX\nWEBVIEW2_VERSION=%ls\n",
        static_cast<unsigned long>(hr), version ? version : L"absent");
    if (version) CoTaskMemFree(version);
    FreeLibrary(loader);
    return ready ? 0 : 12;
}
