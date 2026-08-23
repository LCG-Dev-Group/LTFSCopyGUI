#include "NativeLtfsInternal.h"

#include <winsvc.h>

namespace
{
    constexpr wchar_t FuseServiceName[] = L"fuse4winsvc";
    constexpr DWORD ServiceWaitTimeoutMs = 30000;

    DWORD CaptureFailure()
    {
        DWORD error = ::GetLastError();
        return error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error;
    }

    class ServiceHandle
    {
    public:
        explicit ServiceHandle(SC_HANDLE handle = nullptr)
            : _handle(handle)
        {
        }

        ~ServiceHandle()
        {
            if (_handle != nullptr)
            {
                BOOL closed = ::CloseServiceHandle(_handle);
                DWORD error = closed ? ERROR_SUCCESS : CaptureFailure();
                (void)error;
                _handle = nullptr;
            }
        }

        SC_HANDLE get() const
        {
            return _handle;
        }

    private:
        SC_HANDLE _handle;
    };

    bool QueryServiceState(SC_HANDLE service,
                           DWORD desiredState,
                           DWORD& error)
    {
        const DWORD intervalMs = 100;
        const DWORD maxPolls = ServiceWaitTimeoutMs / intervalMs;
        for (DWORD poll = 0; poll <= maxPolls; ++poll)
        {
            SERVICE_STATUS_PROCESS status{};
            DWORD bytesNeeded = 0;
            BOOL queried = ::QueryServiceStatusEx(
                service,
                SC_STATUS_PROCESS_INFO,
                reinterpret_cast<LPBYTE>(&status),
                sizeof(status),
                &bytesNeeded);
            if (!queried)
            {
                error = CaptureFailure();
                return false;
            }

            if (status.dwCurrentState == desiredState)
            {
                return true;
            }

            if (status.dwCurrentState != SERVICE_START_PENDING &&
                status.dwCurrentState != SERVICE_STOP_PENDING)
            {
                error = ERROR_SERVICE_NOT_ACTIVE;
                return false;
            }

            if (poll < maxPolls)
            {
                ::Sleep(intervalMs);
            }
        }

        error = ERROR_TIMEOUT;
        return false;
    }

}

namespace LTFSCopyGUI
{
namespace Native
{
namespace Implementation
{
    bool StartFuseService(DWORD& error)
    {
        error = ERROR_SUCCESS;
        ServiceHandle manager(::OpenSCManagerW(
            nullptr,
            nullptr,
            SC_MANAGER_ALL_ACCESS));
        if (manager.get() == nullptr)
        {
            error = CaptureFailure();
            return false;
        }

        ServiceHandle service(::OpenServiceW(
            manager.get(),
            FuseServiceName,
            SERVICE_ALL_ACCESS));
        if (service.get() == nullptr)
        {
            error = CaptureFailure();
            return false;
        }

        DWORD bytesNeeded = 0;
        BOOL queried = ::QueryServiceConfigW(
            service.get(),
            nullptr,
            0,
            &bytesNeeded);
        DWORD queryError = queried ? ERROR_SUCCESS : ::GetLastError();
        if (queried || queryError != ERROR_INSUFFICIENT_BUFFER || bytesNeeded == 0)
        {
            error = queried ? ERROR_INVALID_DATA : queryError;
            if (error == ERROR_SUCCESS)
            {
                error = ERROR_GEN_FAILURE;
            }
            return false;
        }

        std::vector<BYTE> configBuffer(bytesNeeded);
        LPQUERY_SERVICE_CONFIGW config =
            reinterpret_cast<LPQUERY_SERVICE_CONFIGW>(configBuffer.data());
        queried = ::QueryServiceConfigW(
            service.get(),
            config,
            bytesNeeded,
            &bytesNeeded);
        if (!queried)
        {
            error = CaptureFailure();
            return false;
        }

        if (config->dwStartType != SERVICE_AUTO_START)
        {
            BOOL changed = ::ChangeServiceConfigW(
                service.get(),
                SERVICE_NO_CHANGE,
                SERVICE_AUTO_START,
                SERVICE_NO_CHANGE,
                nullptr,
                nullptr,
                nullptr,
                nullptr,
                nullptr,
                nullptr,
                nullptr);
            if (!changed)
            {
                error = CaptureFailure();
                return false;
            }
        }

        SERVICE_STATUS_PROCESS status{};
        queried = ::QueryServiceStatusEx(
            service.get(),
            SC_STATUS_PROCESS_INFO,
            reinterpret_cast<LPBYTE>(&status),
            sizeof(status),
            &bytesNeeded);
        if (!queried)
        {
            error = CaptureFailure();
            return false;
        }

        if (status.dwCurrentState == SERVICE_RUNNING)
        {
            return true;
        }

        if (status.dwCurrentState != SERVICE_STOPPED)
        {
            error = ERROR_SERVICE_NOT_ACTIVE;
            return false;
        }

        BOOL started = ::StartServiceW(service.get(), 0, nullptr);
        if (!started)
        {
            DWORD startError = ::GetLastError();
            if (startError != ERROR_SERVICE_ALREADY_RUNNING)
            {
                error = startError == ERROR_SUCCESS ? ERROR_GEN_FAILURE : startError;
                return false;
            }
        }

        return QueryServiceState(service.get(), SERVICE_RUNNING, error);
    }

    bool StopFuseService(DWORD& error)
    {
        error = ERROR_SUCCESS;
        ServiceHandle manager(::OpenSCManagerW(
            nullptr,
            nullptr,
            SC_MANAGER_ALL_ACCESS));
        if (manager.get() == nullptr)
        {
            error = CaptureFailure();
            return false;
        }

        ServiceHandle service(::OpenServiceW(
            manager.get(),
            FuseServiceName,
            SERVICE_ALL_ACCESS));
        if (service.get() == nullptr)
        {
            error = CaptureFailure();
            return false;
        }

        SERVICE_STATUS_PROCESS status{};
        DWORD bytesNeeded = 0;
        BOOL queried = ::QueryServiceStatusEx(
            service.get(),
            SC_STATUS_PROCESS_INFO,
            reinterpret_cast<LPBYTE>(&status),
            sizeof(status),
            &bytesNeeded);
        if (!queried)
        {
            error = CaptureFailure();
            return false;
        }

        if (status.dwCurrentState == SERVICE_STOPPED)
        {
            return true;
        }

        if (status.dwCurrentState != SERVICE_RUNNING)
        {
            error = ERROR_SERVICE_NOT_ACTIVE;
            return false;
        }

        SERVICE_STATUS serviceStatus{};
        BOOL stopped = ::ControlService(
            service.get(),
            SERVICE_CONTROL_STOP,
            &serviceStatus);
        if (!stopped)
        {
            error = CaptureFailure();
            return false;
        }

        return QueryServiceState(service.get(), SERVICE_STOPPED, error);
    }
}
}
}
