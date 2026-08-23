#include "NativeLtfsInternal.h"

#include <vcclr.h>

#include <limits>

namespace
{
    constexpr wchar_t MappingRoot[] = L"SOFTWARE\\HPE\\LTFS\\Mappings";
    constexpr wchar_t LtfsRoot[] = L"SOFTWARE\\HPE\\LTFS";

    std::wstring MappingKey(wchar_t driveLetter)
    {
        return std::wstring(MappingRoot) + L"\\" + driveLetter;
    }

    bool CloseRegistryKey(HKEY key, DWORD& error)
    {
        if (key == nullptr)
        {
            return true;
        }

        LSTATUS status = ::RegCloseKey(key);
        if (status != ERROR_SUCCESS)
        {
            error = static_cast<DWORD>(status);
            return false;
        }

        return true;
    }

    bool ReadStringValue(HKEY key,
                         const wchar_t* valueName,
                         std::wstring& value,
                         DWORD& error)
    {
        DWORD type = 0;
        DWORD byteCount = 0;
        LSTATUS status = ::RegQueryValueExW(
            key,
            valueName,
            nullptr,
            &type,
            nullptr,
            &byteCount);
        if (status != ERROR_SUCCESS)
        {
            error = static_cast<DWORD>(status);
            return false;
        }

        if (type != REG_SZ && type != REG_EXPAND_SZ)
        {
            error = ERROR_DATATYPE_MISMATCH;
            return false;
        }

        std::vector<wchar_t> buffer(
            static_cast<size_t>(byteCount / sizeof(wchar_t)) + 1,
            L'\0');
        status = ::RegQueryValueExW(
            key,
            valueName,
            nullptr,
            &type,
            reinterpret_cast<LPBYTE>(buffer.data()),
            &byteCount);
        if (status != ERROR_SUCCESS)
        {
            error = static_cast<DWORD>(status);
            return false;
        }

        value.assign(buffer.data());
        return true;
    }

    bool WriteStringValue(HKEY key,
                          const wchar_t* valueName,
                          const std::wstring& value,
                          DWORD& error)
    {
        const size_t byteCount = (value.size() + 1) * sizeof(wchar_t);
        if (byteCount > (std::numeric_limits<DWORD>::max)())
        {
            error = ERROR_BUFFER_OVERFLOW;
            return false;
        }

        LSTATUS status = ::RegSetValueExW(
            key,
            valueName,
            0,
            REG_SZ,
            reinterpret_cast<const BYTE*>(value.c_str()),
            static_cast<DWORD>(byteCount));
        if (status != ERROR_SUCCESS)
        {
            error = static_cast<DWORD>(status);
            return false;
        }

        return true;
    }

    bool WriteDwordValue(HKEY key,
                         const wchar_t* valueName,
                         DWORD value,
                         DWORD& error)
    {
        LSTATUS status = ::RegSetValueExW(
            key,
            valueName,
            0,
            REG_DWORD,
            reinterpret_cast<const BYTE*>(&value),
            sizeof(value));
        if (status != ERROR_SUCCESS)
        {
            error = static_cast<DWORD>(status);
            return false;
        }

        return true;
    }

    bool GetInstallDirectory(std::wstring& installDirectory, DWORD& error)
    {
        HKEY key = nullptr;
        LSTATUS status = ::RegOpenKeyExW(
            HKEY_LOCAL_MACHINE,
            LtfsRoot,
            0,
            KEY_READ | KEY_WOW64_64KEY,
            &key);
        if (status != ERROR_SUCCESS)
        {
            error = static_cast<DWORD>(status);
            return false;
        }

        bool success = ReadStringValue(key, L"InstallDir", installDirectory, error);
        DWORD closeError = ERROR_SUCCESS;
        if (!CloseRegistryKey(key, closeError) && success)
        {
            error = closeError;
            success = false;
        }

        return success;
    }

    std::wstring ReplaceAll(std::wstring source,
                            const std::wstring& oldValue,
                            const std::wstring& newValue,
                            bool& replaced)
    {
        replaced = false;
        if (oldValue.empty())
        {
            return source;
        }

        size_t position = 0;
        while ((position = source.find(oldValue, position)) != std::wstring::npos)
        {
            source.replace(position, oldValue.size(), newValue);
            position += newValue.size();
            replaced = true;
        }

        return source;
    }
}

namespace LTFSCopyGUI
{
namespace Native
{
namespace Implementation
{
    std::wstring ToWideString(String^ value)
    {
        if (value == nullptr)
        {
            return std::wstring();
        }

        pin_ptr<const wchar_t> pinnedValue = PtrToStringChars(value);
        return std::wstring(pinnedValue);
    }

    String^ ToManagedString(const std::wstring& value)
    {
        return value.empty() ? String::Empty : gcnew String(value.c_str());
    }

    bool QueryMapping(wchar_t driveLetter,
                      std::wstring* deviceName,
                      std::wstring* serialNumber,
                      bool& exists,
                      DWORD& error)
    {
        exists = false;
        error = ERROR_SUCCESS;

        HKEY key = nullptr;
        const std::wstring keyName = MappingKey(driveLetter);
        LSTATUS status = ::RegOpenKeyExW(
            HKEY_LOCAL_MACHINE,
            keyName.c_str(),
            0,
            KEY_READ | KEY_WOW64_64KEY,
            &key);
        if (status == ERROR_FILE_NOT_FOUND)
        {
            return true;
        }
        if (status != ERROR_SUCCESS)
        {
            error = static_cast<DWORD>(status);
            return false;
        }

        exists = true;
        bool success = true;
        if (deviceName != nullptr)
        {
            success = ReadStringValue(key, L"DeviceName", *deviceName, error);
        }
        if (success && serialNumber != nullptr)
        {
            success = ReadStringValue(key, L"SerialNumber", *serialNumber, error);
        }

        DWORD closeError = ERROR_SUCCESS;
        if (!CloseRegistryKey(key, closeError) && success)
        {
            error = closeError;
            success = false;
        }

        return success;
    }

    bool CreateMapping(wchar_t driveLetter,
                       const std::wstring& tapeDrive,
                       const std::wstring& serialNumber,
                       const std::wstring& logDirectory,
                       const std::wstring& workDirectory,
                       bool showOffline,
                       DWORD& error)
    {
        const std::wstring keyName = MappingKey(driveLetter);
        HKEY key = nullptr;
        DWORD disposition = 0;
        LSTATUS status = ::RegCreateKeyExW(
            HKEY_LOCAL_MACHINE,
            keyName.c_str(),
            0,
            nullptr,
            0,
            KEY_READ | KEY_CREATE_SUB_KEY | KEY_SET_VALUE | KEY_WOW64_64KEY,
            nullptr,
            &key,
            &disposition);
        if (status != ERROR_SUCCESS)
        {
            error = static_cast<DWORD>(status);
            return false;
        }

        bool success = WriteStringValue(key, L"SerialNumber", serialNumber, error);
        if (success)
        {
            success = WriteStringValue(key, L"DeviceName", tapeDrive, error);
        }

        std::wstring installDirectory;
        if (success)
        {
            success = GetInstallDirectory(installDirectory, error);
        }

        if (success)
        {
            std::wstring commandLine = installDirectory;
            if (!commandLine.empty() && commandLine.back() != L'\\')
            {
                commandLine += L'\\';
            }
            commandLine += L"ltfs.exe ";
            commandLine += driveLetter;
            commandLine += L": -o devname=";
            commandLine += tapeDrive;
            commandLine += L" -d -o log_directory=";
            commandLine += logDirectory;
            commandLine += L" -o work_directory=";
            commandLine += workDirectory;
            if (showOffline)
            {
                commandLine += L" -o show_offline";
            }

            success = WriteStringValue(key, L"CommandLine", commandLine, error);
        }

        if (success)
        {
            std::wstring traceTarget = L"\\\\.\\pipe\\";
            traceTarget += driveLetter;
            success = WriteStringValue(key, L"TraceTarget", traceTarget, error);
        }

        if (success)
        {
            success = WriteDwordValue(key, L"TraceType", 0x00000101, error);
        }

        DWORD closeError = ERROR_SUCCESS;
        if (!CloseRegistryKey(key, closeError) && success)
        {
            error = closeError;
            success = false;
        }

        return success;
    }

    bool UpdateMapping(wchar_t driveLetter,
                       const std::wstring& newDeviceName,
                       DWORD& error)
    {
        const std::wstring keyName = MappingKey(driveLetter);
        HKEY key = nullptr;
        DWORD disposition = 0;
        LSTATUS status = ::RegCreateKeyExW(
            HKEY_LOCAL_MACHINE,
            keyName.c_str(),
            0,
            nullptr,
            0,
            KEY_READ | KEY_CREATE_SUB_KEY | KEY_SET_VALUE | KEY_WOW64_64KEY,
            nullptr,
            &key,
            &disposition);
        if (status != ERROR_SUCCESS)
        {
            error = static_cast<DWORD>(status);
            return false;
        }

        std::wstring oldDeviceName;
        std::wstring commandLine;
        bool success = ReadStringValue(key, L"DeviceName", oldDeviceName, error);
        if (success)
        {
            success = WriteStringValue(key, L"DeviceName", newDeviceName, error);
        }
        if (success)
        {
            success = ReadStringValue(key, L"CommandLine", commandLine, error);
        }
        if (success)
        {
            bool replaced = false;
            commandLine = ReplaceAll(
                commandLine,
                L"devname=" + oldDeviceName,
                L"devname=" + newDeviceName,
                replaced);
            if (!replaced)
            {
                error = ERROR_NOT_FOUND;
                success = false;
            }
            else
            {
                success = WriteStringValue(key, L"CommandLine", commandLine, error);
            }
        }

        DWORD closeError = ERROR_SUCCESS;
        if (!CloseRegistryKey(key, closeError) && success)
        {
            error = closeError;
            success = false;
        }

        return success;
    }

    bool RemoveMapping(wchar_t driveLetter, DWORD& error)
    {
        const std::wstring keyName = MappingKey(driveLetter);
        LSTATUS status = ::RegDeleteKeyExW(
            HKEY_LOCAL_MACHINE,
            keyName.c_str(),
            KEY_WOW64_64KEY,
            0);
        error = static_cast<DWORD>(status);
        return status == ERROR_SUCCESS;
    }

    bool GetMappingCount(BYTE& count, DWORD& error)
    {
        count = 0;
        error = ERROR_SUCCESS;
        for (wchar_t driveLetter = L'D'; driveLetter <= L'Z'; ++driveLetter)
        {
            const std::wstring keyName = MappingKey(driveLetter);
            HKEY key = nullptr;
            LSTATUS status = ::RegOpenKeyExW(
                HKEY_LOCAL_MACHINE,
                keyName.c_str(),
                0,
                KEY_READ | KEY_WOW64_64KEY,
                &key);
            if (status == ERROR_FILE_NOT_FOUND)
            {
                continue;
            }
            if (status != ERROR_SUCCESS)
            {
                error = static_cast<DWORD>(status);
                return false;
            }

            ++count;
            DWORD closeError = ERROR_SUCCESS;
            if (!CloseRegistryKey(key, closeError))
            {
                error = closeError;
                return false;
            }
        }

        return true;
    }
}
}
}
