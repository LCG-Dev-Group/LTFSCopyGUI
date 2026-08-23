#include "NativeLtfsCommands.h"
#include "NativeLtfsInternal.h"

#include <algorithm>
#include <cwctype>
#include <string>
#include <vector>

using namespace LTFSCopyGUI::Native::Implementation;
using namespace LTFSCopyGUI::Native;

namespace
{
    NativeTextResult^ MakeTextResult(bool succeeded,
                                      DWORD error,
                                      const std::wstring& text)
    {
        if (succeeded)
        {
            error = ERROR_SUCCESS;
        }
        else if (error == ERROR_SUCCESS)
        {
            error = ERROR_GEN_FAILURE;
        }

        return gcnew NativeTextResult(
            succeeded,
            static_cast<Int32>(error),
            ToManagedString(text));
    }

    bool ParseDriveLetter(String^ value, wchar_t& driveLetter, DWORD& error)
    {
        const std::wstring text = ToWideString(value);
        if (text.size() != 1)
        {
            error = ERROR_INVALID_PARAMETER;
            return false;
        }

        driveLetter = static_cast<wchar_t>(std::towupper(text[0]));
        if (driveLetter < L'D' || driveLetter > L'Z')
        {
            error = ERROR_INVALID_DRIVE;
            return false;
        }

        error = ERROR_SUCCESS;
        return true;
    }

    NativeTextResult^ Failure(DWORD error, const std::wstring& message)
    {
        return MakeTextResult(false, error, message);
    }

    NativeTextResult^ MappingFailure(DWORD error)
    {
        return Failure(error, L"Failed to get mappings from registry.\r\n");
    }

    const Implementation::TapeDriveInfo* FindTapeDrive(
        const std::vector<Implementation::TapeDriveInfo>& drives,
        DWORD deviceNumber)
    {
        for (std::vector<Implementation::TapeDriveInfo>::const_iterator iterator =
                 drives.begin();
             iterator != drives.end();
             ++iterator)
        {
            if (iterator->DeviceNumber == deviceNumber)
            {
                return &(*iterator);
            }
        }

        return nullptr;
    }

    bool ReadMapping(wchar_t driveLetter,
                     std::wstring& deviceName,
                     DWORD& error)
    {
        bool exists = false;
        if (!QueryMapping(
                driveLetter,
                &deviceName,
                nullptr,
                exists,
                error))
        {
            return false;
        }

        if (!exists)
        {
            error = ERROR_FILE_NOT_FOUND;
            return false;
        }

        return true;
    }
}

namespace LTFSCopyGUI
{
namespace Native
{
    NativeTextResult^ NativeLtfsCommands::GetDriveMappings()
    {
        std::wstring output;
        for (wchar_t driveLetter = L'D'; driveLetter <= L'Z'; ++driveLetter)
        {
            std::wstring deviceName;
            std::wstring serialNumber;
            DWORD error = ERROR_SUCCESS;
            bool exists = false;
            if (!Implementation::QueryMapping(
                    driveLetter,
                    &deviceName,
                    &serialNumber,
                    exists,
                    error))
            {
                return MappingFailure(error);
            }

            if (exists)
            {
                output += driveLetter;
                output += L"|";
                output += deviceName;
                output += L"|";
                output += serialNumber;
                output += L"\r\n";
            }
        }

        return MakeTextResult(true, ERROR_SUCCESS, output);
    }

    NativeTextResult^ NativeLtfsCommands::StartLtfsService()
    {
        DWORD error = ERROR_SUCCESS;
        if (!Implementation::StartFuseService(error))
        {
            return Failure(error, L"Failed to start service.");
        }

        return MakeTextResult(true, ERROR_SUCCESS, std::wstring());
    }

    NativeTextResult^ NativeLtfsCommands::StopLtfsService()
    {
        DWORD error = ERROR_SUCCESS;
        if (!Implementation::StopFuseService(error))
        {
            return Failure(error, L"Failed to stop service.");
        }

        return MakeTextResult(true, ERROR_SUCCESS, std::wstring());
    }

    NativeTextResult^ NativeLtfsCommands::MapTapeDrive(String^ driveLetterValue,
                                                       String^ tapeDriveValue,
                                                       Byte tapeIndex,
                                                       String^ logDirectoryValue,
                                                       String^ workDirectoryValue,
                                                       bool showOffline)
    {
        wchar_t driveLetter = 0;
        DWORD error = ERROR_SUCCESS;
        if (!ParseDriveLetter(driveLetterValue, driveLetter, error))
        {
            return Failure(error, L"Invalid drive letter.");
        }

        const std::wstring tapeDrive = ToWideString(tapeDriveValue);
        const std::wstring logDirectory = ToWideString(logDirectoryValue);
        const std::wstring workDirectory = ToWideString(workDirectoryValue);
        if (tapeDrive.empty() || logDirectory.empty() || workDirectory.empty())
        {
            return Failure(ERROR_INVALID_PARAMETER, L"Invalid tape mapping parameters.");
        }

        if (Implementation::PollFileSystem(driveLetter, error))
        {
            return Failure(
                ERROR_ALREADY_EXISTS,
                (std::wstring(L"Drive letter ") + driveLetter +
                 L": already in use.\r\n").c_str());
        }

        std::vector<Implementation::TapeDriveInfo> drives;
        if (!Implementation::EnumerateTapeDrives(drives, error))
        {
            return Failure(error, L"Failed to enumerate tape drives.\r\n");
        }

        const Implementation::TapeDriveInfo* drive =
            FindTapeDrive(drives, static_cast<DWORD>(tapeIndex));
        if (drive == nullptr)
        {
            return MakeTextResult(
                false,
                ERROR_FILE_NOT_FOUND,
                L"Drive " + tapeDrive + L" not found.\r\n");
        }

        bool mappingExists = false;
        if (!Implementation::QueryMapping(
                driveLetter,
                nullptr,
                nullptr,
                mappingExists,
                error))
        {
            return MappingFailure(error);
        }
        if (mappingExists)
        {
            return Failure(
                ERROR_ALREADY_EXISTS,
                (std::wstring(L"Mapping for ") + driveLetter +
                 L": already exists.\r\n").c_str());
        }

        if (!Implementation::CreateMapping(
                driveLetter,
                tapeDrive,
                drive->SerialNumber,
                logDirectory,
                workDirectory,
                showOffline,
                error))
        {
            return Failure(error, L"Failed to create registry entries.\r\n");
        }

        if (!Implementation::StopFuseService(error))
        {
            return Failure(error, L"Failed to stop LTFS service.\r\n");
        }
        if (!Implementation::StartFuseService(error))
        {
            return Failure(error, L"Failed to start LTFS service.\r\n");
        }

        return MakeTextResult(true, ERROR_SUCCESS, std::wstring());
    }

    NativeTextResult^ NativeLtfsCommands::UnmapTapeDrive(String^ driveLetterValue)
    {
        wchar_t driveLetter = 0;
        DWORD error = ERROR_SUCCESS;
        if (!ParseDriveLetter(driveLetterValue, driveLetter, error))
        {
            return Failure(error, L"Invalid drive letter.");
        }

        BYTE mappingCount = 0;
        if (!Implementation::GetMappingCount(mappingCount, error))
        {
            return Failure(error, L"Failed to get mappings from registry.\r\n");
        }
        if (mappingCount == 0)
        {
            return Failure(ERROR_FILE_NOT_FOUND, L"No drives currently mapped.\r\n");
        }

        if (!Implementation::RemoveMapping(driveLetter, error))
        {
            return Failure(error, L"Failed to remove mapping from registry.\r\n");
        }

        --mappingCount;
        if (!Implementation::StopFuseService(error))
        {
            return Failure(error, L"Failed to stop LTFS service.\r\n");
        }
        if (mappingCount > 0 && !Implementation::StartFuseService(error))
        {
            return Failure(error, L"Failed to start LTFS service.\r\n");
        }

        return MakeTextResult(true, ERROR_SUCCESS, std::wstring());
    }

    NativeTextResult^ NativeLtfsCommands::RemapTapeDrives()
    {
        DWORD error = ERROR_SUCCESS;
        std::vector<Implementation::TapeDriveInfo> drives;
        if (!Implementation::EnumerateTapeDrives(drives, error))
        {
            return Failure(error, L"Failed to enumerate tape drives.\r\n");
        }
        if (drives.empty())
        {
            if (!Implementation::StopFuseService(error))
            {
                return Failure(error, L"No tape drives found.\r\n");
            }
            return Failure(ERROR_FILE_NOT_FOUND, L"No tape drives found.\r\n");
        }

        BYTE changesMade = 0;
        bool success = true;
        std::wstring output;
        for (const Implementation::TapeDriveInfo& drive : drives)
        {
            const std::wstring newDeviceName =
                L"TAPE" + std::to_wstring(drive.DeviceNumber);
            for (wchar_t driveLetter = L'D'; driveLetter <= L'Z'; ++driveLetter)
            {
                std::wstring registeredDeviceName;
                std::wstring registeredSerialNumber;
                bool exists = false;
                DWORD mappingError = ERROR_SUCCESS;
                if (!Implementation::QueryMapping(
                        driveLetter,
                        &registeredDeviceName,
                        &registeredSerialNumber,
                        exists,
                        mappingError))
                {
                    return MappingFailure(mappingError);
                }

                if (!exists ||
                    registeredSerialNumber != drive.SerialNumber ||
                    registeredDeviceName == newDeviceName)
                {
                    continue;
                }

                if (!Implementation::UpdateMapping(
                        driveLetter,
                        newDeviceName,
                        mappingError))
                {
                    success = false;
                    error = mappingError;
                    output += L"Failed to update existing mapping for ";
                    output += driveLetter;
                    output += L":\r\n";
                    continue;
                }

                ++changesMade;
                output += driveLetter;
                output += L": ";
                output += registeredDeviceName;
                output += L" [";
                output += registeredSerialNumber;
                output += L"] -> ";
                output += newDeviceName;
                output += L"\r\n";
            }
        }

        output += std::to_wstring(changesMade);
        output += L" mapping(s) updated.\r\n";

        if (success)
        {
            if (changesMade > 0 && !Implementation::StopFuseService(error))
            {
                output += L"Failed to stop LTFS service.\r\n";
                success = false;
            }
            if (success && !Implementation::StartFuseService(error))
            {
                output += L"Failed to start LTFS service.\r\n";
                success = false;
            }
        }

        return MakeTextResult(success, error, output);
    }

    NativeTextResult^ NativeLtfsCommands::LoadTapeDrive(String^ driveLetterValue,
                                                         bool mount)
    {
        wchar_t driveLetter = 0;
        DWORD error = ERROR_SUCCESS;
        if (!ParseDriveLetter(driveLetterValue, driveLetter, error))
        {
            return Failure(error, L"Invalid drive letter.");
        }

        std::wstring deviceName;
        if (!ReadMapping(driveLetter, deviceName, error))
        {
            return Failure(
                error,
                (std::wstring(L"Mapping for ") + driveLetter +
                 L": does not exist.\r\n").c_str());
        }

        if (!Implementation::LoadTape(deviceName, error))
        {
            return Failure(error, L"Failed to load tape.\r\n");
        }
        if (mount && !Implementation::PollFileSystem(driveLetter, error))
        {
            return Failure(error, L"Cannot start file system. LTFS not running.\r\n");
        }

        return MakeTextResult(true, ERROR_SUCCESS, std::wstring());
    }

    NativeTextResult^ NativeLtfsCommands::EjectTapeDrive(String^ driveLetterValue)
    {
        wchar_t driveLetter = 0;
        DWORD error = ERROR_SUCCESS;
        if (!ParseDriveLetter(driveLetterValue, driveLetter, error))
        {
            return Failure(error, L"Invalid drive letter.");
        }

        std::wstring deviceName;
        if (!ReadMapping(driveLetter, deviceName, error))
        {
            return Failure(
                error,
                (std::wstring(L"Mapping for ") + driveLetter +
                 L": does not exist.\r\n").c_str());
        }

        if (!Implementation::EjectTape(deviceName, error))
        {
            return Failure(
                error,
                L"Failed to eject tape. Ensure no files are open on the target volume.\r\n");
        }
        if (!Implementation::PollFileSystem(driveLetter, error))
        {
            return Failure(error, L"Failed to refresh the mapped file system.\r\n");
        }

        return MakeTextResult(true, ERROR_SUCCESS, std::wstring());
    }

    NativeTextResult^ NativeLtfsCommands::MountTapeDrive(String^ driveLetterValue)
    {
        wchar_t driveLetter = 0;
        DWORD error = ERROR_SUCCESS;
        if (!ParseDriveLetter(driveLetterValue, driveLetter, error))
        {
            return Failure(error, L"Invalid drive letter.");
        }

        if (!Implementation::PollFileSystem(driveLetter, error))
        {
            return Failure(error, L"Cannot start file system. LTFS not running.\r\n");
        }

        return MakeTextResult(true, ERROR_SUCCESS, std::wstring());
    }

    NativeTextResult^ NativeLtfsCommands::CheckTapeMedia(String^ driveLetterValue)
    {
        wchar_t driveLetter = 0;
        DWORD error = ERROR_SUCCESS;
        if (!ParseDriveLetter(driveLetterValue, driveLetter, error))
        {
            return Failure(error, L"Invalid drive letter.");
        }

        std::wstring deviceName;
        if (!ReadMapping(driveLetter, deviceName, error))
        {
            return Failure(
                error,
                (std::wstring(L"Mapping for ") + driveLetter +
                 L": does not exist.\r\n").c_str());
        }

        std::wstring mediaDescription;
        if (!Implementation::CheckTapeMedia(deviceName, mediaDescription, error))
        {
            return Failure(error, L"Media check failed.\r\n");
        }

        return MakeTextResult(
            true,
            ERROR_SUCCESS,
            deviceName + L": " + mediaDescription + L"\r\n");
    }
}
}
