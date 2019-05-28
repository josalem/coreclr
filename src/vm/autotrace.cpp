#include "common.h" // Required for pre-compiled header

#ifdef FEATURE_AUTO_TRACE
#ifdef FEATURE_PAL
#include "pal.h"
#endif // FEATURE_PAL

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
#ifndef FEATURE_PAL
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
#endif
    
    PROCESS_INFORMATION result;

    #ifdef FEATURE_PAL
    const WCHAR* commandFormat = u"%hs/run.sh collect --providers Microsoft-Windows-DotNETRuntime:FFFFFFFFFFFFFFBF -p %d";
    const char* defaultTraceLocation = "/git/diagnostics/src/Tools/dotnet-trace/";
    #else
    const WCHAR* commandFormat = L"C:\\Windows\\System32\\cmd.exe /c %hs\\run.cmd collect --providers Microsoft-Windows-DotNETRuntime:FFFFFFFFFFFFFFBF -p %d";
    const char* defaultTraceLocation = "C:\\git\\diagnostics\\src\\Tools\\dotnet-trace\\";
    #endif

    const char *traceLoc = getenv("AUTO_TRACE_LOC");
    const char *dotnetTraceDirectory = traceLoc == NULL ? defaultTraceLocation : traceLoc;
    size_t len = _snwprintf(NULL, 0, commandFormat, traceLoc, dotnetTraceDirectory, currentProcessId);
    WCHAR* command = new WCHAR[len];
    _snwprintf_s(command, len, _TRUNCATE, commandFormat, dotnetTraceDirectory, currentProcessId);
    MAKE_WIDEPTR_FROMUTF8(currentDirectory, dotnetTraceDirectory);

    BOOL code = CreateProcessW(
        /* lpApplicationName    = */ nullptr,
        /* lpCommandLine        = */ command,
        /* lpCommandLine        = */ nullptr,
        /* lpThreadAttributes   = */ nullptr,
        /* bInheritHandles      = */ false,
        /* dwCreationFlags      = */ CREATE_NEW_CONSOLE,
        /* lpEnvironment        = */ nullptr,
        /* lpCurrentDirectory   = */ currentDirectory,
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