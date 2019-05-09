#include "common.h" // Required for pre-compiled header

#ifdef FEATURE_AUTO_TRACE

#ifdef FEATURE_PAL
#error auto trace is not supported for cross plat
#endif

#include <windows.h>

HANDLE auto_trace_event;

void auto_trace_init()
{
    auto_trace_event = CreateEventW(
        /* lpEventAttributes = */ NULL,
        /* bManualReset      = */ FALSE,
        /* bInitialState     = */ FALSE,
        /* lpName            = */ nullptr
    );
}

void auto_trace_launch()
{
    DWORD currentProcessId = GetCurrentProcessId();
    STARTUPINFO si;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(STARTUPINFO);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    
    PROCESS_INFORMATION result;
    const wchar_t* commandFormat = L"C:\\Windows\\System32\\cmd.exe /c run.cmd collect --providers Microsoft-Windows-DotNETRuntime:FFFFFFFFFFFFFFBF -p %d";
    size_t len = wcslen(commandFormat) + 10 + 1;
    wchar_t* command = new wchar_t[len];
    wsprintf(command, commandFormat, currentProcessId);

    BOOL code = CreateProcessW(
        /* lpApplicationName    = */ nullptr,
        /* lpCommandLine        = */ command,
        /* lpCommandLine        = */ nullptr,
        /* lpThreadAttributes   = */ nullptr,
        /* bInheritHandles      = */ false,
        /* dwCreationFlags      = */ CREATE_NEW_CONSOLE,
        /* lpEnvironment        = */ nullptr,
        /* lpCurrentDirectory   = */ L"C:\\Dev\\diagnostics\\src\\Tools\\dotnet-trace",
        /* lpStartupInfo        = */ &si,
        /* lpProcessInformation = */ &result
    );
    delete[] command;
}

void auto_trace_wait()
{
    WaitForSingleObject(auto_trace_event, INFINITE);
}

void auto_trace_signal()
{
    #ifdef SetEvent
    #undef SetEvent
    #endif
    SetEvent(auto_trace_event);    
}

#endif // FEATURE_AUTO_TRACE