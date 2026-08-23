#pragma once

#include "NativeMethods.h"

namespace LTFSCopyGUI
{
namespace Native
{
    public ref class NativeLtfsCommands abstract sealed
    {
    public:
        static NativeTextResult^ GetDriveMappings();
        static NativeTextResult^ StartLtfsService();
        static NativeTextResult^ StopLtfsService();
        static NativeTextResult^ RemapTapeDrives();

        static NativeTextResult^ MapTapeDrive(String^ driveLetter,
                                              String^ tapeDrive,
                                              Byte tapeIndex,
                                              String^ logDirectory,
                                              String^ workDirectory,
                                              bool showOffline);

        static NativeTextResult^ UnmapTapeDrive(String^ driveLetter);
        static NativeTextResult^ LoadTapeDrive(String^ driveLetter, bool mount);
        static NativeTextResult^ EjectTapeDrive(String^ driveLetter);
        static NativeTextResult^ MountTapeDrive(String^ driveLetter);
        static NativeTextResult^ CheckTapeMedia(String^ driveLetter);
    };
}
}
