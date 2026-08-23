#include "NativeMethods.h"

#include <ntddscsi.h>
#include <commctrl.h>
#include <shellapi.h>
#include <setupapi.h>
#include <shlwapi.h>
#include <vcclr.h>

#include <cstddef>
#include <vector>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "setupapi.lib")

using namespace System::ComponentModel;

namespace
{
	constexpr DWORD SenseBufferLength = 64;

	DWORD CaptureFailure(BOOL succeeded)
	{
		if (succeeded)
		{
			return ERROR_SUCCESS;
		}

		DWORD error = ::GetLastError();
		return error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error;
	}

	struct SCSI_PASS_THROUGH_DIRECT_WITH_SENSE
	{
		SCSI_PASS_THROUGH_DIRECT PassThrough;
		UCHAR Sense[SenseBufferLength];
	};

	String^ ReadDeviceStringProperty(HDEVINFO deviceInfoSet,
		SP_DEVINFO_DATA& deviceInfoData,
		DWORD property)
	{
		DWORD propertyType = 0;
		DWORD requiredSize = 0;
		BOOL queried = ::SetupDiGetDeviceRegistryPropertyW(
			deviceInfoSet,
			&deviceInfoData,
			property,
			&propertyType,
			nullptr,
			0,
			&requiredSize);
		DWORD error = queried ? ERROR_SUCCESS : ::GetLastError();
		if (queried)
		{
			return String::Empty;
		}

		if (error == ERROR_INVALID_DATA ||
			error == ERROR_FILE_NOT_FOUND ||
			error == ERROR_NOT_FOUND)
		{
			return String::Empty;
		}

		if (error != ERROR_INSUFFICIENT_BUFFER || requiredSize == 0)
		{
			throw gcnew Win32Exception(
				error,
				"SetupDiGetDeviceRegistryPropertyW failed to query the buffer size.");
		}

		std::vector<BYTE> buffer(requiredSize + sizeof(wchar_t), 0);
		queried = ::SetupDiGetDeviceRegistryPropertyW(
			deviceInfoSet,
			&deviceInfoData,
			property,
			&propertyType,
			buffer.data(),
			requiredSize,
			&requiredSize);
		if (!queried)
		{
			error = ::GetLastError();
			throw gcnew Win32Exception(
				error,
				"SetupDiGetDeviceRegistryPropertyW failed to read a device property.");
		}

		if (propertyType != REG_SZ && propertyType != REG_EXPAND_SZ)
		{
			return String::Empty;
		}

		const wchar_t* value = reinterpret_cast<const wchar_t*>(buffer.data());
		return gcnew String(value);
	}
}

namespace LTFSCopyGUI
{
	namespace Native
	{
		NativeCallResult::NativeCallResult(bool succeeded, Int32 win32Error, UInt32 bytesReturned)
			: _succeeded(succeeded),
			_win32Error(win32Error),
			_bytesReturned(bytesReturned)
		{}

		bool NativeCallResult::Succeeded::get()
		{
			return _succeeded;
		}

		Int32 NativeCallResult::Win32Error::get()
		{
			return _win32Error;
		}

		UInt32 NativeCallResult::BytesReturned::get()
		{
			return _bytesReturned;
		}

		String^ NativeCallResult::ErrorMessage::get()
		{
			if (_win32Error == ERROR_SUCCESS)
			{
				return "No Win32 error was reported.";
			}

			return (gcnew Win32Exception(_win32Error))->Message;
		}

		void NativeCallResult::ThrowIfFailed(String^ operation)
		{
			if (_succeeded)
			{
				return;
			}

			String^ message = String::IsNullOrWhiteSpace(operation)
				? "Native Win32 call failed."
				: operation;
			throw gcnew Win32Exception(_win32Error, message);
		}

		NativeHandleResult::NativeHandleResult(bool succeeded, IntPtr handle, Int32 win32Error)
			: NativeCallResult(succeeded, win32Error, 0),
			_handle(handle)
		{}

		IntPtr NativeHandleResult::Handle::get()
		{
			return _handle;
		}

		NativeScsiResult::NativeScsiResult(bool succeeded,
			Int32 win32Error,
			UInt32 bytesReturned,
			array<Byte>^ sense)
			: NativeCallResult(succeeded, win32Error, bytesReturned),
			_sense(sense == nullptr ? gcnew array<Byte>(0) : sense)
		{}

		array<Byte>^ NativeScsiResult::Sense::get()
		{
			return _sense;
		}

		NativeStorageDeviceNumberResult::NativeStorageDeviceNumberResult(bool succeeded,
			Int32 win32Error,
			UInt32 bytesReturned,
			Int32 deviceType,
			Int32 deviceNumber,
			Int32 partitionNumber)
			: NativeCallResult(succeeded, win32Error, bytesReturned),
			_deviceType(deviceType),
			_deviceNumber(deviceNumber),
			_partitionNumber(partitionNumber)
		{}

		Int32 NativeStorageDeviceNumberResult::DeviceType::get()
		{
			return _deviceType;
		}

		Int32 NativeStorageDeviceNumberResult::DeviceNumber::get()
		{
			return _deviceNumber;
		}

		Int32 NativeStorageDeviceNumberResult::PartitionNumber::get()
		{
			return _partitionNumber;
		}

		NativeRect::NativeRect(Int32 left, Int32 top, Int32 right, Int32 bottom)
			: _left(left),
			_top(top),
			_right(right),
			_bottom(bottom)
		{}

		Int32 NativeRect::Left::get()
		{
			return _left;
		}

		Int32 NativeRect::Top::get()
		{
			return _top;
		}

		Int32 NativeRect::Right::get()
		{
			return _right;
		}

		Int32 NativeRect::Bottom::get()
		{
			return _bottom;
		}

		NativeDevice::NativeDevice(bool present,
			String^ className,
			String^ pdoName,
			String^ name)
			: _present(present),
			_className(String::IsNullOrEmpty(className) ? "Unknown" : className),
			_pdoName(pdoName == nullptr ? String::Empty : pdoName),
			_name(name == nullptr ? String::Empty : name)
		{}

		bool NativeDevice::Present::get()
		{
			return _present;
		}

		String^ NativeDevice::ClassName::get()
		{
			return _className;
		}

		String^ NativeDevice::PDOName::get()
		{
			return _pdoName;
		}

		String^ NativeDevice::Name::get()
		{
			return _name;
		}

		NativeStringCompareResult::NativeStringCompareResult(bool succeeded,
			Int32 win32Error,
			Int32 comparison)
			: NativeCallResult(succeeded, win32Error, 0),
			_comparison(comparison)
		{}

		Int32 NativeStringCompareResult::Comparison::get()
		{
			return _comparison;
		}

		NativeTextResult::NativeTextResult(bool succeeded,
			Int32 win32Error,
			String^ text)
			: NativeCallResult(succeeded, win32Error, 0),
			_text(text == nullptr ? String::Empty : text)
		{}

		String^ NativeTextResult::Text::get()
		{
			return _text;
		}

		NativeHandleResult^ NativeMethods::OpenFile(String^ path,
			UInt32 desiredAccess,
			UInt32 shareMode,
			UInt32 creationDisposition,
			UInt32 flagsAndAttributes)
		{
			if (String::IsNullOrWhiteSpace(path))
			{
				return gcnew NativeHandleResult(false,
					IntPtr::Zero,
					ERROR_INVALID_PARAMETER);
			}

			pin_ptr<const wchar_t> pinnedPath = PtrToStringChars(path);
			HANDLE handle = ::CreateFileW(pinnedPath,
				desiredAccess,
				shareMode,
				nullptr,
				creationDisposition,
				flagsAndAttributes,
				nullptr);
			if (handle == INVALID_HANDLE_VALUE)
			{
				DWORD error = ::GetLastError();
				if (error == ERROR_SUCCESS)
				{
					error = ERROR_GEN_FAILURE;
				}
				return gcnew NativeHandleResult(false, IntPtr::Zero, error);
			}

			return gcnew NativeHandleResult(true,
				IntPtr(handle),
				ERROR_SUCCESS);
		}

		NativeCallResult^ NativeMethods::CloseHandle(IntPtr handle)
		{
			BOOL succeeded = ::CloseHandle(reinterpret_cast<HANDLE>(handle.ToPointer()));
			DWORD error = CaptureFailure(succeeded);
			return gcnew NativeCallResult(succeeded, error, 0);
		}

		NativeCallResult^ NativeMethods::DeviceIoControl(IntPtr handle,
			UInt32 controlCode,
			IntPtr inputBuffer,
			UInt32 inputBufferSize,
			IntPtr outputBuffer,
			UInt32 outputBufferSize)
		{
			DWORD bytesReturned = 0;
			BOOL succeeded = ::DeviceIoControl(
				reinterpret_cast<HANDLE>(handle.ToPointer()),
				controlCode,
				inputBuffer.ToPointer(),
				inputBufferSize,
				outputBuffer.ToPointer(),
				outputBufferSize,
				&bytesReturned,
				nullptr);
			DWORD error = CaptureFailure(succeeded);
			return gcnew NativeCallResult(succeeded, error, bytesReturned);
		}

		NativeScsiResult^ NativeMethods::ExecuteScsiPassThrough(IntPtr handle,
			array<Byte>^ cdb,
			IntPtr dataBuffer,
			UInt32 dataTransferLength,
			Byte dataIn,
			UInt32 timeoutValue,
			Byte targetId,
			Byte lun)
		{
			if (handle == IntPtr::Zero || handle == IntPtr(-1) ||
				cdb == nullptr || cdb->Length == 0 || cdb->Length > 16)
			{
				return gcnew NativeScsiResult(false,
					ERROR_INVALID_PARAMETER,
					0,
					gcnew array<Byte>(0));
			}

			SCSI_PASS_THROUGH_DIRECT_WITH_SENSE packet{};
			packet.PassThrough.Length = sizeof(SCSI_PASS_THROUGH_DIRECT);
			packet.PassThrough.CdbLength = static_cast<UCHAR>(cdb->Length);
			packet.PassThrough.DataIn = dataIn;
			packet.PassThrough.DataTransferLength = dataTransferLength;
			packet.PassThrough.TimeOutValue = timeoutValue;
			packet.PassThrough.DataBuffer = dataBuffer.ToPointer();
			packet.PassThrough.SenseInfoLength = SenseBufferLength;
			packet.PassThrough.SenseInfoOffset = static_cast<ULONG>(offsetof(
				SCSI_PASS_THROUGH_DIRECT_WITH_SENSE,
				Sense));
			packet.PassThrough.TargetId = targetId;
			packet.PassThrough.Lun = lun;

			for (int index = 0; index < cdb->Length; ++index)
			{
				packet.PassThrough.Cdb[index] = cdb[index];
			}

			DWORD bytesReturned = 0;
			BOOL succeeded = ::DeviceIoControl(
				reinterpret_cast<HANDLE>(handle.ToPointer()),
				IOCTL_SCSI_PASS_THROUGH_DIRECT,
				&packet,
				sizeof(packet),
				&packet,
				sizeof(packet),
				&bytesReturned,
				nullptr);
			DWORD error = CaptureFailure(succeeded);

			array<Byte>^ sense = gcnew array<Byte>(SenseBufferLength);
			for (DWORD index = 0; index < SenseBufferLength; ++index)
			{
				sense[index] = packet.Sense[index];
			}

			return gcnew NativeScsiResult(succeeded, error, bytesReturned, sense);
		}

		NativeStorageDeviceNumberResult^ NativeMethods::QueryStorageDeviceNumber(String^ path)
		{
			NativeHandleResult^ openResult = OpenFile(path,
				GENERIC_READ | GENERIC_WRITE,
				FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
				OPEN_EXISTING,
				0);
			if (!openResult->Succeeded)
			{
				return gcnew NativeStorageDeviceNumberResult(false,
					openResult->Win32Error,
					0,
					0,
					0,
					0);
			}

			STORAGE_DEVICE_NUMBER deviceNumber{};
			DWORD bytesReturned = 0;
			BOOL succeeded = ::DeviceIoControl(
				reinterpret_cast<HANDLE>(openResult->Handle.ToPointer()),
				IOCTL_STORAGE_GET_DEVICE_NUMBER,
				nullptr,
				0,
				&deviceNumber,
				sizeof(deviceNumber),
				&bytesReturned,
				nullptr);
			DWORD error = CaptureFailure(succeeded);

			NativeCallResult^ closeResult = CloseHandle(openResult->Handle);
			if (succeeded && !closeResult->Succeeded)
			{
				succeeded = FALSE;
				error = static_cast<DWORD>(closeResult->Win32Error);
			}

			if (succeeded && bytesReturned < sizeof(deviceNumber))
			{
				succeeded = FALSE;
				error = ERROR_INSUFFICIENT_BUFFER;
			}

			return gcnew NativeStorageDeviceNumberResult(
				succeeded,
				static_cast<Int32>(error),
				bytesReturned,
				deviceNumber.DeviceType,
				deviceNumber.DeviceNumber,
				deviceNumber.PartitionNumber);
		}

		array<NativeDevice^>^ NativeMethods::EnumerateDevices()
		{
			return EnumerateDevices(nullptr);
		}

		array<NativeDevice^>^ NativeMethods::EnumerateDevices(String^ enumerator)
		{
			const wchar_t* enumeratorValue = nullptr;
			if (!String::IsNullOrWhiteSpace(enumerator))
			{
				pin_ptr<const wchar_t> pinnedEnumerator = PtrToStringChars(enumerator);
				enumeratorValue = pinnedEnumerator;
			}

			HDEVINFO deviceInfoSet = ::SetupDiGetClassDevsW(
				nullptr,
				enumeratorValue,
				nullptr,
				DIGCF_ALLCLASSES | DIGCF_PRESENT);
			if (deviceInfoSet == INVALID_HANDLE_VALUE)
			{
				DWORD error = ::GetLastError();
				throw gcnew Win32Exception(error, "SetupDiGetClassDevsW failed.");
			}

			System::Collections::Generic::List<NativeDevice^>^ devices =
				gcnew System::Collections::Generic::List<NativeDevice^>(0);
			try
			{
				for (DWORD index = 0;; ++index)
				{
					SP_DEVINFO_DATA deviceInfoData{};
					deviceInfoData.cbSize = sizeof(deviceInfoData);
					BOOL enumerated = ::SetupDiEnumDeviceInfo(
						deviceInfoSet,
						index,
						&deviceInfoData);
					if (!enumerated)
					{
						DWORD error = ::GetLastError();
						if (error == ERROR_NO_MORE_ITEMS)
						{
							break;
						}

						throw gcnew Win32Exception(error, "SetupDiEnumDeviceInfo failed.");
					}

					String^ className = ReadDeviceStringProperty(
						deviceInfoSet,
						deviceInfoData,
						SPDRP_CLASS);
					String^ pdoName = ReadDeviceStringProperty(
						deviceInfoSet,
						deviceInfoData,
						SPDRP_PHYSICAL_DEVICE_OBJECT_NAME);
					String^ name = ReadDeviceStringProperty(
						deviceInfoSet,
						deviceInfoData,
						SPDRP_FRIENDLYNAME);
					if (String::IsNullOrWhiteSpace(name))
					{
						name = ReadDeviceStringProperty(
							deviceInfoSet,
							deviceInfoData,
							SPDRP_DEVICEDESC);
					}

					devices->Add(gcnew NativeDevice(true, className, pdoName, name));
				}
			}
			catch (...)
			{
				BOOL destroyed = ::SetupDiDestroyDeviceInfoList(deviceInfoSet);
				DWORD cleanupError = destroyed ? ERROR_SUCCESS : ::GetLastError();
				(void)cleanupError;
				throw;
			}

			BOOL destroyed = ::SetupDiDestroyDeviceInfoList(deviceInfoSet);
			DWORD error = destroyed ? ERROR_SUCCESS : ::GetLastError();
			if (!destroyed)
			{
				throw gcnew Win32Exception(error, "SetupDiDestroyDeviceInfoList failed.");
			}

			return devices->ToArray();
		}

		NativeCallResult^ NativeMethods::ChangeWindowMessageFilterEx(IntPtr windowHandle,
			UInt32 message,
			UInt32 action)
		{
			CHANGEFILTERSTRUCT changeFilter{};
			changeFilter.cbSize = sizeof(changeFilter);
			BOOL succeeded = ::ChangeWindowMessageFilterEx(
				static_cast<HWND>(windowHandle.ToPointer()),
				message,
				action,
				&changeFilter);
			DWORD error = CaptureFailure(succeeded);
			return gcnew NativeCallResult(succeeded, error, 0);
		}

		NativeCallResult^ NativeMethods::DragAcceptFiles(IntPtr windowHandle, bool accept)
		{
			if (windowHandle == IntPtr::Zero)
			{
				return gcnew NativeCallResult(false, ERROR_INVALID_HANDLE, 0);
			}

			::SetLastError(ERROR_SUCCESS);
			::DragAcceptFiles(static_cast<HWND>(windowHandle.ToPointer()),
				accept ? TRUE : FALSE);
			DWORD error = ::GetLastError();
			return gcnew NativeCallResult(error == ERROR_SUCCESS, error, 0);
		}

		array<String^>^ NativeMethods::GetDroppedFiles(IntPtr dropHandle)
		{
			HDROP handle = static_cast<HDROP>(dropHandle.ToPointer());
			UINT count = ::DragQueryFileW(handle, 0xFFFFFFFF, nullptr, 0);
			if (count == 0)
			{
				DWORD error = ::GetLastError();
				if (error == ERROR_SUCCESS)
				{
					error = ERROR_INVALID_DATA;
				}
				throw gcnew Win32Exception(error, "DragQueryFileW failed to enumerate dropped files.");
			}

			array<String^>^ files = gcnew array<String^>(count);
			for (UINT index = 0; index < count; ++index)
			{
				UINT length = ::DragQueryFileW(handle, index, nullptr, 0);
				if (length == 0)
				{
					DWORD error = ::GetLastError();
					if (error == ERROR_SUCCESS)
					{
						error = ERROR_INVALID_DATA;
					}
					throw gcnew Win32Exception(error, "DragQueryFileW failed to query a dropped file path.");
				}

				std::vector<wchar_t> pathBuffer(length + 1, L'\0');
				UINT copied = ::DragQueryFileW(handle,
					index,
					pathBuffer.data(),
					length + 1);
				if (copied == 0)
				{
					DWORD error = ::GetLastError();
					if (error == ERROR_SUCCESS)
					{
						error = ERROR_INVALID_DATA;
					}
					throw gcnew Win32Exception(error, "DragQueryFileW failed to read a dropped file path.");
				}

				files[index] = gcnew String(pathBuffer.data(), 0, static_cast<int>(copied));
			}

			return files;
		}

		NativeCallResult^ NativeMethods::DragFinish(IntPtr dropHandle)
		{
			if (dropHandle == IntPtr::Zero)
			{
				return gcnew NativeCallResult(false, ERROR_INVALID_HANDLE, 0);
			}

			::SetLastError(ERROR_SUCCESS);
			::DragFinish(static_cast<HDROP>(dropHandle.ToPointer()));
			DWORD error = ::GetLastError();
			return gcnew NativeCallResult(error == ERROR_SUCCESS, error, 0);
		}

		NativeRect^ NativeMethods::GetClientRect(IntPtr windowHandle)
		{
			RECT rect{};
			BOOL succeeded = ::GetClientRect(
				static_cast<HWND>(windowHandle.ToPointer()),
				&rect);
			DWORD error = CaptureFailure(succeeded);
			if (!succeeded)
			{
				throw gcnew Win32Exception(error, "GetClientRect failed.");
			}

			return gcnew NativeRect(rect.left, rect.top, rect.right, rect.bottom);
		}

		UInt32 NativeMethods::GetDpiForWindow(IntPtr windowHandle)
		{
			HMODULE user32 = ::GetModuleHandleW(L"user32.dll");
			if (user32 == nullptr)
			{
				DWORD error = ::GetLastError();
				if (error == ERROR_SUCCESS)
				{
					error = ERROR_MOD_NOT_FOUND;
				}
				throw gcnew Win32Exception(error, "GetModuleHandleW(user32.dll) failed.");
			}

			FARPROC entryPoint = ::GetProcAddress(user32, "GetDpiForWindow");
			if (entryPoint != nullptr)
			{
				::SetLastError(ERROR_SUCCESS);
				UINT dpi = reinterpret_cast<UINT(WINAPI*)(HWND)>(entryPoint)(
					static_cast<HWND>(windowHandle.ToPointer()));
				if (dpi == 0)
				{
					DWORD error = ::GetLastError();
					throw gcnew Win32Exception(
						error == ERROR_SUCCESS ? ERROR_INVALID_HANDLE : error,
						"GetDpiForWindow failed.");
				}

				return dpi;
			}

			DWORD lookupError = ::GetLastError();
			if (lookupError != ERROR_SUCCESS && lookupError != ERROR_PROC_NOT_FOUND)
			{
				throw gcnew Win32Exception(lookupError, "GetProcAddress(GetDpiForWindow) failed.");
			}

			// GetDpiForWindow was introduced after Windows 7. Use the system DPI
			// when the entry point is unavailable so the Native assembly remains
			// loadable and usable on Windows 7.
			HWND window = static_cast<HWND>(windowHandle.ToPointer());
			HDC deviceContext = ::GetDC(window);
			if (deviceContext == nullptr)
			{
				DWORD error = ::GetLastError();
				throw gcnew Win32Exception(
					error == ERROR_SUCCESS ? ERROR_INVALID_HANDLE : error,
					"GetDC failed while determining the system DPI.");
			}

			int dpi = ::GetDeviceCaps(deviceContext, LOGPIXELSX);
			int released = ::ReleaseDC(window, deviceContext);
			if (dpi <= 0)
			{
				throw gcnew Win32Exception(
					ERROR_INVALID_DATA,
					"GetDeviceCaps(PIXELSENSE) failed while determining the system DPI.");
			}
			if (released == 0)
			{
				DWORD error = ::GetLastError();
				throw gcnew Win32Exception(
					error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error,
					"ReleaseDC failed while determining the system DPI.");
			}

			return static_cast<UInt32>(dpi);
		}

		NativeCallResult^ NativeMethods::SendMessage(IntPtr windowHandle,
			Int32 message,
			IntPtr wParam,
			IntPtr lParam)
		{
			if (windowHandle == IntPtr::Zero)
			{
				return gcnew NativeCallResult(false, ERROR_INVALID_HANDLE, 0);
			}

			::SetLastError(ERROR_SUCCESS);
			::SendMessageW(
				static_cast<HWND>(windowHandle.ToPointer()),
				static_cast<UINT>(message),
				reinterpret_cast<WPARAM>(wParam.ToPointer()),
				reinterpret_cast<LPARAM>(lParam.ToPointer()));
			DWORD error = ::GetLastError();
			return gcnew NativeCallResult(error == ERROR_SUCCESS, error, 0);
		}

		NativeCallResult^ NativeMethods::SetTreeViewExtendedStyle(IntPtr windowHandle,
			UInt32 style,
			UInt32 mask)
		{
			if (windowHandle == IntPtr::Zero)
			{
				return gcnew NativeCallResult(false, ERROR_INVALID_HANDLE, 0);
			}

			::SetLastError(ERROR_SUCCESS);
			::SendMessageW(
				static_cast<HWND>(windowHandle.ToPointer()),
				TVM_SETEXTENDEDSTYLE,
				static_cast<WPARAM>(mask),
				static_cast<LPARAM>(style));
			DWORD error = ::GetLastError();
			return gcnew NativeCallResult(error == ERROR_SUCCESS, error, 0);
		}

		NativeCallResult^ NativeMethods::SetTreeViewItemState(IntPtr windowHandle,
			IntPtr itemHandle,
			UInt32 state,
			UInt32 stateMask)
		{
			if (windowHandle == IntPtr::Zero || itemHandle == IntPtr::Zero)
			{
				return gcnew NativeCallResult(false, ERROR_INVALID_HANDLE, 0);
			}

			TVITEMW item{};
			item.mask = TVIF_HANDLE | TVIF_STATE;
			item.hItem = static_cast<HTREEITEM>(itemHandle.ToPointer());
			item.state = state;
			item.stateMask = stateMask;

			::SetLastError(ERROR_SUCCESS);
			LRESULT messageResult = ::SendMessageW(
				static_cast<HWND>(windowHandle.ToPointer()),
				TVM_SETITEMW,
				0,
				reinterpret_cast<LPARAM>(&item));
			DWORD error = ::GetLastError();
			bool succeeded = messageResult != 0;
			if (!succeeded && error == ERROR_SUCCESS)
			{
				error = ERROR_INVALID_FUNCTION;
			}

			return gcnew NativeCallResult(succeeded, error, 0);
		}

		NativeStringCompareResult^ NativeMethods::CompareLogical(String^ left,
			String^ right)
		{
			if (left == nullptr || right == nullptr)
			{
				return gcnew NativeStringCompareResult(false,
					ERROR_INVALID_PARAMETER,
					0);
			}

			pin_ptr<const wchar_t> pinnedLeft = PtrToStringChars(left);
			pin_ptr<const wchar_t> pinnedRight = PtrToStringChars(right);
			::SetLastError(ERROR_SUCCESS);
			int comparison = ::StrCmpLogicalW(pinnedLeft, pinnedRight);
			DWORD error = ::GetLastError();
			return gcnew NativeStringCompareResult(
				error == ERROR_SUCCESS,
				error,
				comparison);
		}

		NativeCallResult^ NativeMethods::AttachConsole(Int32 processId)
		{
			BOOL succeeded = ::AttachConsole(static_cast<DWORD>(processId));
			DWORD error = CaptureFailure(succeeded);
			return gcnew NativeCallResult(succeeded, error, 0);
		}

		NativeCallResult^ NativeMethods::AllocConsole()
		{
			BOOL succeeded = ::AllocConsole();
			DWORD error = CaptureFailure(succeeded);
			return gcnew NativeCallResult(succeeded, error, 0);
		}

		NativeCallResult^ NativeMethods::FreeConsole()
		{
			BOOL succeeded = ::FreeConsole();
			DWORD error = CaptureFailure(succeeded);
			return gcnew NativeCallResult(succeeded, error, 0);
		}

		NativeHandleResult^ NativeMethods::LoadNativeLibrary(String^ path)
		{
			if (String::IsNullOrWhiteSpace(path))
			{
				return gcnew NativeHandleResult(false,
					IntPtr::Zero,
					ERROR_INVALID_PARAMETER);
			}

			pin_ptr<const wchar_t> pinnedPath = PtrToStringChars(path);
			HMODULE module = ::LoadLibraryW(pinnedPath);
			if (module == nullptr)
			{
				DWORD error = ::GetLastError();
				return gcnew NativeHandleResult(false, IntPtr::Zero, error);
			}

			return gcnew NativeHandleResult(true, IntPtr(module), ERROR_SUCCESS);
		}

		NativeCallResult^ NativeMethods::FreeLibrary(IntPtr moduleHandle)
		{
			BOOL succeeded = ::FreeLibrary(
				static_cast<HMODULE>(moduleHandle.ToPointer()));
			DWORD error = CaptureFailure(succeeded);
			return gcnew NativeCallResult(succeeded, error, 0);
		}
	}
}
