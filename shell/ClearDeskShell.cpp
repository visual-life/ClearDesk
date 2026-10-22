// ClearDesk Windows 11 top-level desktop context menu ("ClearDesk 新建组件").
// IExplorerCommand root with a submenu of widget kinds; each runs
// "<dir of this DLL>\ClearDesk.exe" --new-widget <kind>. Hosted by dllhost via
// the sparse package's com:SurrogateServer registration.
#include <windows.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <new>
#include <stdio.h>
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "ole32.lib")

// {6F3B1E52-8C1D-4E0B-9C7A-2D1C5DE0A201}
static const CLSID CLSID_ClearDeskMenu = { 0x6f3b1e52, 0x8c1d, 0x4e0b, { 0x9c, 0x7a, 0x2d, 0x1c, 0x5d, 0xe0, 0xa2, 0x01 } };

static HMODULE g_module;
static long g_refs;

struct Kind { const wchar_t* id; const wchar_t* label; };
static const Kind kKinds[] = {
    { L"note", L"\u4fbf\u7b7e" },
    { L"box", L"\u684c\u9762\u6536\u7eb3\u683c" },
    { L"clock", L"\u65f6\u949f" },
    { L"weather", L"\u5929\u6c14" },
    { L"music", L"\u97f3\u4e50" },
    { L"calendar", L"\u65e5\u5386" },
    { L"almanac", L"\u9ec4\u5386 / \u519c\u5386" },
    { L"performance", L"\u6027\u80fd" },
};
static const int kCount = sizeof(kKinds) / sizeof(kKinds[0]);

static void ExePath(wchar_t* out, DWORD cap)
{
    GetModuleFileNameW(g_module, out, cap);
    PathRemoveFileSpecW(out);
    PathAppendW(out, L"ClearDesk.exe");
}

static bool Disabled()
{
    // Settings toggle: ClearDesk writes HKCU\Software\ClearDesk\ModernMenu = 0 when switched off.
    DWORD v = 1, size = sizeof(v);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\ClearDesk", L"ModernMenu", RRF_RT_REG_DWORD, nullptr, &v, &size) == ERROR_SUCCESS)
        return v == 0;
    return false;
}

class Command : public IExplorerCommand, public IEnumExplorerCommand
{
    long m_ref = 1;
    int m_kind;     // -1 = root
    int m_cursor = 0;
public:
    explicit Command(int kind) : m_kind(kind) { InterlockedIncrement(&g_refs); }
    virtual ~Command() { InterlockedDecrement(&g_refs); }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IExplorerCommand) *ppv = static_cast<IExplorerCommand*>(this);
        else if (riid == IID_IEnumExplorerCommand && m_kind == -1) *ppv = static_cast<IEnumExplorerCommand*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_ref); }
    IFACEMETHODIMP_(ULONG) Release() { long r = InterlockedDecrement(&m_ref); if (!r) delete this; return r; }

    IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* name)
    {
        return SHStrDupW(m_kind < 0 ? L"ClearDesk \u65b0\u5efa\u7ec4\u4ef6" : kKinds[m_kind].label, name);
    }
    IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon)
    {
        if (m_kind >= 0) { *icon = nullptr; return E_NOTIMPL; }
        wchar_t p[MAX_PATH + 4]; ExePath(p, MAX_PATH); wcscat_s(p, L",0");
        return SHStrDupW(p, icon);
    }
    IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tip) { *tip = nullptr; return E_NOTIMPL; }
    IFACEMETHODIMP GetCanonicalName(GUID* g) { *g = GUID_NULL; return S_OK; }
    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state)
    {
        *state = (m_kind < 0 && Disabled()) ? ECS_HIDDEN : ECS_ENABLED;
        return S_OK;
    }
    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags)
    {
        *flags = m_kind < 0 ? ECF_HASSUBCOMMANDS : (m_kind == 1 ? ECF_SEPARATORAFTER : ECF_DEFAULT);
        return S_OK;
    }
    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** e)
    {
        if (m_kind >= 0) { *e = nullptr; return E_NOTIMPL; }
        Command* c = new (std::nothrow) Command(-1);
        if (!c) return E_OUTOFMEMORY;
        *e = c; // fresh enumerator positioned at 0
        return S_OK;
    }
    IFACEMETHODIMP Invoke(IShellItemArray*, IBindCtx*)
    {
        if (m_kind < 0) return S_OK;
        wchar_t exe[MAX_PATH]; ExePath(exe, MAX_PATH);
        wchar_t cmd[MAX_PATH + 64];
        swprintf_s(cmd, L"\"%s\" --new-widget %s", exe, kKinds[m_kind].id);
        wchar_t dir[MAX_PATH]; wcscpy_s(dir, exe); PathRemoveFileSpecW(dir);
        STARTUPINFOW si = { sizeof(si) }; PROCESS_INFORMATION pi = {};
        if (!CreateProcessW(exe, cmd, nullptr, nullptr, FALSE, CREATE_BREAKAWAY_FROM_JOB, nullptr, dir, &si, &pi) &&
            !CreateProcessW(exe, cmd, nullptr, nullptr, FALSE, 0, nullptr, dir, &si, &pi))
            return HRESULT_FROM_WIN32(GetLastError());
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        return S_OK;
    }

    // IEnumExplorerCommand (root only)
    IFACEMETHODIMP Next(ULONG n, IExplorerCommand** out, ULONG* fetched)
    {
        ULONG got = 0;
        while (got < n && m_cursor < kCount)
        {
            Command* c = new (std::nothrow) Command(m_cursor++);
            if (!c) break;
            out[got++] = c;
        }
        if (fetched) *fetched = got;
        return got == n ? S_OK : S_FALSE;
    }
    IFACEMETHODIMP Skip(ULONG n) { m_cursor = min(kCount, m_cursor + (int)n); return m_cursor < kCount ? S_OK : S_FALSE; }
    IFACEMETHODIMP Reset() { m_cursor = 0; return S_OK; }
    IFACEMETHODIMP Clone(IEnumExplorerCommand** e) { Command* c = new (std::nothrow) Command(-1); if (!c) return E_OUTOFMEMORY; c->m_cursor = m_cursor; *e = c; return S_OK; }
};

class Factory : public IClassFactory
{
    long m_ref = 1;
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = this; AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_ref); }
    IFACEMETHODIMP_(ULONG) Release() { long r = InterlockedDecrement(&m_ref); if (!r) delete this; return r; }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv)
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        Command* c = new (std::nothrow) Command(-1);
        if (!c) return E_OUTOFMEMORY;
        HRESULT hr = c->QueryInterface(riid, ppv);
        c->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) { if (lock) InterlockedIncrement(&g_refs); else InterlockedDecrement(&g_refs); return S_OK; }
};

BOOL APIENTRY DllMain(HMODULE m, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = m; DisableThreadLibraryCalls(m); }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (clsid != CLSID_ClearDeskMenu) return CLASS_E_CLASSNOTAVAILABLE;
    Factory* f = new (std::nothrow) Factory();
    if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow() { return g_refs == 0 ? S_OK : S_FALSE; }
