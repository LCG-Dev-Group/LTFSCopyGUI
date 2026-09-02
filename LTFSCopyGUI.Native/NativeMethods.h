#pragma once

#include <windows.h>

using namespace System;

namespace LTFSCopyGUI
{
namespace Native
{
    public ref class NativeCallResult
    {
    private:
        bool _succeeded;
        Int32 _win32Error;
        UInt32 _bytesReturned;

    public:
        NativeCallResult(bool succeeded, Int32 win32Error, UInt32 bytesReturned);

        property bool Succeeded
        {
            bool get();
        }

        property Int32 Win32Error
        {
            Int32 get();
        }

        property UInt32 BytesReturned
        {
            UInt32 get();
        }

        property String^ ErrorMessage
        {
            String^ get();
        }

        void ThrowIfFailed(String^ operation);
    };

    public ref class NativeHandleResult : NativeCallResult
    {
    private:
        IntPtr _handle;

    public:
        NativeHandleResult(bool succeeded, IntPtr handle, Int32 win32Error);

        property IntPtr Handle
        {
            IntPtr get();
        }
    };

    public ref class NativeDirectoryCaseSensitiveResult : NativeCallResult
    {
    private:
        bool _caseSensitive;

    public:
        NativeDirectoryCaseSensitiveResult(bool succeeded,
                                            bool caseSensitive,
                                            Int32 win32Error);

        property bool CaseSensitive
        {
            bool get();
        }
    };

    public ref class NativeScsiResult : NativeCallResult
    {
    private:
        array<Byte>^ _sense;

    public:
        NativeScsiResult(bool succeeded,
                         Int32 win32Error,
                         UInt32 bytesReturned,
                         array<Byte>^ sense);

        property array<Byte>^ Sense
        {
            array<Byte>^ get();
        }
    };

    public ref class NativeStorageDeviceNumberResult : NativeCallResult
    {
    private:
        Int32 _deviceType;
        Int32 _deviceNumber;
        Int32 _partitionNumber;

    public:
        NativeStorageDeviceNumberResult(bool succeeded,
                                        Int32 win32Error,
                                        UInt32 bytesReturned,
                                        Int32 deviceType,
                                        Int32 deviceNumber,
                                        Int32 partitionNumber);

        property Int32 DeviceType
        {
            Int32 get();
        }

        property Int32 DeviceNumber
        {
            Int32 get();
        }

        property Int32 PartitionNumber
        {
            Int32 get();
        }
    };

    public ref class NativeRect
    {
    private:
        Int32 _left;
        Int32 _top;
        Int32 _right;
        Int32 _bottom;

    public:
        NativeRect(Int32 left, Int32 top, Int32 right, Int32 bottom);

        property Int32 Left
        {
            Int32 get();
        }

        property Int32 Top
        {
            Int32 get();
        }

        property Int32 Right
        {
            Int32 get();
        }

        property Int32 Bottom
        {
            Int32 get();
        }
    };

    public ref class NativeDevice
    {
    private:
        bool _present;
        String^ _className;
        String^ _pdoName;
        String^ _name;

    public:
        NativeDevice(bool present,
                     String^ className,
                     String^ pdoName,
                     String^ name);

        property bool Present
        {
            bool get();
        }

        property String^ ClassName
        {
            String^ get();
        }

        property String^ PDOName
        {
            String^ get();
        }

        property String^ Name
        {
            String^ get();
        }
    };

    public ref class NativeStringCompareResult : NativeCallResult
    {
    private:
        Int32 _comparison;

    public:
        NativeStringCompareResult(bool succeeded,
                                  Int32 win32Error,
                                  Int32 comparison);

        property Int32 Comparison
        {
            Int32 get();
        }
    };

    public ref class NativeTextResult : NativeCallResult
    {
    private:
        String^ _text;

    public:
        NativeTextResult(bool succeeded,
                         Int32 win32Error,
                         String^ text);

        property String^ Text
        {
            String^ get();
        }
    };

    public ref class NativeMethods abstract sealed
    {
    public:
        literal UInt32 IoctlStorageGetDeviceNumber = 0x002D1080;
        literal UInt32 IoctlDiskGetLengthInfo = 0x0007405C;
        literal UInt32 IoctlStorageQueryProperty = 0x002D1400;
        literal UInt32 FsctlSetSparse = 0x000900C4;

        static NativeHandleResult^ OpenFile(String^ path,
                                             UInt32 desiredAccess,
                                             UInt32 shareMode,
                                             UInt32 creationDisposition,
                                             UInt32 flagsAndAttributes);

        static NativeDirectoryCaseSensitiveResult^ QueryDirectoryCaseSensitive(String^ path);

        static NativeCallResult^ CloseHandle(IntPtr handle);

        static NativeCallResult^ DeviceIoControl(IntPtr handle,
                                                  UInt32 controlCode,
                                                  IntPtr inputBuffer,
                                                  UInt32 inputBufferSize,
                                                  IntPtr outputBuffer,
                                                  UInt32 outputBufferSize);

        static NativeScsiResult^ ExecuteScsiPassThrough(IntPtr handle,
                                                        array<Byte>^ cdb,
                                                        IntPtr dataBuffer,
                                                        UInt32 dataTransferLength,
                                                        Byte dataIn,
                                                        UInt32 timeoutValue,
                                                        Byte targetId,
                                                        Byte lun);

        static NativeStorageDeviceNumberResult^ QueryStorageDeviceNumber(String^ path);

        static array<NativeDevice^>^ EnumerateDevices();
        static array<NativeDevice^>^ EnumerateDevices(String^ enumerator);

        static NativeCallResult^ ChangeWindowMessageFilterEx(IntPtr windowHandle,
                                                              UInt32 message,
                                                              UInt32 action);

        static NativeCallResult^ DragAcceptFiles(IntPtr windowHandle, bool accept);
        static array<String^>^ GetDroppedFiles(IntPtr dropHandle);
        static NativeCallResult^ DragFinish(IntPtr dropHandle);

        static NativeRect^ GetClientRect(IntPtr windowHandle);
        static UInt32 GetDpiForWindow(IntPtr windowHandle);
        static NativeCallResult^ SendMessage(IntPtr windowHandle,
                                              Int32 message,
                                              IntPtr wParam,
                                              IntPtr lParam);

        static NativeCallResult^ SetTreeViewExtendedStyle(IntPtr windowHandle,
                                                           UInt32 style,
                                                           UInt32 mask);

        static NativeCallResult^ SetTreeViewItemState(IntPtr windowHandle,
                                                       IntPtr itemHandle,
                                                       UInt32 state,
                                                       UInt32 stateMask);

        static NativeStringCompareResult^ CompareLogical(String^ left,
                                                          String^ right);

        static NativeCallResult^ AttachConsole(Int32 processId);
        static NativeCallResult^ AllocConsole();
        static NativeCallResult^ FreeConsole();

        static NativeHandleResult^ LoadNativeLibrary(String^ path);
        static NativeCallResult^ FreeLibrary(IntPtr moduleHandle);
    };
}
}
