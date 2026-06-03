#include <windows.h>
#include <psapi.h>
#include <mmsystem.h>
#include <cstdint>
#include <cstring>
#include <cwchar>

namespace
{
    struct SharedState
    {
        volatile long long generation;
        double speed;
        unsigned char reserved[16];
    };

    using SleepFn = void (WINAPI*)(DWORD);
    using SleepExFn = DWORD (WINAPI*)(DWORD, BOOL);
    using GetTickCountFn = DWORD (WINAPI*)();
    using GetTickCount64Fn = ULONGLONG (WINAPI*)();
    using QueryPerformanceCounterFn = BOOL (WINAPI*)(LARGE_INTEGER*);
    using GetSystemTimeAsFileTimeFn = void (WINAPI*)(LPFILETIME);
    using GetSystemTimePreciseAsFileTimeFn = void (WINAPI*)(LPFILETIME);
    using SetTimerFn = UINT_PTR (WINAPI*)(HWND, UINT_PTR, UINT, TIMERPROC);
    using GetMessageTimeFn = LONG (WINAPI*)();
    using TimeGetTimeFn = DWORD (WINAPI*)();
    using TimeSetEventFn = MMRESULT (WINAPI*)(UINT, UINT, LPTIMECALLBACK, DWORD_PTR, UINT);
    using TimeKillEventFn = MMRESULT (WINAPI*)(UINT);

    SharedState* g_shared = nullptr;
    long long g_generation = 0;
    double g_speed = 1.0;

    SleepFn RealSleep = nullptr;
    SleepExFn RealSleepEx = nullptr;
    GetTickCountFn RealGetTickCount = nullptr;
    GetTickCount64Fn RealGetTickCount64 = nullptr;
    QueryPerformanceCounterFn RealQueryPerformanceCounter = nullptr;
    GetSystemTimeAsFileTimeFn RealGetSystemTimeAsFileTime = nullptr;
    GetSystemTimePreciseAsFileTimeFn RealGetSystemTimePreciseAsFileTime = nullptr;
    SetTimerFn RealSetTimer = nullptr;
    GetMessageTimeFn RealGetMessageTime = nullptr;
    TimeGetTimeFn RealTimeGetTime = nullptr;
    TimeSetEventFn RealTimeSetEvent = nullptr;
    TimeKillEventFn RealTimeKillEvent = nullptr;
    HANDLE g_workerThread = nullptr;
    SRWLOCK g_stateLock = SRWLOCK_INIT;
    bool g_debugEnabled = false;
    DWORD g_lastDebugTick = 0;

    static void InitSharedState();

    DWORD g_realBaseTick = 0;
    DWORD g_virtualBaseTick = 0;
    ULONGLONG g_realBaseTick64 = 0;
    ULONGLONG g_virtualBaseTick64 = 0;
    DWORD g_realBaseTimeGetTime = 0;
    DWORD g_virtualBaseTimeGetTime = 0;
    LONG g_realBaseMessageTime = 0;
    LONG g_virtualBaseMessageTime = 0;
    LARGE_INTEGER g_realBaseQpc{};
    LARGE_INTEGER g_virtualBaseQpc{};
    FILETIME g_realBaseFileTime{};
    FILETIME g_virtualBaseFileTime{};

    static bool IsDebugEnabled()
    {
        wchar_t value[16]{};
        const auto capacity = static_cast<DWORD>(sizeof(value) / sizeof(value[0]));
        const auto length = GetEnvironmentVariableW(L"CEF_FLASH_BROWSER_SPEEDGEAR_DEBUG", value, capacity);
        if (length == 0 || length >= capacity)
            return false;

        return wcscmp(value, L"1") == 0
            || _wcsicmp(value, L"true") == 0
            || _wcsicmp(value, L"yes") == 0;
    }

    static void DebugWrite(const wchar_t* message)
    {
        if (g_debugEnabled)
            OutputDebugStringW(message);
    }

    static void DebugWriteSpeedChanged(double speed)
    {
        if (!g_debugEnabled)
            return;

        wchar_t message[128]{};
        swprintf_s(message, L"[SpeedGear] speed changed: %.3fx\n", speed);
        OutputDebugStringW(message);
    }

    static ULONGLONG FileTimeToU64(FILETIME ft)
    {
        return (static_cast<ULONGLONG>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    }

    static FILETIME U64ToFileTime(ULONGLONG value)
    {
        FILETIME ft{};
        ft.dwLowDateTime = static_cast<DWORD>(value);
        ft.dwHighDateTime = static_cast<DWORD>(value >> 32);
        return ft;
    }

    static FARPROC Resolve(const wchar_t* moduleName, const char* functionName)
    {
        auto module = GetModuleHandleW(moduleName);
        if (!module)
            module = LoadLibraryW(moduleName);
        return module ? GetProcAddress(module, functionName) : nullptr;
    }

    static void GetRealFileTime(LPFILETIME out)
    {
        if (RealGetSystemTimePreciseAsFileTime)
        {
            RealGetSystemTimePreciseAsFileTime(out);
        }
        else if (RealGetSystemTimeAsFileTime)
        {
            RealGetSystemTimeAsFileTime(out);
        }
        else
        {
            ::GetSystemTimeAsFileTime(out);
        }
    }

    static DWORD ScaleDwordTime(DWORD realNow, DWORD realBase, DWORD virtualBase, double speed)
    {
        const auto delta = realNow - realBase;
        return static_cast<DWORD>(virtualBase + static_cast<DWORD>(delta * speed));
    }

    static ULONGLONG ScaleUlongLongTime(ULONGLONG realNow, ULONGLONG realBase, ULONGLONG virtualBase, double speed)
    {
        const auto delta = realNow - realBase;
        return static_cast<ULONGLONG>(virtualBase + static_cast<ULONGLONG>(delta * speed));
    }

    static LONGLONG ScaleLongLongTime(LONGLONG realNow, LONGLONG realBase, LONGLONG virtualBase, double speed)
    {
        const auto delta = realNow - realBase;
        return static_cast<LONGLONG>(virtualBase + static_cast<LONGLONG>(delta * speed));
    }

    static DWORD ScaleDelay(DWORD ms, double speed)
    {
        if (ms == 0)
            return 0;

        if (speed <= 0)
            speed = 1.0;

        const auto scaled = static_cast<DWORD>(ms / speed);
        return scaled < 1 ? 1 : scaled;
    }

    static void InitializeBases()
    {
        g_realBaseTick = RealGetTickCount ? RealGetTickCount() : ::GetTickCount();
        g_virtualBaseTick = g_realBaseTick;
        g_realBaseTick64 = RealGetTickCount64 ? RealGetTickCount64() : ::GetTickCount64();
        g_virtualBaseTick64 = g_realBaseTick64;
        g_realBaseTimeGetTime = RealTimeGetTime ? RealTimeGetTime() : ::timeGetTime();
        g_virtualBaseTimeGetTime = g_realBaseTimeGetTime;
        g_realBaseMessageTime = RealGetMessageTime ? RealGetMessageTime() : ::GetMessageTime();
        g_virtualBaseMessageTime = g_realBaseMessageTime;
        if (RealQueryPerformanceCounter)
            RealQueryPerformanceCounter(&g_realBaseQpc);
        else
            ::QueryPerformanceCounter(&g_realBaseQpc);
        g_virtualBaseQpc = g_realBaseQpc;
        GetRealFileTime(&g_realBaseFileTime);
        g_virtualBaseFileTime = g_realBaseFileTime;
    }

    static void RebaseForSpeedChange(double newSpeed)
    {
        const auto oldSpeed = g_speed;

        const auto tickNow = RealGetTickCount ? RealGetTickCount() : ::GetTickCount();
        g_virtualBaseTick = ScaleDwordTime(tickNow, g_realBaseTick, g_virtualBaseTick, oldSpeed);
        g_realBaseTick = tickNow;

        const auto tick64Now = RealGetTickCount64 ? RealGetTickCount64() : ::GetTickCount64();
        g_virtualBaseTick64 = ScaleUlongLongTime(tick64Now, g_realBaseTick64, g_virtualBaseTick64, oldSpeed);
        g_realBaseTick64 = tick64Now;

        const auto timeGetTimeNow = RealTimeGetTime ? RealTimeGetTime() : ::timeGetTime();
        g_virtualBaseTimeGetTime = ScaleDwordTime(timeGetTimeNow, g_realBaseTimeGetTime, g_virtualBaseTimeGetTime, oldSpeed);
        g_realBaseTimeGetTime = timeGetTimeNow;

        const auto messageTimeNow = RealGetMessageTime ? RealGetMessageTime() : ::GetMessageTime();
        g_virtualBaseMessageTime = static_cast<LONG>(ScaleDwordTime(
            static_cast<DWORD>(messageTimeNow),
            static_cast<DWORD>(g_realBaseMessageTime),
            static_cast<DWORD>(g_virtualBaseMessageTime),
            oldSpeed));
        g_realBaseMessageTime = messageTimeNow;

        LARGE_INTEGER qpcNow{};
        if (RealQueryPerformanceCounter)
            RealQueryPerformanceCounter(&qpcNow);
        else
            ::QueryPerformanceCounter(&qpcNow);
        g_virtualBaseQpc.QuadPart = ScaleLongLongTime(qpcNow.QuadPart, g_realBaseQpc.QuadPart, g_virtualBaseQpc.QuadPart, oldSpeed);
        g_realBaseQpc = qpcNow;

        FILETIME fileTimeNow{};
        GetRealFileTime(&fileTimeNow);
        const auto virtualFileTime = ScaleUlongLongTime(
            FileTimeToU64(fileTimeNow),
            FileTimeToU64(g_realBaseFileTime),
            FileTimeToU64(g_virtualBaseFileTime),
            oldSpeed);
        g_virtualBaseFileTime = U64ToFileTime(virtualFileTime);
        g_realBaseFileTime = fileTimeNow;

        g_speed = newSpeed;
        DebugWriteSpeedChanged(newSpeed);
    }

    static void SyncSpeedLocked()
    {
        if (g_shared && g_shared->generation != g_generation)
        {
            g_generation = g_shared->generation;
            const auto nextSpeed = g_shared->speed > 0 ? g_shared->speed : 1.0;
            RebaseForSpeedChange(nextSpeed);
        }
    }

    void WINAPI HookSleep(DWORD ms)
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        const auto scaled = ScaleDelay(ms, g_speed);
        ReleaseSRWLockExclusive(&g_stateLock);
        RealSleep(scaled);
    }

    DWORD WINAPI HookSleepEx(DWORD ms, BOOL alertable)
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        const auto scaled = ScaleDelay(ms, g_speed);
        ReleaseSRWLockExclusive(&g_stateLock);
        return RealSleepEx(scaled, alertable);
    }

    DWORD WINAPI HookGetTickCount()
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        const auto now = RealGetTickCount();
        const auto scaled = ScaleDwordTime(now, g_realBaseTick, g_virtualBaseTick, g_speed);
        ReleaseSRWLockExclusive(&g_stateLock);
        return scaled;
    }

    ULONGLONG WINAPI HookGetTickCount64()
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        const auto now = RealGetTickCount64();
        const auto scaled = ScaleUlongLongTime(now, g_realBaseTick64, g_virtualBaseTick64, g_speed);
        ReleaseSRWLockExclusive(&g_stateLock);
        return scaled;
    }

    BOOL WINAPI HookQueryPerformanceCounter(LARGE_INTEGER* out)
    {
        if (!out)
            return FALSE;

        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        LARGE_INTEGER now{};
        const auto ok = RealQueryPerformanceCounter(&now);
        out->QuadPart = ScaleLongLongTime(now.QuadPart, g_realBaseQpc.QuadPart, g_virtualBaseQpc.QuadPart, g_speed);
        ReleaseSRWLockExclusive(&g_stateLock);
        return ok;
    }

    void WINAPI HookSystemTimeAsFileTime(LPFILETIME out)
    {
        if (!out)
            return;

        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        FILETIME now{};
        GetRealFileTime(&now);
        *out = U64ToFileTime(ScaleUlongLongTime(
            FileTimeToU64(now),
            FileTimeToU64(g_realBaseFileTime),
            FileTimeToU64(g_virtualBaseFileTime),
            g_speed));
        ReleaseSRWLockExclusive(&g_stateLock);
    }

    void WINAPI HookGetSystemTimeAsFileTime(LPFILETIME out)
    {
        HookSystemTimeAsFileTime(out);
    }

    void WINAPI HookGetSystemTimePreciseAsFileTime(LPFILETIME out)
    {
        HookSystemTimeAsFileTime(out);
    }

    UINT_PTR WINAPI HookSetTimer(HWND hwnd, UINT_PTR id, UINT elapse, TIMERPROC proc)
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        auto scaled = static_cast<UINT>(elapse / g_speed);
        ReleaseSRWLockExclusive(&g_stateLock);
        if (scaled < 1)
            scaled = 1;
        return RealSetTimer(hwnd, id, scaled, proc);
    }

    DWORD WINAPI HookTimeGetTime()
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        const auto now = RealTimeGetTime();
        const auto scaled = ScaleDwordTime(now, g_realBaseTimeGetTime, g_virtualBaseTimeGetTime, g_speed);
        ReleaseSRWLockExclusive(&g_stateLock);
        return scaled;
    }

    LONG WINAPI HookGetMessageTime()
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        const auto now = RealGetMessageTime();
        const auto scaled = static_cast<LONG>(ScaleDwordTime(
            static_cast<DWORD>(now),
            static_cast<DWORD>(g_realBaseMessageTime),
            static_cast<DWORD>(g_virtualBaseMessageTime),
            g_speed));
        ReleaseSRWLockExclusive(&g_stateLock);
        return scaled;
    }

    MMRESULT WINAPI HookTimeSetEvent(UINT delay, UINT resolution, LPTIMECALLBACK proc, DWORD_PTR user, UINT event)
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        auto scaled = static_cast<UINT>(ScaleDelay(delay, g_speed));
        ReleaseSRWLockExclusive(&g_stateLock);
        return RealTimeSetEvent(scaled, resolution, proc, user, event);
    }

    MMRESULT WINAPI HookTimeKillEvent(UINT timerId)
    {
        if (!RealTimeKillEvent)
            return MMSYSERR_ERROR;

        return RealTimeKillEvent(timerId);
    }

    static void PatchImport(HMODULE module, const char* importedName, FARPROC replacement)
    {
        auto base = reinterpret_cast<std::uint8_t*>(module);
        auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
        if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE)
            return;

        auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
        if (!nt || nt->Signature != IMAGE_NT_SIGNATURE)
            return;

        auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (!dir.VirtualAddress)
            return;

        auto desc = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);
        for (; desc->Name; ++desc)
        {
            auto thunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + desc->FirstThunk);
            auto orig = desc->OriginalFirstThunk
                ? reinterpret_cast<IMAGE_THUNK_DATA*>(base + desc->OriginalFirstThunk)
                : thunk;

            for (; thunk->u1.Function && orig->u1.AddressOfData; ++thunk, ++orig)
            {
                if (IMAGE_SNAP_BY_ORDINAL(orig->u1.Ordinal))
                    continue;

                auto byName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + orig->u1.AddressOfData);
                if (std::strcmp(reinterpret_cast<const char*>(byName->Name), importedName) != 0)
                    continue;

                DWORD oldProtect = 0;
                if (VirtualProtect(&thunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProtect))
                {
                    thunk->u1.Function = reinterpret_cast<ULONG_PTR>(replacement);
                    VirtualProtect(&thunk->u1.Function, sizeof(void*), oldProtect, &oldProtect);
                }
            }
        }
    }

    static void PatchAllModules()
    {
        HMODULE modules[1024]{};
        DWORD needed = 0;
        if (!EnumProcessModules(GetCurrentProcess(), modules, sizeof(modules), &needed))
            return;

        const auto count = needed / sizeof(HMODULE);
        for (DWORD i = 0; i < count; ++i)
        {
            PatchImport(modules[i], "Sleep", reinterpret_cast<FARPROC>(HookSleep));
            PatchImport(modules[i], "SleepEx", reinterpret_cast<FARPROC>(HookSleepEx));
            PatchImport(modules[i], "GetTickCount", reinterpret_cast<FARPROC>(HookGetTickCount));
            PatchImport(modules[i], "GetTickCount64", reinterpret_cast<FARPROC>(HookGetTickCount64));
            PatchImport(modules[i], "QueryPerformanceCounter", reinterpret_cast<FARPROC>(HookQueryPerformanceCounter));
            PatchImport(modules[i], "GetSystemTimeAsFileTime", reinterpret_cast<FARPROC>(HookGetSystemTimeAsFileTime));
            PatchImport(modules[i], "GetSystemTimePreciseAsFileTime", reinterpret_cast<FARPROC>(HookGetSystemTimePreciseAsFileTime));
            PatchImport(modules[i], "SetTimer", reinterpret_cast<FARPROC>(HookSetTimer));
            PatchImport(modules[i], "GetMessageTime", reinterpret_cast<FARPROC>(HookGetMessageTime));
            PatchImport(modules[i], "timeGetTime", reinterpret_cast<FARPROC>(HookTimeGetTime));
            PatchImport(modules[i], "timeSetEvent", reinterpret_cast<FARPROC>(HookTimeSetEvent));
            PatchImport(modules[i], "timeKillEvent", reinterpret_cast<FARPROC>(HookTimeKillEvent));
        }
    }

    static void OutputDebugStatusIfDue()
    {
        if (!g_debugEnabled || !RealGetTickCount)
            return;

        const auto now = RealGetTickCount();
        if (g_lastDebugTick != 0 && now - g_lastDebugTick < 5000)
            return;

        g_lastDebugTick = now;

        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();

        const auto realTick = RealGetTickCount ? RealGetTickCount() : ::GetTickCount();
        const auto virtualTick = ScaleDwordTime(realTick, g_realBaseTick, g_virtualBaseTick, g_speed);
        const auto tickDelta = virtualTick - g_virtualBaseTick;

        DWORD timeGetTimeDelta = 0;
        if (RealTimeGetTime)
        {
            const auto realTimeGetTime = RealTimeGetTime();
            const auto virtualTimeGetTime = ScaleDwordTime(realTimeGetTime, g_realBaseTimeGetTime, g_virtualBaseTimeGetTime, g_speed);
            timeGetTimeDelta = virtualTimeGetTime - g_virtualBaseTimeGetTime;
        }

        LONGLONG qpcDelta = 0;
        if (RealQueryPerformanceCounter)
        {
            LARGE_INTEGER realQpc{};
            if (RealQueryPerformanceCounter(&realQpc))
            {
                const auto virtualQpc = ScaleLongLongTime(realQpc.QuadPart, g_realBaseQpc.QuadPart, g_virtualBaseQpc.QuadPart, g_speed);
                qpcDelta = virtualQpc - g_virtualBaseQpc.QuadPart;
            }
        }

        const auto speed = g_speed;
        ReleaseSRWLockExclusive(&g_stateLock);

        wchar_t message[256]{};
        swprintf_s(
            message,
            L"[SpeedGear] status speed=%.3fx tickDelta=%lu qpcDelta=%lld timeGetTimeDelta=%lu\n",
            speed,
            tickDelta,
            qpcDelta,
            timeGetTimeDelta);
        OutputDebugStringW(message);
    }

    DWORD WINAPI SpeedGearWorker(LPVOID)
    {
        g_debugEnabled = IsDebugEnabled();
        DebugWrite(L"[SpeedGear] DLL loaded\n");

        RealSleep = reinterpret_cast<SleepFn>(Resolve(L"kernel32.dll", "Sleep"));
        RealSleepEx = reinterpret_cast<SleepExFn>(Resolve(L"kernel32.dll", "SleepEx"));
        RealGetTickCount = reinterpret_cast<GetTickCountFn>(Resolve(L"kernel32.dll", "GetTickCount"));
        RealGetTickCount64 = reinterpret_cast<GetTickCount64Fn>(Resolve(L"kernel32.dll", "GetTickCount64"));
        RealQueryPerformanceCounter = reinterpret_cast<QueryPerformanceCounterFn>(Resolve(L"kernel32.dll", "QueryPerformanceCounter"));
        RealGetSystemTimeAsFileTime = reinterpret_cast<GetSystemTimeAsFileTimeFn>(Resolve(L"kernel32.dll", "GetSystemTimeAsFileTime"));
        RealGetSystemTimePreciseAsFileTime = reinterpret_cast<GetSystemTimePreciseAsFileTimeFn>(Resolve(L"kernel32.dll", "GetSystemTimePreciseAsFileTime"));
        RealSetTimer = reinterpret_cast<SetTimerFn>(Resolve(L"user32.dll", "SetTimer"));
        RealGetMessageTime = reinterpret_cast<GetMessageTimeFn>(Resolve(L"user32.dll", "GetMessageTime"));
        RealTimeGetTime = reinterpret_cast<TimeGetTimeFn>(Resolve(L"winmm.dll", "timeGetTime"));
        RealTimeSetEvent = reinterpret_cast<TimeSetEventFn>(Resolve(L"winmm.dll", "timeSetEvent"));
        RealTimeKillEvent = reinterpret_cast<TimeKillEventFn>(Resolve(L"winmm.dll", "timeKillEvent"));

        InitSharedState();
        InitializeBases();

        for (;;)
        {
            PatchAllModules();
            OutputDebugStatusIfDue();

            if (RealSleep)
            {
                RealSleep(1000);
            }
            else
            {
                ::Sleep(1000);
            }
        }
    }

    static void InitSharedState()
    {
        auto mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(SharedState), L"Local\\CefFlashBrowser.SpeedGear");
        if (!mapping)
            return;

        g_shared = reinterpret_cast<SharedState*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedState)));
        if (g_shared)
        {
            g_generation = g_shared->generation;
            g_speed = g_shared->speed > 0 ? g_shared->speed : 1.0;
        }
    }
}

extern "C" __declspec(dllexport) void WINAPI CefFlashBrowserSpeedGearLoaded() {}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        g_workerThread = CreateThread(nullptr, 0, SpeedGearWorker, nullptr, 0, nullptr);
        if (g_workerThread)
        {
            CloseHandle(g_workerThread);
            g_workerThread = nullptr;
        }
    }
    return TRUE;
}
