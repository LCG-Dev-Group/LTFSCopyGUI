#include "NativeLtfsInternal.h"

#include <setupapi.h>
#include <winioctl.h>

#include <algorithm>
#include <array>
#include <string>
#include <vector>

namespace
{
    constexpr DWORD ScsiDataIn = 1;
    constexpr DWORD ScsiDataUnspecified = 0;
    const GUID TapeDeviceInterfaceGuid =
    {
        0x53f5630b,
        0xb6bf,
        0x11d0,
        {0x94, 0xf2, 0x00, 0xa0, 0xc9, 0x1e, 0xfb, 0x8b}
    };

    std::wstring TrimAscii(const BYTE* value, size_t length)
    {
        size_t first = 0;
        while (first < length && (value[first] == 0 || value[first] == ' '))
        {
            ++first;
        }

        size_t last = length;
        while (last > first && (value[last - 1] == 0 || value[last - 1] == ' '))
        {
            --last;
        }

        std::wstring result;
        result.reserve(last - first);
        for (size_t index = first; index < last; ++index)
        {
            result.push_back(static_cast<wchar_t>(value[index]));
        }
        return result;
    }

    bool QueryInterfacePath(HDEVINFO deviceInfoSet,
                            SP_DEVICE_INTERFACE_DATA& interfaceData,
                            std::wstring& path,
                            DWORD& error)
    {
        DWORD requiredSize = 0;
        BOOL queried = ::SetupDiGetDeviceInterfaceDetailW(
            deviceInfoSet,
            &interfaceData,
            nullptr,
            0,
            &requiredSize,
            nullptr);
        DWORD queryError = queried ? ERROR_SUCCESS : ::GetLastError();
        if (queried || queryError != ERROR_INSUFFICIENT_BUFFER || requiredSize == 0)
        {
            error = queried ? ERROR_INVALID_DATA : queryError;
            if (error == ERROR_SUCCESS)
            {
                error = ERROR_GEN_FAILURE;
            }
            return false;
        }

        std::vector<BYTE> buffer(requiredSize, 0);
        PSP_DEVICE_INTERFACE_DETAIL_DATA_W detail =
            reinterpret_cast<PSP_DEVICE_INTERFACE_DETAIL_DATA_W>(buffer.data());
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);

        queried = ::SetupDiGetDeviceInterfaceDetailW(
            deviceInfoSet,
            &interfaceData,
            detail,
            requiredSize,
            &requiredSize,
            nullptr);
        if (!queried)
        {
            error = ::GetLastError();
            if (error == ERROR_SUCCESS)
            {
                error = ERROR_GEN_FAILURE;
            }
            return false;
        }

        path.assign(detail->DevicePath);
        return true;
    }

    bool ExecuteNativeScsi(HANDLE handle,
                           const BYTE* cdb,
                           size_t cdbLength,
                           void* dataBuffer,
                           ULONG dataLength,
                           BYTE dataIn,
                           ULONG timeout,
                           DWORD& error,
                           std::vector<BYTE>* sense = nullptr)
    {
        if (cdb == nullptr || cdbLength == 0 || cdbLength > 16)
        {
            error = ERROR_INVALID_PARAMETER;
            return false;
        }

        array<Byte>^ managedCdb = gcnew array<Byte>(static_cast<int>(cdbLength));
        for (size_t index = 0; index < cdbLength; ++index)
        {
            managedCdb[static_cast<int>(index)] = cdb[index];
        }

        IntPtr managedBuffer = dataBuffer == nullptr
            ? IntPtr::Zero
            : IntPtr(dataBuffer);
        LTFSCopyGUI::Native::NativeScsiResult^ result =
            LTFSCopyGUI::Native::NativeMethods::ExecuteScsiPassThrough(
                IntPtr(handle),
                managedCdb,
                managedBuffer,
                dataLength,
                dataIn,
                timeout,
                0,
                0);
        if (sense != nullptr)
        {
            sense->clear();
            if (result->Sense != nullptr)
            {
                sense->reserve(result->Sense->Length);
                for (int index = 0; index < result->Sense->Length; ++index)
                {
                    sense->push_back(static_cast<BYTE>(result->Sense[index]));
                }
            }
        }

        error = static_cast<DWORD>(result->Win32Error);
        return result->Succeeded;
    }

    std::wstring TapeDevicePath(const std::wstring& tapeDrive)
    {
        if (tapeDrive.rfind(L"\\\\.\\", 0) == 0)
        {
            return tapeDrive;
        }

        return L"\\\\.\\" + tapeDrive;
    }

    bool OpenTape(const std::wstring& tapeDrive,
                  LTFSCopyGUI::Native::NativeHandleResult^& openResult,
                  DWORD& error)
    {
        openResult = LTFSCopyGUI::Native::NativeMethods::OpenFile(
            LTFSCopyGUI::Native::Implementation::ToManagedString(
                TapeDevicePath(tapeDrive)),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_DELETE | FILE_SHARE_READ | FILE_SHARE_WRITE,
            OPEN_EXISTING,
            0);
        if (!openResult->Succeeded)
        {
            error = static_cast<DWORD>(openResult->Win32Error);
            return false;
        }

        return true;
    }

    bool CloseTape(IntPtr handle, DWORD& error)
    {
        LTFSCopyGUI::Native::NativeCallResult^ closeResult =
            LTFSCopyGUI::Native::NativeMethods::CloseHandle(handle);
        if (!closeResult->Succeeded)
        {
            error = static_cast<DWORD>(closeResult->Win32Error);
            return false;
        }

        return true;
    }

    std::wstring MediaDescription(USHORT mediaType)
    {
        switch (mediaType)
        {
        case 0x005E: return L"LTO8 RW";
        case 0x015E: return L"LTO8 WORM";
        case 0x025E: return L"LTO8 RO";
        case 0x005D: return L"LTOM8 RW";
        case 0x015D: return L"LTOM8 WORM";
        case 0x025D: return L"LTOM8 RO";
        case 0x005C: return L"LTO7 RW";
        case 0x015C: return L"LTO7 WORM";
        case 0x025C: return L"LTO7 RO";
        case 0x005A: return L"LTO6 RW";
        case 0x015A: return L"LTO6 WORM";
        case 0x025A: return L"LTO6 RO";
        case 0x0058: return L"LTO5 RW";
        case 0x0158: return L"LTO5 WORM";
        case 0x0258: return L"LTO5 RO";
        case 0x0046: return L"LTO4 RW";
        case 0x0146: return L"LTO4 WORM";
        case 0x0246: return L"LTO4 RO";
        case 0x0044: return L"LTO3 RW";
        case 0x0144: return L"LTO3 WORM";
        case 0x0244: return L"LTO3 RO";
        default:
        {
            wchar_t buffer[64]{};
            _snwprintf_s(buffer,
                         _countof(buffer),
                         _TRUNCATE,
                         L"Unknown media type 0x%X",
                         mediaType);
            return buffer;
        }
        }
    }
}

namespace LTFSCopyGUI
{
namespace Native
{
namespace Implementation
{
    bool EnumerateTapeDrives(std::vector<TapeDriveInfo>& drives, DWORD& error)
    {
        drives.clear();
        error = ERROR_SUCCESS;

        HDEVINFO deviceInfoSet = ::SetupDiGetClassDevsW(
            &TapeDeviceInterfaceGuid,
            nullptr,
            nullptr,
            DIGCF_DEVICEINTERFACE | DIGCF_PRESENT);
        if (deviceInfoSet == INVALID_HANDLE_VALUE)
        {
            error = ::GetLastError();
            if (error == ERROR_SUCCESS)
            {
                error = ERROR_GEN_FAILURE;
            }
            return false;
        }

        bool success = true;
        for (DWORD index = 0;; ++index)
        {
            SP_DEVICE_INTERFACE_DATA interfaceData{};
            interfaceData.cbSize = sizeof(interfaceData);
            BOOL enumerated = ::SetupDiEnumDeviceInterfaces(
                deviceInfoSet,
                nullptr,
                &TapeDeviceInterfaceGuid,
                index,
                &interfaceData);
            if (!enumerated)
            {
                DWORD enumError = ::GetLastError();
                if (enumError == ERROR_NO_MORE_ITEMS)
                {
                    break;
                }

                error = enumError == ERROR_SUCCESS ? ERROR_GEN_FAILURE : enumError;
                success = false;
                break;
            }

            std::wstring devicePath;
            DWORD pathError = ERROR_SUCCESS;
            if (!QueryInterfacePath(deviceInfoSet, interfaceData, devicePath, pathError))
            {
                error = pathError;
                success = false;
                break;
            }

            NativeHandleResult^ openResult = NativeMethods::OpenFile(
                ToManagedString(devicePath),
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_DELETE | FILE_SHARE_READ | FILE_SHARE_WRITE,
                OPEN_EXISTING,
                0);
            if (!openResult->Succeeded)
            {
                // A present interface may still be inaccessible. Preserve the
                // behavior of the original helper and continue enumeration.
                continue;
            }

            HANDLE handle = reinterpret_cast<HANDLE>(openResult->Handle.ToPointer());
            STORAGE_DEVICE_NUMBER deviceNumber{};
            NativeCallResult^ numberResult = NativeMethods::DeviceIoControl(
                openResult->Handle,
                IOCTL_STORAGE_GET_DEVICE_NUMBER,
                IntPtr::Zero,
                0,
                IntPtr(&deviceNumber),
                sizeof(deviceNumber));
            if (!numberResult->Succeeded ||
                numberResult->BytesReturned < sizeof(deviceNumber))
            {
                NativeCallResult^ closeResult = NativeMethods::CloseHandle(openResult->Handle);
                (void)closeResult;
                continue;
            }

            std::array<BYTE, 128> inquiry{};
            const BYTE inquiryCdb[] = {0x12, 0x00, 0x00, 0x00, 0x80, 0x00};
            DWORD scsiError = ERROR_SUCCESS;
            if (!ExecuteNativeScsi(
                    handle,
                    inquiryCdb,
                    sizeof(inquiryCdb),
                    inquiry.data(),
                    static_cast<ULONG>(inquiry.size()),
                    ScsiDataIn,
                    3,
                    scsiError))
            {
                NativeCallResult^ closeResult = NativeMethods::CloseHandle(openResult->Handle);
                (void)closeResult;
                continue;
            }

            std::array<BYTE, 128> serialPage{};
            const BYTE serialCdb[] = {0x12, 0x01, 0x80, 0x00, 0x80, 0x00};
            if (!ExecuteNativeScsi(
                    handle,
                    serialCdb,
                    sizeof(serialCdb),
                    serialPage.data(),
                    static_cast<ULONG>(serialPage.size()),
                    ScsiDataIn,
                    3,
                    scsiError))
            {
                NativeCallResult^ closeResult = NativeMethods::CloseHandle(openResult->Handle);
                (void)closeResult;
                continue;
            }

            TapeDriveInfo info;
            info.DeviceNumber = deviceNumber.DeviceNumber;
            info.VendorId = TrimAscii(inquiry.data() + 8, 8);
            info.ProductId = TrimAscii(inquiry.data() + 16, 16);
            const size_t serialLength = std::min<size_t>(
                serialPage[3],
                serialPage.size() > 4 ? serialPage.size() - 4 : 0);
            info.SerialNumber = TrimAscii(serialPage.data() + 4, serialLength);

            NativeCallResult^ closeResult = NativeMethods::CloseHandle(openResult->Handle);
            if (!closeResult->Succeeded)
            {
                error = static_cast<DWORD>(closeResult->Win32Error);
                success = false;
                break;
            }

            drives.push_back(info);
        }

        BOOL destroyed = ::SetupDiDestroyDeviceInfoList(deviceInfoSet);
        DWORD destroyError = destroyed ? ERROR_SUCCESS : ::GetLastError();
        if (!destroyed && success)
        {
            error = destroyError == ERROR_SUCCESS ? ERROR_GEN_FAILURE : destroyError;
            success = false;
        }

        return success;
    }

    bool PollFileSystem(wchar_t driveLetter, DWORD& error)
    {
        std::wstring path = L"\\\\.\\";
        path += driveLetter;
        path += L":";

        NativeHandleResult^ openResult = NativeMethods::OpenFile(
            ToManagedString(path),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_DELETE | FILE_SHARE_READ | FILE_SHARE_WRITE,
            OPEN_EXISTING,
            0);
        if (!openResult->Succeeded)
        {
            error = static_cast<DWORD>(openResult->Win32Error);
            return false;
        }

        return CloseTape(openResult->Handle, error);
    }

    bool LoadTape(const std::wstring& tapeDrive, DWORD& error)
    {
        NativeHandleResult^ openResult = nullptr;
        if (!OpenTape(tapeDrive, openResult, error))
        {
            return false;
        }

        const BYTE cdb[] = {0x1B, 0x00, 0x00, 0x00, 0x01, 0x00};
        DWORD scsiError = ERROR_SUCCESS;
        bool success = ExecuteNativeScsi(
            reinterpret_cast<HANDLE>(openResult->Handle.ToPointer()),
            cdb,
            sizeof(cdb),
            nullptr,
            0,
            ScsiDataUnspecified,
            300,
            scsiError);
        error = scsiError;

        DWORD closeError = ERROR_SUCCESS;
        bool closed = CloseTape(openResult->Handle, closeError);
        if (success && !closed)
        {
            error = closeError;
            success = false;
        }

        return success;
    }

    bool EjectTape(const std::wstring& tapeDrive, DWORD& error)
    {
        NativeHandleResult^ openResult = nullptr;
        if (!OpenTape(tapeDrive, openResult, error))
        {
            return false;
        }

        bool success = true;
        DWORD operationError = ERROR_SUCCESS;
        NativeCallResult^ result = NativeMethods::DeviceIoControl(
            openResult->Handle,
            FSCTL_LOCK_VOLUME,
            IntPtr::Zero,
            0,
            IntPtr::Zero,
            0);
        if (!result->Succeeded)
        {
            success = false;
            operationError = static_cast<DWORD>(result->Win32Error);
        }

        if (success)
        {
            result = NativeMethods::DeviceIoControl(
                openResult->Handle,
                FSCTL_DISMOUNT_VOLUME,
                IntPtr::Zero,
                0,
                IntPtr::Zero,
                0);
            if (!result->Succeeded)
            {
                success = false;
                operationError = static_cast<DWORD>(result->Win32Error);
            }
        }

        if (success)
        {
            result = NativeMethods::DeviceIoControl(
                openResult->Handle,
                IOCTL_DISK_EJECT_MEDIA,
                IntPtr::Zero,
                0,
                IntPtr::Zero,
                0);
            if (!result->Succeeded)
            {
                success = false;
                operationError = static_cast<DWORD>(result->Win32Error);
            }
        }

        DWORD closeError = ERROR_SUCCESS;
        bool closed = CloseTape(openResult->Handle, closeError);
        if (!success)
        {
            error = operationError;
        }
        else if (!closed)
        {
            error = closeError;
            success = false;
        }

        return success;
    }

    bool CheckTapeMedia(const std::wstring& tapeDrive,
                        std::wstring& mediaDescription,
                        DWORD& error)
    {
        mediaDescription.clear();
        NativeHandleResult^ openResult = nullptr;
        if (!OpenTape(tapeDrive, openResult, error))
        {
            return false;
        }

        HANDLE handle = reinterpret_cast<HANDLE>(openResult->Handle.ToPointer());
        std::array<BYTE, 64> data{};
        const BYTE readPositionCdb[] =
            {0x34, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};
        std::vector<BYTE> sense;
        DWORD scsiError = ERROR_SUCCESS;
        bool success = ExecuteNativeScsi(
            handle,
            readPositionCdb,
            sizeof(readPositionCdb),
            data.data(),
            static_cast<ULONG>(data.size()),
            ScsiDataIn,
            300,
            scsiError,
            &sense);
        if (!success)
        {
            error = scsiError;
            DWORD closeError = ERROR_SUCCESS;
            CloseTape(openResult->Handle, closeError);
            return false;
        }

        if (sense.size() >= 14 &&
            ((sense[2] & 0x0F) == 0x02) &&
            sense[12] == 0x3A &&
            sense[13] == 0x00)
        {
            mediaDescription = L"No tape loaded";
            return CloseTape(openResult->Handle, error);
        }

        data.fill(0);
        const BYTE modeSenseCdb[] =
            {0x5A, 0x00, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00};
        success = ExecuteNativeScsi(
            handle,
            modeSenseCdb,
            sizeof(modeSenseCdb),
            data.data(),
            static_cast<ULONG>(data.size()),
            ScsiDataIn,
            300,
            scsiError);
        if (!success)
        {
            error = scsiError;
            DWORD closeError = ERROR_SUCCESS;
            CloseTape(openResult->Handle, closeError);
            return false;
        }

        USHORT mediaType = static_cast<USHORT>(data[8]) |
            static_cast<USHORT>((data[18] & 0x01) << 8);
        if ((mediaType & 0x100) == 0)
        {
            mediaType = static_cast<USHORT>(
                mediaType | static_cast<USHORT>((data[3] & 0x80) << 2));
        }
        mediaDescription = MediaDescription(mediaType);
        return CloseTape(openResult->Handle, error);
    }
}
}
}
