#pragma once

#include "NativeMethods.h"

#include <string>
#include <vector>
#include <windows.h>

namespace LTFSCopyGUI
{
namespace Native
{
namespace Implementation
{
    struct TapeDriveInfo
    {
        DWORD DeviceNumber = 0;
        std::wstring SerialNumber;
        std::wstring VendorId;
        std::wstring ProductId;
    };

    std::wstring ToWideString(String^ value);
    String^ ToManagedString(const std::wstring& value);

    bool QueryMapping(wchar_t driveLetter,
                      std::wstring* deviceName,
                      std::wstring* serialNumber,
                      bool& exists,
                      DWORD& error);

    bool CreateMapping(wchar_t driveLetter,
                       const std::wstring& tapeDrive,
                       const std::wstring& serialNumber,
                       const std::wstring& logDirectory,
                       const std::wstring& workDirectory,
                       bool showOffline,
                       DWORD& error);

    bool UpdateMapping(wchar_t driveLetter,
                       const std::wstring& newDeviceName,
                       DWORD& error);

    bool RemoveMapping(wchar_t driveLetter, DWORD& error);
    bool GetMappingCount(BYTE& count, DWORD& error);

    bool StartFuseService(DWORD& error);
    bool StopFuseService(DWORD& error);

    bool EnumerateTapeDrives(std::vector<TapeDriveInfo>& drives, DWORD& error);
    bool PollFileSystem(wchar_t driveLetter, DWORD& error);
    bool LoadTape(const std::wstring& tapeDrive, DWORD& error);
    bool EjectTape(const std::wstring& tapeDrive, DWORD& error);
    bool CheckTapeMedia(const std::wstring& tapeDrive,
                        std::wstring& mediaDescription,
                        DWORD& error);
}
}
}
