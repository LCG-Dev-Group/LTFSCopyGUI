Imports System.Threading

' Minimal contract required by LTFSWriter's tape-output loop. Both the disk
' reader and direct-tape bridge expose the same native slot shape, so an
' adapter object and per-member delegate closures are unnecessary.
Public Interface IFastReaderConsumer
    Inherits IDisposable

    ReadOnly Property BufferedBytes As Long
    ReadOnly Property BufferCapacityBytes As Long
    ReadOnly Property OccupiedSlotCount As ULong
    ReadOnly Property RemainingBytes As Long

    Function ReadSlot(fileIndex As Long, cancellationToken As CancellationToken) As RustFastReaderProvider.Slot
    Sub AdvanceSlot(slot As RustFastReaderProvider.Slot)
    Function GetPerformanceStats() As RustFastReaderProvider.PerformanceStats
    Sub WaitForStreamFillFraction(fraction As Double, cancellationToken As CancellationToken)
    Function GetCompletedFileHashes(fileIndex As Long) As Dictionary(Of String, String)
    Sub Cancel()
End Interface
