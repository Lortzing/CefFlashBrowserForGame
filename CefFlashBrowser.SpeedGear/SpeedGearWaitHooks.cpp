#include <windows.h>
#include <cstdint>
#include <limits>
#include "ThirdParty/MinHook/include/MinHook.h"

namespace
{
    struct SharedState
    {
        volatile long long generation;
        double speed;
        unsigned char reserved[16];
    };

    using WaitForSingleObjectFn = DWORD(WINAPI*)(HANDLE, DWORD);
    using WaitForSingleObjectExFn = DWORD(WINAPI*)(HANDLE, DWORD, BOOL);
    using WaitForMultipleObjectsFn = DWORD(WINAPI*)(DWORD, const HANDLE*, BOOL, DWORD);
    using WaitForMultipleObjectsExFn = DWORD(WINAPI*)(DWORD, const HANDLE*, BOOL, DWORD, BOOL);
    using MsgWaitForMultipleObjectsFn = DWORD(WINAPI*)(DWORD, const HANDLE*, BOOL, DWORD, DWORD);
    using MsgWaitForMultipleObjectsExFn = DWORD(WINAPI*)(DWORD, const HANDLE*, DWORD, DWORD, DWORD);
    using NtDelayExecutionFn = LONG(NTAPI*)(BOOLEAN, PLARGE_INTEGER);

    SharedState* g_shared = nullptr;

    WaitForSingleObjectFn RealWaitForSingleObject = nullptr;
    WaitForSingleObjectExFn RealWaitForSingleObjectEx = nullptr;
    WaitForMultipleObjectsFn RealWaitForMultipleObjects = nullptr;
    WaitForMultipleObjectsExFn RealWaitForMultipleObjectsEx = nullptr;
    MsgWaitForMultipleObjectsFn RealMsgWaitForMultipleObjects = nullptr;
    MsgWaitForMultipleObjectsExFn RealMsgWaitForMultipleObjectsEx = nullptr;
    NtDelayExecutionFn RealNtDelayExecution = nullptr;

    WaitForSingleObjectFn TargetWaitForSingleObject = nullptr;
    WaitForSingleObjectExFn TargetWaitForSingleObjectEx = nullptr;
    WaitForMultipleObjectsFn TargetWaitForMultipleObjects = nullptr;
    WaitForMultipleObjectsExFn TargetWaitForMultipleObjectsEx = nullptr;
    MsgWaitForMultipleObjectsFn TargetMsgWaitForMultipleObjects = nullptr;
    MsgWaitForMultipleObjectsExFn TargetMsgWaitForMultipleObjectsEx = nullptr;
    NtDelayExecutionFn TargetNtDelayExecution = nullptr;

    static FARPROC Resolve(const wchar_t* moduleName, const char* functionName)
    {
        auto module = GetModuleHandleW(moduleName);
        if (!module)
            module = LoadLibraryW(moduleName);
        return module ? GetProcAddress(module, functionName) : nullptr;
    }

    static bool TryReadSpeed(double* speed)
    {
        if (!g_shared || !speed)
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
                *speed = currentSpeed > 0 ? currentSpeed : 1.0;
                return true;
            }
        }

        return false;
    }

    static double GetSpeed()
    {
        double speed = 1.0;
        TryReadSpeed(&speed);
        return speed > 0 ? speed : 1.0;
    }

    static DWORD ScaleDelay(DWORD ms)
    {
        if (ms == 0 || ms == INFINITE)
            return ms;

        const auto speed = GetSpeed();
        const auto scaledDouble = ms / speed;
        if (scaledDouble >= INFINITE)
            return INFINITE - 1;

        const auto scaled = static_cast<DWORD>(scaledDouble);
        return scaled < 1 ? 1 : scaled;
    }

    static LARGE_INTEGER ScaleRelativeNtDelay(const LARGE_INTEGER* delay)
    {
        LARGE_INTEGER scaled{};
        if (!delay)
            return scaled;

        scaled = *delay;
        if (scaled.QuadPart >= 0)
            return scaled; // Leave absolute NT timeouts alone.

        const auto speed = GetSpeed();
        auto units = static_cast<ULONGLONG>(-(scaled.QuadPart + 1)) + 1;
        const auto max = static_cast<ULONGLONG>((std::numeric_limits<LONGLONG>::max)());
        const auto scaledDouble = units / speed;

        units = scaledDouble >= static_cast<double>(max)
            ? max
            : static_cast<ULONGLONG>(scaledDouble);

        if (units == 0)
            units = 1;

        scaled.QuadPart = -static_cast<LONGLONG>(units);
        return scaled;
    }

    DWORD WINAPI HookWaitForSingleObject(HANDLE handle, DWORD milliseconds)
    {
        return RealWaitForSingleObject(handle, ScaleDelay(milliseconds));
    }

    DWORD WINAPI HookWaitForSingleObjectEx(HANDLE handle, DWORD milliseconds, BOOL alertable)
    {
        return RealWaitForSingleObjectEx(handle, ScaleDelay(milliseconds), alertable);
    }

    DWORD WINAPI HookWaitForMultipleObjects(DWORD count, const HANDLE* handles, BOOL waitAll, DWORD milliseconds)
    {
        return RealWaitForMultipleObjects(count, handles, waitAll, ScaleDelay(milliseconds));
    }

    DWORD WINAPI HookWaitForMultipleObjectsEx(DWORD count, const HANDLE* handles, BOOL waitAll, DWORD milliseconds, BOOL alertable)
    {
        return RealWaitForMultipleObjectsEx(count, handles, waitAll, ScaleDelay(milliseconds), alertable);
    }

    DWORD WINAPI HookMsgWaitForMultipleObjects(DWORD count, const HANDLE* handles, BOOL waitAll, DWORD milliseconds, DWORD wakeMask)
    {
        return RealMsgWaitForMultipleObjects(count, handles, waitAll, ScaleDelay(milliseconds), wakeMask);
    }

    DWORD WINAPI HookMsgWaitForMultipleObjectsEx(DWORD count, const HANDLE* handles, DWORD milliseconds, DWORD wakeMask, DWORD flags)
    {
        return RealMsgWaitForMultipleObjectsEx(count, handles, ScaleDelay(milliseconds), wakeMask, flags);
    }

    LONG NTAPI HookNtDelayExecution(BOOLEAN alertable, PLARGE_INTEGER delay)
    {
        if (!delay)
            return RealNtDelayExecution(alertable, delay);

        const auto scaled = ScaleRelativeNtDelay(delay);
        return RealNtDelayExecution(alertable, const_cast<PLARGE_INTEGER>(&scaled));
    }

    template <typename T>
    static void InstallHook(T target, T hook, T* real)
    {
        if (!target || !hook || !real)
            return;

        auto status = MH_CreateHook(
            reinterpret_cast<LPVOID>(target),
            reinterpret_cast<LPVOID>(hook),
            reinterpret_cast<LPVOID*>(real));

        if (status == MH_OK || status == MH_ERROR_ALREADY_CREATED)
            MH_EnableHook(reinterpret_cast<LPVOID>(target));
    }

    static void OpenSharedState()
    {
        auto mapping = CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            nullptr,
            PAGE_READWRITE,
            0,
            sizeof(SharedState),
            L"Local\\CefFlashBrowser.SpeedGear");

        if (!mapping)
            return;

        g_shared = reinterpret_cast<SharedState*>(
            MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, sizeof(SharedState)));
        CloseHandle(mapping);
    }

    static void InstallWaitHooks()
    {
        OpenSharedState();

        TargetWaitForSingleObject = reinterpret_cast<WaitForSingleObjectFn>(Resolve(L"kernel32.dll", "WaitForSingleObject"));
        TargetWaitForSingleObjectEx = reinterpret_cast<WaitForSingleObjectExFn>(Resolve(L"kernel32.dll", "WaitForSingleObjectEx"));
        TargetWaitForMultipleObjects = reinterpret_cast<WaitForMultipleObjectsFn>(Resolve(L"kernel32.dll", "WaitForMultipleObjects"));
        TargetWaitForMultipleObjectsEx = reinterpret_cast<WaitForMultipleObjectsExFn>(Resolve(L"kernel32.dll", "WaitForMultipleObjectsEx"));
        TargetMsgWaitForMultipleObjects = reinterpret_cast<MsgWaitForMultipleObjectsFn>(Resolve(L"user32.dll", "MsgWaitForMultipleObjects"));
        TargetMsgWaitForMultipleObjectsEx = reinterpret_cast<MsgWaitForMultipleObjectsExFn>(Resolve(L"user32.dll", "MsgWaitForMultipleObjectsEx"));
        TargetNtDelayExecution = reinterpret_cast<NtDelayExecutionFn>(Resolve(L"ntdll.dll", "NtDelayExecution"));

        const auto initStatus = MH_Initialize();
        if (initStatus != MH_OK && initStatus != MH_ERROR_ALREADY_INITIALIZED)
            return;

        InstallHook(TargetWaitForSingleObject, HookWaitForSingleObject, &RealWaitForSingleObject);
        InstallHook(TargetWaitForSingleObjectEx, HookWaitForSingleObjectEx, &RealWaitForSingleObjectEx);
        InstallHook(TargetWaitForMultipleObjects, HookWaitForMultipleObjects, &RealWaitForMultipleObjects);
        InstallHook(TargetWaitForMultipleObjectsEx, HookWaitForMultipleObjectsEx, &RealWaitForMultipleObjectsEx);
        InstallHook(TargetMsgWaitForMultipleObjects, HookMsgWaitForMultipleObjects, &RealMsgWaitForMultipleObjects);
        InstallHook(TargetMsgWaitForMultipleObjectsEx, HookMsgWaitForMultipleObjectsEx, &RealMsgWaitForMultipleObjectsEx);
        InstallHook(TargetNtDelayExecution, HookNtDelayExecution, &RealNtDelayExecution);
    }

    DWORD WINAPI WaitHookWorker(LPVOID)
    {
        // Let the main SpeedGear worker initialize shared state and its primary hooks first.
        Sleep(10);
        InstallWaitHooks();
        return 0;
    }

    struct AutoStartWaitHooks
    {
        AutoStartWaitHooks()
        {
            auto thread = CreateThread(nullptr, 0, WaitHookWorker, nullptr, 0, nullptr);
            if (thread)
                CloseHandle(thread);
        }
    };

    AutoStartWaitHooks g_autoStartWaitHooks;
}
