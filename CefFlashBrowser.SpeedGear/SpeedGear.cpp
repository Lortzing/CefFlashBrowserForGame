#include <windows.h>
#include <psapi.h>
#include <mmsystem.h>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <limits>

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
    using QueryPerformanceFrequencyFn = BOOL (WINAPI*)(LARGE_INTEGER*);
    using GetSystemTimeAsFileTimeFn = void (WINAPI*)(LPFILETIME);
    using GetSystemTimePreciseAsFileTimeFn = void (WINAPI*)(LPFILETIME);
    using LoadLibraryAFn = HMODULE (WINAPI*)(LPCSTR);
    using LoadLibraryExAFn = HMODULE (WINAPI*)(LPCSTR, HANDLE, DWORD);
    using LoadLibraryWFn = HMODULE (WINAPI*)(LPCWSTR);
    using LoadLibraryExWFn = HMODULE (WINAPI*)(LPCWSTR, HANDLE, DWORD);
    using GetProcAddressFn = FARPROC (WINAPI*)(HMODULE, LPCSTR);
    using SetWaitableTimerFn = BOOL (WINAPI*)(HANDLE, const LARGE_INTEGER*, LONG, PTIMERAPCROUTINE, LPVOID, BOOL);
    using SetWaitableTimerExFn = BOOL (WINAPI*)(HANDLE, const LARGE_INTEGER*, LONG, PTIMERAPCROUTINE, LPVOID, PREASON_CONTEXT, ULONG);
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
    QueryPerformanceFrequencyFn RealQueryPerformanceFrequency = nullptr;
    GetSystemTimeAsFileTimeFn RealGetSystemTimeAsFileTime = nullptr;
    GetSystemTimePreciseAsFileTimeFn RealGetSystemTimePreciseAsFileTime = nullptr;
    LoadLibraryAFn RealLoadLibraryA = nullptr;
    LoadLibraryExAFn RealLoadLibraryExA = nullptr;
    LoadLibraryWFn RealLoadLibraryW = nullptr;
    LoadLibraryExWFn RealLoadLibraryExW = nullptr;
    GetProcAddressFn RealGetProcAddress = nullptr;
    SetWaitableTimerFn RealSetWaitableTimer = nullptr;
    SetWaitableTimerExFn RealSetWaitableTimerEx = nullptr;
    SetTimerFn RealSetTimer = nullptr;
    GetMessageTimeFn RealGetMessageTime = nullptr;
    TimeGetTimeFn RealTimeGetTime = nullptr;
    TimeSetEventFn RealTimeSetEvent = nullptr;
    TimeKillEventFn RealTimeKillEvent = nullptr;
    HANDLE g_workerThread = nullptr;
    SRWLOCK g_stateLock = SRWLOCK_INIT;
    bool g_debugEnabled = false;
    DWORD g_lastDebugTick = 0;
    volatile LONG g_patchInProgress = 0;
    DWORD g_lastPatchModuleCount = 0;
    DWORD g_lastPatchEntryCount = 0;
    DWORD g_lastPatchFailureCount = 0;
    wchar_t g_lastPatchFailedModule[MAX_PATH]{};
    volatile LONG g_patchRequested = 1;

    static void InitSharedState();
    static void PatchAllModules();

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

        if (ms == INFINITE)
            return INFINITE;

        if (speed <= 0)
            speed = 1.0;

        const auto scaledDouble = ms / speed;
        if (scaledDouble >= INFINITE)
            return INFINITE - 1;

        const auto scaled = static_cast<DWORD>(scaledDouble);
        return scaled < 1 ? 1 : scaled;
    }

    static LONG ScalePeriod(LONG period, double speed)
    {
        if (period <= 0)
            return 0;

        if (speed <= 0)
            speed = 1.0;

        const auto scaledDouble = period / speed;
        if (scaledDouble >= (std::numeric_limits<LONG>::max)())
            return (std::numeric_limits<LONG>::max)();

        const auto scaled = static_cast<LONG>(scaledDouble);
        return scaled < 1 ? 1 : scaled;
    }

    static ULONGLONG SignedAbsToU64(LONGLONG value)
    {
        if (value >= 0)
            return static_cast<ULONGLONG>(value);

        return static_cast<ULONGLONG>(-(value + 1)) + 1;
    }

    static LONGLONG VirtualFileTimeToRealFileTime(LONGLONG virtualFileTime, double speed)
    {
        if (speed <= 0)
            speed = 1.0;

        const auto virtualBase = FileTimeToU64(g_virtualBaseFileTime);
        const auto realBase = FileTimeToU64(g_realBaseFileTime);
        const auto virtualValue = static_cast<ULONGLONG>(virtualFileTime);

        if (virtualValue <= virtualBase)
            return static_cast<LONGLONG>(realBase);

        const auto virtualDelta = virtualValue - virtualBase;
        const auto realDeltaDouble = virtualDelta / speed;
        const auto signedMax = static_cast<ULONGLONG>((std::numeric_limits<LONGLONG>::max)());
        if (realBase >= signedMax)
            return (std::numeric_limits<LONGLONG>::max)();

        const auto maxDelta = signedMax - realBase;
        auto realDelta = realDeltaDouble >= static_cast<double>(maxDelta)
            ? maxDelta
            : static_cast<ULONGLONG>(realDeltaDouble);
        if (realDelta > maxDelta)
            realDelta = maxDelta;
        return static_cast<LONGLONG>(realBase + realDelta);
    }

    static LARGE_INTEGER ScaleWaitableDueTime(const LARGE_INTEGER* dueTime, double speed)
    {
        LARGE_INTEGER scaled{};
        if (!dueTime)
            return scaled;

        scaled = *dueTime;
        if (scaled.QuadPart > 0)
        {
            scaled.QuadPart = VirtualFileTimeToRealFileTime(scaled.QuadPart, speed);
            return scaled;
        }

        if (scaled.QuadPart == 0)
            return scaled;

        if (speed <= 0)
            speed = 1.0;

        const auto units = SignedAbsToU64(scaled.QuadPart);
        const auto signedMax = static_cast<ULONGLONG>((std::numeric_limits<LONGLONG>::max)());
        const auto scaledDouble = units / speed;
        auto scaledUnits = scaledDouble >= static_cast<double>(signedMax)
            ? signedMax
            : static_cast<ULONGLONG>(scaledDouble);
        if (scaledUnits > signedMax)
            scaledUnits = signedMax;
        if (scaledUnits > 0 && scaledUnits < 10000)
            scaledUnits = 10000;
        if (scaledUnits == 0 && units != 0)
            scaledUnits = 10000;

        scaled.QuadPart = -static_cast<LONGLONG>(scaledUnits);
        return scaled;
    }

    static bool TryReadSharedSpeed(long long* generation, double* speed)
    {
        if (!g_shared || !generation || !speed)
            return false;

        for (int i = 0; i < 8; ++i)
        {
            const auto startGeneration = g_shared->generation;
            MemoryBarrier();

            if ((startGeneration & 1) != 0)
                continue;

            const auto currentSpeed = g_shared->speed;
            MemoryBarrier();

            const auto endGeneration = g_shared->generation;
            if (startGeneration == endGeneration && (endGeneration & 1) == 0)
            {
                *generation = endGeneration;
                *speed = currentSpeed;
                return true;
            }
        }

        return false;
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
        long long generation = 0;
        double speed = 1.0;
        if (TryReadSharedSpeed(&generation, &speed) && generation != g_generation)
        {
            g_generation = generation;
            const auto nextSpeed = speed > 0 ? speed : 1.0;
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

    BOOL WINAPI HookQueryPerformanceFrequency(LARGE_INTEGER* out)
    {
        if (!RealQueryPerformanceFrequency)
            return FALSE;

        return RealQueryPerformanceFrequency(out);
    }

    BOOL WINAPI HookSetWaitableTimer(
        HANDLE timer,
        const LARGE_INTEGER* dueTime,
        LONG period,
        PTIMERAPCROUTINE completionRoutine,
        LPVOID arg,
        BOOL resume)
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        const auto scaledDueTime = ScaleWaitableDueTime(dueTime, g_speed);
        const auto scaledPeriod = ScalePeriod(period, g_speed);
        ReleaseSRWLockExclusive(&g_stateLock);

        return RealSetWaitableTimer(timer, dueTime ? &scaledDueTime : nullptr, scaledPeriod, completionRoutine, arg, resume);
    }

    BOOL WINAPI HookSetWaitableTimerEx(
        HANDLE timer,
        const LARGE_INTEGER* dueTime,
        LONG period,
        PTIMERAPCROUTINE completionRoutine,
        LPVOID arg,
        PREASON_CONTEXT wakeContext,
        ULONG tolerableDelay)
    {
        AcquireSRWLockExclusive(&g_stateLock);
        SyncSpeedLocked();
        const auto scaledDueTime = ScaleWaitableDueTime(dueTime, g_speed);
        const auto scaledPeriod = ScalePeriod(period, g_speed);
        ReleaseSRWLockExclusive(&g_stateLock);

        return RealSetWaitableTimerEx(timer, dueTime ? &scaledDueTime : nullptr, scaledPeriod, completionRoutine, arg, wakeContext, tolerableDelay);
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
        auto scaled = static_cast<UINT>(ScaleDelay(elapse, g_speed));
        ReleaseSRWLockExclusive(&g_stateLock);
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

    static void RequestPatchAllModules()
    {
        InterlockedExchange(&g_patchRequested, 1);
    }

    HMODULE WINAPI HookLoadLibraryW(LPCWSTR fileName)
    {
        if (!RealLoadLibraryW)
            return nullptr;

        const auto module = RealLoadLibraryW(fileName);
        if (module)
            RequestPatchAllModules();
        return module;
    }

    HMODULE WINAPI HookLoadLibraryA(LPCSTR fileName)
    {
        if (!RealLoadLibraryA)
            return nullptr;

        const auto module = RealLoadLibraryA(fileName);
        if (module)
            RequestPatchAllModules();
        return module;
    }

    HMODULE WINAPI HookLoadLibraryExW(LPCWSTR fileName, HANDLE file, DWORD flags)
    {
        if (!RealLoadLibraryExW)
            return nullptr;

        const auto module = RealLoadLibraryExW(fileName, file, flags);
        if (module)
            RequestPatchAllModules();
        return module;
    }

    HMODULE WINAPI HookLoadLibraryExA(LPCSTR fileName, HANDLE file, DWORD flags)
    {
        if (!RealLoadLibraryExA)
            return nullptr;

        const auto module = RealLoadLibraryExA(fileName, file, flags);
        if (module)
            RequestPatchAllModules();
        return module;
    }

    FARPROC WINAPI HookGetProcAddress(HMODULE module, LPCSTR procName)
    {
        if (!RealGetProcAddress)
            return nullptr;

        const auto resolved = RealGetProcAddress(module, procName);
        if (!resolved || IS_INTRESOURCE(procName))
            return resolved;

        if (std::strcmp(procName, "Sleep") == 0 && resolved == reinterpret_cast<FARPROC>(RealSleep))
            return reinterpret_cast<FARPROC>(HookSleep);
        if (std::strcmp(procName, "SleepEx") == 0 && resolved == reinterpret_cast<FARPROC>(RealSleepEx))
            return reinterpret_cast<FARPROC>(HookSleepEx);
        if (std::strcmp(procName, "GetTickCount") == 0 && resolved == reinterpret_cast<FARPROC>(RealGetTickCount))
            return reinterpret_cast<FARPROC>(HookGetTickCount);
        if (std::strcmp(procName, "GetTickCount64") == 0 && resolved == reinterpret_cast<FARPROC>(RealGetTickCount64))
            return reinterpret_cast<FARPROC>(HookGetTickCount64);
        if (std::strcmp(procName, "QueryPerformanceCounter") == 0 && resolved == reinterpret_cast<FARPROC>(RealQueryPerformanceCounter))
            return reinterpret_cast<FARPROC>(HookQueryPerformanceCounter);
        if (std::strcmp(procName, "QueryPerformanceFrequency") == 0 && resolved == reinterpret_cast<FARPROC>(RealQueryPerformanceFrequency))
            return reinterpret_cast<FARPROC>(HookQueryPerformanceFrequency);
        if (std::strcmp(procName, "GetSystemTimeAsFileTime") == 0 && resolved == reinterpret_cast<FARPROC>(RealGetSystemTimeAsFileTime))
            return reinterpret_cast<FARPROC>(HookGetSystemTimeAsFileTime);
        if (std::strcmp(procName, "GetSystemTimePreciseAsFileTime") == 0 && resolved == reinterpret_cast<FARPROC>(RealGetSystemTimePreciseAsFileTime))
            return reinterpret_cast<FARPROC>(HookGetSystemTimePreciseAsFileTime);
        if (std::strcmp(procName, "SetWaitableTimer") == 0 && resolved == reinterpret_cast<FARPROC>(RealSetWaitableTimer))
            return reinterpret_cast<FARPROC>(HookSetWaitableTimer);
        if (std::strcmp(procName, "SetWaitableTimerEx") == 0 && resolved == reinterpret_cast<FARPROC>(RealSetWaitableTimerEx))
            return reinterpret_cast<FARPROC>(HookSetWaitableTimerEx);
        if (std::strcmp(procName, "LoadLibraryA") == 0 && resolved == reinterpret_cast<FARPROC>(RealLoadLibraryA))
            return reinterpret_cast<FARPROC>(HookLoadLibraryA);
        if (std::strcmp(procName, "LoadLibraryExA") == 0 && resolved == reinterpret_cast<FARPROC>(RealLoadLibraryExA))
            return reinterpret_cast<FARPROC>(HookLoadLibraryExA);
        if (std::strcmp(procName, "LoadLibraryW") == 0 && resolved == reinterpret_cast<FARPROC>(RealLoadLibraryW))
            return reinterpret_cast<FARPROC>(HookLoadLibraryW);
        if (std::strcmp(procName, "LoadLibraryExW") == 0 && resolved == reinterpret_cast<FARPROC>(RealLoadLibraryExW))
            return reinterpret_cast<FARPROC>(HookLoadLibraryExW);
        if (std::strcmp(procName, "GetProcAddress") == 0 && resolved == reinterpret_cast<FARPROC>(RealGetProcAddress))
            return reinterpret_cast<FARPROC>(HookGetProcAddress);
        if (std::strcmp(procName, "SetTimer") == 0 && resolved == reinterpret_cast<FARPROC>(RealSetTimer))
            return reinterpret_cast<FARPROC>(HookSetTimer);
        if (std::strcmp(procName, "GetMessageTime") == 0 && resolved == reinterpret_cast<FARPROC>(RealGetMessageTime))
            return reinterpret_cast<FARPROC>(HookGetMessageTime);
        if (std::strcmp(procName, "timeGetTime") == 0 && resolved == reinterpret_cast<FARPROC>(RealTimeGetTime))
            return reinterpret_cast<FARPROC>(HookTimeGetTime);
        if (std::strcmp(procName, "timeSetEvent") == 0 && resolved == reinterpret_cast<FARPROC>(RealTimeSetEvent))
            return reinterpret_cast<FARPROC>(HookTimeSetEvent);
        if (std::strcmp(procName, "timeKillEvent") == 0 && resolved == reinterpret_cast<FARPROC>(RealTimeKillEvent))
            return reinterpret_cast<FARPROC>(HookTimeKillEvent);

        return resolved;
    }

    static DWORD PatchImport(HMODULE module, const char* importedName, FARPROC expected, FARPROC replacement, bool* failed)
    {
        auto base = reinterpret_cast<std::uint8_t*>(module);
        auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
        if (!dos || dos->e_magic != IMAGE_DOS_SIGNATURE)
            return 0;

        auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
        if (!nt || nt->Signature != IMAGE_NT_SIGNATURE)
            return 0;

        auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (!dir.VirtualAddress)
            return 0;

        DWORD patched = 0;
        auto desc = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress);
        for (; desc->Name; ++desc)
        {
            auto thunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + desc->FirstThunk);
            auto orig = desc->OriginalFirstThunk
                ? reinterpret_cast<IMAGE_THUNK_DATA*>(base + desc->OriginalFirstThunk)
                : nullptr;

            for (; thunk->u1.Function; ++thunk)
            {
                if (thunk->u1.Function != reinterpret_cast<ULONG_PTR>(expected))
                {
                    if (orig)
                        ++orig;
                    continue;
                }

                if (orig)
                {
                    if (IMAGE_SNAP_BY_ORDINAL(orig->u1.Ordinal))
                    {
                        ++orig;
                        continue;
                    }

                    auto byName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + orig->u1.AddressOfData);
                    if (std::strcmp(reinterpret_cast<const char*>(byName->Name), importedName) != 0)
                    {
                        ++orig;
                        continue;
                    }
                }

                if (thunk->u1.Function == reinterpret_cast<ULONG_PTR>(replacement))
                {
                    if (orig)
                        ++orig;
                    continue;
                }

                DWORD oldProtect = 0;
                if (VirtualProtect(&thunk->u1.Function, sizeof(void*), PAGE_READWRITE, &oldProtect))
                {
                    thunk->u1.Function = reinterpret_cast<ULONG_PTR>(replacement);
                    VirtualProtect(&thunk->u1.Function, sizeof(void*), oldProtect, &oldProtect);
                    patched++;
                }
                else if (failed)
                {
                    *failed = true;
                }

                if (orig)
                    ++orig;
            }
        }

        return patched;
    }

    static void PatchAllModules()
    {
        if (InterlockedExchange(&g_patchInProgress, 1) != 0)
            return;

        HMODULE modules[1024]{};
        DWORD needed = 0;
        if (!EnumProcessModules(GetCurrentProcess(), modules, sizeof(modules), &needed))
        {
            InterlockedExchange(&g_patchInProgress, 0);
            return;
        }

        const auto count = needed / sizeof(HMODULE);
        DWORD patchedEntries = 0;
        DWORD failureCount = 0;
        wchar_t lastFailedModule[MAX_PATH]{};
        for (DWORD i = 0; i < count; ++i)
        {
            bool failed = false;
            if (RealSleep)
                patchedEntries += PatchImport(modules[i], "Sleep", reinterpret_cast<FARPROC>(RealSleep), reinterpret_cast<FARPROC>(HookSleep), &failed);
            if (RealSleepEx)
                patchedEntries += PatchImport(modules[i], "SleepEx", reinterpret_cast<FARPROC>(RealSleepEx), reinterpret_cast<FARPROC>(HookSleepEx), &failed);
            if (RealGetTickCount)
                patchedEntries += PatchImport(modules[i], "GetTickCount", reinterpret_cast<FARPROC>(RealGetTickCount), reinterpret_cast<FARPROC>(HookGetTickCount), &failed);
            if (RealGetTickCount64)
                patchedEntries += PatchImport(modules[i], "GetTickCount64", reinterpret_cast<FARPROC>(RealGetTickCount64), reinterpret_cast<FARPROC>(HookGetTickCount64), &failed);
            if (RealQueryPerformanceCounter)
                patchedEntries += PatchImport(modules[i], "QueryPerformanceCounter", reinterpret_cast<FARPROC>(RealQueryPerformanceCounter), reinterpret_cast<FARPROC>(HookQueryPerformanceCounter), &failed);
            if (RealQueryPerformanceFrequency)
                patchedEntries += PatchImport(modules[i], "QueryPerformanceFrequency", reinterpret_cast<FARPROC>(RealQueryPerformanceFrequency), reinterpret_cast<FARPROC>(HookQueryPerformanceFrequency), &failed);
            if (RealGetSystemTimeAsFileTime)
                patchedEntries += PatchImport(modules[i], "GetSystemTimeAsFileTime", reinterpret_cast<FARPROC>(RealGetSystemTimeAsFileTime), reinterpret_cast<FARPROC>(HookGetSystemTimeAsFileTime), &failed);
            if (RealGetSystemTimePreciseAsFileTime)
                patchedEntries += PatchImport(modules[i], "GetSystemTimePreciseAsFileTime", reinterpret_cast<FARPROC>(RealGetSystemTimePreciseAsFileTime), reinterpret_cast<FARPROC>(HookGetSystemTimePreciseAsFileTime), &failed);
            if (RealSetWaitableTimer)
                patchedEntries += PatchImport(modules[i], "SetWaitableTimer", reinterpret_cast<FARPROC>(RealSetWaitableTimer), reinterpret_cast<FARPROC>(HookSetWaitableTimer), &failed);
            if (RealSetWaitableTimerEx)
                patchedEntries += PatchImport(modules[i], "SetWaitableTimerEx", reinterpret_cast<FARPROC>(RealSetWaitableTimerEx), reinterpret_cast<FARPROC>(HookSetWaitableTimerEx), &failed);
            if (RealLoadLibraryA)
                patchedEntries += PatchImport(modules[i], "LoadLibraryA", reinterpret_cast<FARPROC>(RealLoadLibraryA), reinterpret_cast<FARPROC>(HookLoadLibraryA), &failed);
            if (RealLoadLibraryExA)
                patchedEntries += PatchImport(modules[i], "LoadLibraryExA", reinterpret_cast<FARPROC>(RealLoadLibraryExA), reinterpret_cast<FARPROC>(HookLoadLibraryExA), &failed);
            if (RealLoadLibraryW)
                patchedEntries += PatchImport(modules[i], "LoadLibraryW", reinterpret_cast<FARPROC>(RealLoadLibraryW), reinterpret_cast<FARPROC>(HookLoadLibraryW), &failed);
            if (RealLoadLibraryExW)
                patchedEntries += PatchImport(modules[i], "LoadLibraryExW", reinterpret_cast<FARPROC>(RealLoadLibraryExW), reinterpret_cast<FARPROC>(HookLoadLibraryExW), &failed);
            if (RealGetProcAddress)
                patchedEntries += PatchImport(modules[i], "GetProcAddress", reinterpret_cast<FARPROC>(RealGetProcAddress), reinterpret_cast<FARPROC>(HookGetProcAddress), &failed);
            if (RealSetTimer)
                patchedEntries += PatchImport(modules[i], "SetTimer", reinterpret_cast<FARPROC>(RealSetTimer), reinterpret_cast<FARPROC>(HookSetTimer), &failed);
            if (RealGetMessageTime)
                patchedEntries += PatchImport(modules[i], "GetMessageTime", reinterpret_cast<FARPROC>(RealGetMessageTime), reinterpret_cast<FARPROC>(HookGetMessageTime), &failed);
            if (RealTimeGetTime)
                patchedEntries += PatchImport(modules[i], "timeGetTime", reinterpret_cast<FARPROC>(RealTimeGetTime), reinterpret_cast<FARPROC>(HookTimeGetTime), &failed);
            if (RealTimeSetEvent)
                patchedEntries += PatchImport(modules[i], "timeSetEvent", reinterpret_cast<FARPROC>(RealTimeSetEvent), reinterpret_cast<FARPROC>(HookTimeSetEvent), &failed);
            if (RealTimeKillEvent)
                patchedEntries += PatchImport(modules[i], "timeKillEvent", reinterpret_cast<FARPROC>(RealTimeKillEvent), reinterpret_cast<FARPROC>(HookTimeKillEvent), &failed);

            if (failed)
            {
                failureCount++;
                GetModuleFileNameW(modules[i], lastFailedModule, static_cast<DWORD>(sizeof(lastFailedModule) / sizeof(lastFailedModule[0])));
            }
        }

        g_lastPatchModuleCount = count;
        g_lastPatchEntryCount = patchedEntries;
        g_lastPatchFailureCount = failureCount;
        if (lastFailedModule[0] != L'\0')
            wcscpy_s(g_lastPatchFailedModule, lastFailedModule);

        InterlockedExchange(&g_patchInProgress, 0);
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
        const auto realTickDelta = realTick - g_realBaseTick;
        const auto tickDelta = virtualTick - g_virtualBaseTick;

        DWORD realTimeGetTimeDelta = 0;
        DWORD timeGetTimeDelta = 0;
        if (RealTimeGetTime)
        {
            const auto realTimeGetTime = RealTimeGetTime();
            const auto virtualTimeGetTime = ScaleDwordTime(realTimeGetTime, g_realBaseTimeGetTime, g_virtualBaseTimeGetTime, g_speed);
            realTimeGetTimeDelta = realTimeGetTime - g_realBaseTimeGetTime;
            timeGetTimeDelta = virtualTimeGetTime - g_virtualBaseTimeGetTime;
        }

        LONGLONG realQpcDelta = 0;
        LONGLONG qpcDelta = 0;
        if (RealQueryPerformanceCounter)
        {
            LARGE_INTEGER realQpc{};
            if (RealQueryPerformanceCounter(&realQpc))
            {
                const auto virtualQpc = ScaleLongLongTime(realQpc.QuadPart, g_realBaseQpc.QuadPart, g_virtualBaseQpc.QuadPart, g_speed);
                realQpcDelta = realQpc.QuadPart - g_realBaseQpc.QuadPart;
                qpcDelta = virtualQpc - g_virtualBaseQpc.QuadPart;
            }
        }

        const auto speed = g_speed;
        const auto generation = g_generation;
        ReleaseSRWLockExclusive(&g_stateLock);

        wchar_t message[512]{};
        swprintf_s(
            message,
            L"[SpeedGear] status speed=%.3fx generation=%lld tick=%lu/%lu qpc=%lld/%lld timeGetTime=%lu/%lu patchModules=%lu patchEntries=%lu patchFailures=%lu lastPatchFailed=%s\n",
            speed,
            generation,
            realTickDelta,
            tickDelta,
            realQpcDelta,
            qpcDelta,
            realTimeGetTimeDelta,
            timeGetTimeDelta,
            g_lastPatchModuleCount,
            g_lastPatchEntryCount,
            g_lastPatchFailureCount,
            g_lastPatchFailedModule[0] ? g_lastPatchFailedModule : L"-");
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
        RealQueryPerformanceFrequency = reinterpret_cast<QueryPerformanceFrequencyFn>(Resolve(L"kernel32.dll", "QueryPerformanceFrequency"));
        RealGetSystemTimeAsFileTime = reinterpret_cast<GetSystemTimeAsFileTimeFn>(Resolve(L"kernel32.dll", "GetSystemTimeAsFileTime"));
        RealGetSystemTimePreciseAsFileTime = reinterpret_cast<GetSystemTimePreciseAsFileTimeFn>(Resolve(L"kernel32.dll", "GetSystemTimePreciseAsFileTime"));
        RealLoadLibraryA = reinterpret_cast<LoadLibraryAFn>(Resolve(L"kernel32.dll", "LoadLibraryA"));
        RealLoadLibraryExA = reinterpret_cast<LoadLibraryExAFn>(Resolve(L"kernel32.dll", "LoadLibraryExA"));
        RealLoadLibraryW = reinterpret_cast<LoadLibraryWFn>(Resolve(L"kernel32.dll", "LoadLibraryW"));
        RealLoadLibraryExW = reinterpret_cast<LoadLibraryExWFn>(Resolve(L"kernel32.dll", "LoadLibraryExW"));
        RealGetProcAddress = reinterpret_cast<GetProcAddressFn>(Resolve(L"kernel32.dll", "GetProcAddress"));
        RealSetWaitableTimer = reinterpret_cast<SetWaitableTimerFn>(Resolve(L"kernel32.dll", "SetWaitableTimer"));
        RealSetWaitableTimerEx = reinterpret_cast<SetWaitableTimerExFn>(Resolve(L"kernel32.dll", "SetWaitableTimerEx"));
        RealSetTimer = reinterpret_cast<SetTimerFn>(Resolve(L"user32.dll", "SetTimer"));
        RealGetMessageTime = reinterpret_cast<GetMessageTimeFn>(Resolve(L"user32.dll", "GetMessageTime"));
        RealTimeGetTime = reinterpret_cast<TimeGetTimeFn>(Resolve(L"winmm.dll", "timeGetTime"));
        RealTimeSetEvent = reinterpret_cast<TimeSetEventFn>(Resolve(L"winmm.dll", "timeSetEvent"));
        RealTimeKillEvent = reinterpret_cast<TimeKillEventFn>(Resolve(L"winmm.dll", "timeKillEvent"));

        InitSharedState();
        InitializeBases();

        for (;;)
        {
            if (InterlockedExchange(&g_patchRequested, 0) != 0)
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
            double speed = 1.0;
            if (TryReadSharedSpeed(&g_generation, &speed))
                g_speed = speed > 0 ? speed : 1.0;
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
