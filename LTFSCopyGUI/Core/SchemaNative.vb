Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Globalization
Imports System.Runtime.InteropServices
Imports System.Text

Friend NotInheritable Class NativeStoreFileSummaryData
    Public Property Name As String
    Public Property Length As Long
    Public Property Partition As UInteger
    Public Property StartBlock As Long
    Public Property ByteOffset As Long
    Public Property ByteCount As Long
End Class

Friend NotInheritable Class NativeStoreSearchResultData
    Public Property Found As Boolean
    Public Property MatchKind As UInteger
    Public Property ParentDirectoryRecordOffset As Long
    Public Property RecordOffset As Long
    Public Property RecordLength As Long
    Public Property FileIndex As Long
    Public Property Path As String
    Public Property DirectoryPath As String
End Class

Friend NotInheritable Class NativeStoreTapeSortResultData
    Public Property FileCount As ULong
    Public Property PartitionAFileCount As ULong
    Public Property PartitionBFileCount As ULong
End Class

Friend NotInheritable Class NativeStoreDirectorySortResultData
    Public Property FileCount As ULong
    Public Property DirectoryCount As ULong
End Class

Friend Module NativeSchemaXml
    Private Const NativeDll As String = "ltfscopy_schema.dll"
    Friend Const StatusOk As Integer = 0
    Friend Const StatusError As Integer = -1
    Friend Const StatusInvalidArgument As Integer = -2
    Friend Const StatusInvalidData As Integer = -3
    Friend Const StatusBufferTooSmall As Integer = -4
    Friend Const NativeDirectorySortModeLogical As UInteger = 1UI
    Friend Const NativeDirectorySortModeCurrentCulture As UInteger = 2UI

    Friend Const SchemaStringCreator As UInteger = 1
    Friend Const SchemaStringVolumeUuid As UInteger = 2
    Friend Const SchemaStringUpdateTime As UInteger = 3

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeUtf16Slice
        Public Pointer As IntPtr
        Public Length As UInteger
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeSchemaResult
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public RootFileIndexOffset As Long
        Public RootFileCount As ULong
        Public RootDirectoryIndexOffset As Long
        Public RootDirectoryCount As ULong
        Public SelectionCount As ULong
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeSchemaMetadata
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public PresentMask As UInteger
        Public Reserved As UInteger
        Public GenerationNumber As ULong
        Public LocationPartition As UInteger
        Public LocationReserved As UInteger
        Public LocationStartBlock As ULong
        Public PreviousLocationPartition As UInteger
        Public PreviousLocationReserved As UInteger
        Public PreviousLocationStartBlock As ULong
        Public AllowPolicyUpdate As UInteger
        Public DataPlacementPolicy As UInteger
        Public VolumeLockState As UInteger
        Public HighestFileUid As Long
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeFileInfo
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public Length As Long
        Public [ReadOnly] As UInteger
        Public OpenForWrite As UInteger
        Public FileUid As Long
        Public XattrCount As UInteger
        Public ExtentCount As UInteger
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeExtent
        Public FileOffset As Long
        Public Partition As UInteger
        Public Reserved As UInteger
        Public StartBlock As Long
        Public ByteOffset As Long
        Public ByteCount As Long
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeXattrInput
        Public Key As NativeUtf16Slice
        Public Value As NativeUtf16Slice
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeExtentInput
        Public FileOffset As Long
        Public Partition As UInteger
        Public Reserved As UInteger
        Public StartBlock As Long
        Public ByteOffset As Long
        Public ByteCount As Long
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeFileInput
        Public StructSize As UInteger
        Public Reserved As UInteger
        Public Name As NativeUtf16Slice
        Public Length As Long
        Public [ReadOnly] As UInteger
        Public OpenForWrite As UInteger
        Public CreationTime As NativeUtf16Slice
        Public ChangeTime As NativeUtf16Slice
        Public ModifyTime As NativeUtf16Slice
        Public AccessTime As NativeUtf16Slice
        Public BackupTime As NativeUtf16Slice
        Public FileUid As Long
        Public Symlink As NativeUtf16Slice
        Public Xattrs As IntPtr
        Public XattrCount As UInteger
        Public Extents As IntPtr
        Public ExtentCount As UInteger
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeStoreDirectoryInfo
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public ScalarOffset As Long
        Public ScalarLength As Long
        Public FileIndexOffset As Long
        Public FileCount As Long
        Public DirectoryIndexOffset As Long
        Public DirectoryCount As Long
        Public TotalFileCount As Long
        Public TotalDirectoryCount As Long
        Public [ReadOnly] As UInteger
        Public Reserved As UInteger
        Public FileUid As Long
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeStoreFileIndexEntry
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public NextOffset As Long
        Public RecordOffset As Long
        Public RecordLength As Long
        Public SelectionIndex As Long
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeStoreFileSummary
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public Length As Long
        Public Partition As UInteger
        Public Reserved As UInteger
        Public StartBlock As Long
        Public ByteOffset As Long
        Public ByteCount As Long
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeStoreDirectoryIndexEntry
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public NextOffset As Long
        Public RecordOffset As Long
        Public SelectionIndex As Long
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeStoreSearchResult
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public Found As UInteger
        Public MatchKind As UInteger
        Public ParentDirectoryRecordOffset As Long
        Public RecordOffset As Long
        Public RecordLength As Long
        Public FileIndex As Long
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeStoreTapeSortResult
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public FileCount As ULong
        Public PartitionAFileCount As ULong
        Public PartitionBFileCount As ULong
    End Structure

    <StructLayout(LayoutKind.Sequential, Pack:=8)>
    Friend Structure NativeStoreDirectorySortResult
        Public StructSize As UInteger
        Public AbiVersion As UInteger
        Public FileCount As ULong
        Public DirectoryCount As ULong
    End Structure

    <UnmanagedFunctionPointer(CallingConvention.Winapi)>
    Friend Delegate Sub NativeSearchProgressCallback(processed As ULong, total As ULong, userData As IntPtr)

    <UnmanagedFunctionPointer(CallingConvention.Winapi)>
    Friend Delegate Sub NativeTapeSortProgressCallback(processed As ULong, total As ULong, userData As IntPtr)

    <UnmanagedFunctionPointer(CallingConvention.Winapi)>
    Friend Delegate Sub NativeDirectorySortProgressCallback(processed As ULong, total As ULong, userData As IntPtr)

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_parse_schema_file(
        <MarshalAs(UnmanagedType.LPWStr)> inputPath As String,
        inputPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> fileRecordsPath As String,
        fileRecordsPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> directoryRecordsPath As String,
        directoryRecordsPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> fileIndexPath As String,
        fileIndexPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> directoryIndexPath As String,
        directoryIndexPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> selectionPath As String,
        selectionPathLength As UInteger,
        ByRef output As IntPtr) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_merge_schema_files(
        <MarshalAs(UnmanagedType.LPWStr)> inputPaths As String,
        inputPathsLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> rootName As String,
        rootNameLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> fileRecordsPath As String,
        fileRecordsPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> directoryRecordsPath As String,
        directoryRecordsPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> fileIndexPath As String,
        fileIndexPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> directoryIndexPath As String,
        directoryIndexPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> selectionPath As String,
        selectionPathLength As UInteger,
        ByRef output As IntPtr) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_store_open(
        <MarshalAs(UnmanagedType.LPWStr)> fileRecordsPath As String,
        fileRecordsPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> directoryRecordsPath As String,
        directoryRecordsPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> fileIndexPath As String,
        fileIndexPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> directoryIndexPath As String,
        directoryIndexPathLength As UInteger,
        ByRef output As IntPtr) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Sub lsc_store_close(context As IntPtr)
    End Sub

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_store_get_directory_info(
        context As IntPtr,
        recordOffset As Long,
        ByRef output As NativeStoreDirectoryInfo) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_store_get_directory_file_bytes(
        context As IntPtr,
        recordOffset As Long,
        ByRef output As Long) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_store_copy_directory_string(
        context As IntPtr,
        recordOffset As Long,
        field As UInteger,
        <Out> buffer As StringBuilder,
        capacity As UInteger,
        ByRef required As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_store_get_file_index_entry(
        context As IntPtr,
        offset As Long,
        ByRef output As NativeStoreFileIndexEntry) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_store_get_directory_index_entry(
        context As IntPtr,
        offset As Long,
        ByRef output As NativeStoreDirectoryIndexEntry) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_store_search(
        context As IntPtr,
        rootRecordOffset As Long,
        <MarshalAs(UnmanagedType.LPWStr)> rootPath As String,
        rootPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> keyword As String,
        keywordLength As UInteger,
        caseSensitive As UInteger,
        resumeKind As UInteger,
        resumeRecordOffset As Long,
        callback As NativeSearchProgressCallback,
        userData As IntPtr,
        ByRef output As NativeStoreSearchResult,
        <Out> pathBuffer As StringBuilder,
        pathCapacity As UInteger,
        ByRef pathRequired As UInteger,
        <Out> directoryPathBuffer As StringBuilder,
        directoryPathCapacity As UInteger,
        ByRef directoryPathRequired As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_store_tape_sort(
        context As IntPtr,
        rootFileIndexOffset As Long,
        rootFileCount As ULong,
        rootDirectoryIndexOffset As Long,
        rootDirectoryCount As ULong,
        <MarshalAs(UnmanagedType.LPWStr)> selectionPath As String,
        selectionPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> outputPath As String,
        outputPathLength As UInteger,
        callback As NativeTapeSortProgressCallback,
        userData As IntPtr,
        ByRef output As NativeStoreTapeSortResult) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_store_sort_directory_children(
        context As IntPtr,
        directoryRecordOffset As Long,
        sortMode As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> localeName As String,
        localeNameLength As UInteger,
        fileTargetIndexOffset As Long,
        directoryTargetIndexOffset As Long,
        <MarshalAs(UnmanagedType.LPWStr)> fileOutputPath As String,
        fileOutputPathLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> directoryOutputPath As String,
        directoryOutputPathLength As UInteger,
        callback As NativeDirectorySortProgressCallback,
        userData As IntPtr,
        ByRef output As NativeStoreDirectorySortResult) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_store_copy_file_record(
        context As IntPtr,
        recordOffset As Long,
        recordLength As ULong,
        buffer As IntPtr,
        capacity As ULong,
        ByRef written As ULong) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_store_copy_file_name(
        context As IntPtr,
        recordOffset As Long,
        recordLength As ULong,
        <Out> buffer As StringBuilder,
        capacity As UInteger,
        ByRef required As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_store_copy_file_summary(
        context As IntPtr,
        recordOffset As Long,
        recordLength As ULong,
        <Out> nameBuffer As StringBuilder,
        nameCapacity As UInteger,
        ByRef nameRequired As UInteger,
        ByRef output As NativeStoreFileSummary) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_schema_get_result(context As IntPtr, ByRef output As NativeSchemaResult) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_schema_get_metadata(context As IntPtr, ByRef output As NativeSchemaMetadata) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_schema_copy_string(
        context As IntPtr,
        field As UInteger,
        <Out> buffer As StringBuilder,
        capacity As UInteger,
        ByRef required As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Sub lsc_schema_destroy(context As IntPtr)
    End Sub

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_file_parse(data As IntPtr, length As ULong, ByRef output As IntPtr) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_file_get_info(context As IntPtr, ByRef output As NativeFileInfo) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_file_copy_string(
        context As IntPtr,
        field As UInteger,
        <Out> buffer As StringBuilder,
        capacity As UInteger,
        ByRef required As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_file_copy_xattr_string(
        context As IntPtr,
        index As UInteger,
        field As UInteger,
        <Out> buffer As StringBuilder,
        capacity As UInteger,
        ByRef required As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_file_get_extent(context As IntPtr, index As UInteger, ByRef output As NativeExtent) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Sub lsc_file_destroy(context As IntPtr)
    End Sub

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_file_serialize(ByRef input As NativeFileInput, buffer As IntPtr, capacity As UInteger, ByRef written As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_last_error(<Out> buffer As StringBuilder, capacity As UInteger, ByRef required As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_writer_open(
        <MarshalAs(UnmanagedType.LPWStr)> path As String,
        pathLength As UInteger,
        ByRef output As IntPtr) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_writer_start(writer As IntPtr, <MarshalAs(UnmanagedType.LPWStr)> name As String, nameLength As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_writer_start_attribute(
        writer As IntPtr,
        <MarshalAs(UnmanagedType.LPWStr)> name As String,
        nameLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> attributeName As String,
        attributeNameLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> attributeValue As String,
        attributeValueLength As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_writer_empty(writer As IntPtr, <MarshalAs(UnmanagedType.LPWStr)> name As String, nameLength As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_writer_end(writer As IntPtr, <MarshalAs(UnmanagedType.LPWStr)> name As String, nameLength As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True, CharSet:=CharSet.Unicode)>
    Private Function lsc_writer_element(
        writer As IntPtr,
        <MarshalAs(UnmanagedType.LPWStr)> name As String,
        nameLength As UInteger,
        <MarshalAs(UnmanagedType.LPWStr)> value As String,
        valueLength As UInteger) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_writer_file(writer As IntPtr, ByRef input As NativeFileInput) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_writer_raw(writer As IntPtr, data As IntPtr, length As ULong) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_writer_store_file_record(
        writer As IntPtr,
        store As IntPtr,
        recordOffset As Long,
        recordLength As ULong) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_writer_store_directory_files(
        writer As IntPtr,
        store As IntPtr,
        directoryRecordOffset As Long) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Function lsc_writer_finish(writer As IntPtr) As Integer
    End Function

    <DllImport(NativeDll, CallingConvention:=CallingConvention.Winapi, ExactSpelling:=True)>
    Private Sub lsc_writer_destroy(writer As IntPtr)
    End Sub

    Private Sub Check(status As Integer, operation As String)
        If status = StatusOk Then Return
        Dim required As UInteger = 0
        Dim errorText As String = "native schema operation failed"
        Try
            lsc_last_error(Nothing, 0, required)
            If required > 0 AndAlso required <= Integer.MaxValue Then
                Dim builder As New StringBuilder(CInt(required))
                lsc_last_error(builder, required, required)
                errorText = builder.ToString()
            End If
        Catch
        End Try
        Throw New InvalidDataException(operation & ": " & errorText & " (status " & status.ToString(CultureInfo.InvariantCulture) & ")")
    End Sub

    Private Delegate Function NativeStringCopy(buffer As StringBuilder, capacity As UInteger, ByRef required As UInteger) As Integer

    Private Function CopyNativeString(copy As NativeStringCopy, operation As String) As String
        Dim required As UInteger = 0
        Dim status As Integer = copy(Nothing, 0, required)
        If status <> StatusBufferTooSmall AndAlso status <> StatusOk Then Check(status, operation)
        If required = 0 Then Return String.Empty
        If required > Integer.MaxValue Then Throw New InvalidDataException(operation & ": string is too long")
        Dim builder As New StringBuilder(CInt(required))
        Check(copy(builder, required, required), operation)
        Return builder.ToString()
    End Function

    Friend Sub OpenStore(fileRecordsPath As String,
                         directoryRecordsPath As String,
                         fileIndexPath As String,
                         directoryIndexPath As String,
                         ByRef handle As IntPtr)
        Check(lsc_store_open(fileRecordsPath, CUInt(fileRecordsPath.Length),
                             directoryRecordsPath, CUInt(directoryRecordsPath.Length),
                             fileIndexPath, CUInt(fileIndexPath.Length),
                             directoryIndexPath, CUInt(directoryIndexPath.Length),
                             handle), "open schema backing store")
    End Sub

    Friend Sub CloseStore(handle As IntPtr)
        If handle <> IntPtr.Zero Then lsc_store_close(handle)
    End Sub

    Friend Function ReadStoreDirectoryInfo(handle As IntPtr, recordOffset As Long) As NativeStoreDirectoryInfo
        Dim result As New NativeStoreDirectoryInfo
        Check(lsc_store_get_directory_info(handle, recordOffset, result), "read schema directory info")
        Return result
    End Function

    Friend Function ReadStoreDirectoryFileBytes(handle As IntPtr, recordOffset As Long) As Long
        Dim result As Long
        Check(lsc_store_get_directory_file_bytes(handle, recordOffset, result), "read schema directory file bytes")
        Return result
    End Function

    Friend Function CopyStoreDirectoryString(handle As IntPtr, recordOffset As Long, field As UInteger) As String
        Return CopyNativeString(Function(buffer As StringBuilder, capacity As UInteger, ByRef required As UInteger) As Integer
                                    Return lsc_store_copy_directory_string(handle, recordOffset, field, buffer, capacity, required)
                                End Function, "copy schema directory string")
    End Function

    Friend Function ReadStoreFileIndexEntry(handle As IntPtr, offset As Long) As NativeStoreFileIndexEntry
        Dim result As New NativeStoreFileIndexEntry
        Check(lsc_store_get_file_index_entry(handle, offset, result), "read schema file index")
        Return result
    End Function

    Friend Function ReadStoreDirectoryIndexEntry(handle As IntPtr, offset As Long) As NativeStoreDirectoryIndexEntry
        Dim result As New NativeStoreDirectoryIndexEntry
        Check(lsc_store_get_directory_index_entry(handle, offset, result), "read schema directory index")
        Return result
    End Function

    Friend Function SearchStore(handle As IntPtr,
                                rootRecordOffset As Long,
                                rootPath As String,
                                keyword As String,
                                caseSensitive As Boolean,
                                resumeKind As UInteger,
                                resumeRecordOffset As Long,
                                progressCallback As NativeSearchProgressCallback) As NativeStoreSearchResultData
        If handle = IntPtr.Zero Then Throw New ArgumentNullException(NameOf(handle))

        Dim pathCapacity As UInteger = 32768UI
        Dim directoryPathCapacity As UInteger = 32768UI
        Dim pathBuffer As New StringBuilder(CInt(pathCapacity))
        Dim directoryPathBuffer As New StringBuilder(CInt(directoryPathCapacity))
        Dim pathRequired As UInteger = 0UI
        Dim directoryPathRequired As UInteger = 0UI
        Dim native As New NativeStoreSearchResult
        Dim status As Integer = lsc_store_search(
            handle,
            rootRecordOffset,
            If(rootPath, String.Empty),
            CUInt(If(rootPath, String.Empty).Length),
            If(keyword, String.Empty),
            CUInt(If(keyword, String.Empty).Length),
            If(caseSensitive, 1UI, 0UI),
            resumeKind,
            resumeRecordOffset,
            progressCallback,
            IntPtr.Zero,
            native,
            pathBuffer,
            pathCapacity,
            pathRequired,
            directoryPathBuffer,
            directoryPathCapacity,
            directoryPathRequired)

        If status = StatusBufferTooSmall Then
            If pathRequired > Integer.MaxValue OrElse directoryPathRequired > Integer.MaxValue Then
                Throw New InvalidDataException("Native schema search path is too long.")
            End If
            If pathRequired > pathCapacity Then pathCapacity = pathRequired
            If directoryPathRequired > directoryPathCapacity Then directoryPathCapacity = directoryPathRequired
            pathBuffer = New StringBuilder(CInt(pathCapacity))
            directoryPathBuffer = New StringBuilder(CInt(directoryPathCapacity))
            pathRequired = 0UI
            directoryPathRequired = 0UI
            status = lsc_store_search(
                handle,
                rootRecordOffset,
                If(rootPath, String.Empty),
                CUInt(If(rootPath, String.Empty).Length),
                If(keyword, String.Empty),
                CUInt(If(keyword, String.Empty).Length),
                If(caseSensitive, 1UI, 0UI),
                resumeKind,
                resumeRecordOffset,
                progressCallback,
                IntPtr.Zero,
                native,
                pathBuffer,
                pathCapacity,
                pathRequired,
                directoryPathBuffer,
                directoryPathCapacity,
                directoryPathRequired)
        End If
        Check(status, "search schema store")

        Return New NativeStoreSearchResultData With {
            .Found = native.Found <> 0UI,
            .MatchKind = native.MatchKind,
            .ParentDirectoryRecordOffset = native.ParentDirectoryRecordOffset,
            .RecordOffset = native.RecordOffset,
            .RecordLength = native.RecordLength,
            .FileIndex = native.FileIndex,
            .Path = pathBuffer.ToString(),
            .DirectoryPath = directoryPathBuffer.ToString()}
    End Function

    Friend Function TapeSortStore(handle As IntPtr,
                                  rootFileIndexOffset As Long,
                                  rootFileCount As ULong,
                                  rootDirectoryIndexOffset As Long,
                                  rootDirectoryCount As ULong,
                                  selectionPath As String,
                                  outputPath As String,
                                  progressCallback As NativeTapeSortProgressCallback) As NativeStoreTapeSortResultData
        If handle = IntPtr.Zero Then Throw New ArgumentNullException(NameOf(handle))
        If String.IsNullOrWhiteSpace(selectionPath) Then Throw New ArgumentException("Selection path is required.", NameOf(selectionPath))
        If String.IsNullOrWhiteSpace(outputPath) Then Throw New ArgumentException("Output path is required.", NameOf(outputPath))

        Dim native As New NativeStoreTapeSortResult
        Check(lsc_store_tape_sort(
                  handle,
                  rootFileIndexOffset,
                  rootFileCount,
                  rootDirectoryIndexOffset,
                  rootDirectoryCount,
                  selectionPath,
                  CUInt(selectionPath.Length),
                  outputPath,
                  CUInt(outputPath.Length),
                  progressCallback,
                  IntPtr.Zero,
                  native), "sort schema files by tape position")
        Return New NativeStoreTapeSortResultData With {
            .FileCount = native.FileCount,
            .PartitionAFileCount = native.PartitionAFileCount,
            .PartitionBFileCount = native.PartitionBFileCount}
    End Function

    Friend Function SortDirectoryChildrenStore(handle As IntPtr,
                                                directoryRecordOffset As Long,
                                                sortMode As UInteger,
                                                localeName As String,
                                                fileTargetIndexOffset As Long,
                                                directoryTargetIndexOffset As Long,
                                                fileOutputPath As String,
                                                directoryOutputPath As String,
                                                progressCallback As NativeDirectorySortProgressCallback) As NativeStoreDirectorySortResultData
        If handle = IntPtr.Zero Then Throw New ArgumentNullException(NameOf(handle))
        If String.IsNullOrWhiteSpace(fileOutputPath) Then Throw New ArgumentException("File sort output path is required.", NameOf(fileOutputPath))
        If String.IsNullOrWhiteSpace(directoryOutputPath) Then Throw New ArgumentException("Directory sort output path is required.", NameOf(directoryOutputPath))

        Dim native As New NativeStoreDirectorySortResult
        Check(lsc_store_sort_directory_children(
                  handle,
                  directoryRecordOffset,
                  sortMode,
                  If(localeName, String.Empty),
                  CUInt(If(localeName, String.Empty).Length),
                  fileTargetIndexOffset,
                  directoryTargetIndexOffset,
                  fileOutputPath,
                  CUInt(fileOutputPath.Length),
                  directoryOutputPath,
                  CUInt(directoryOutputPath.Length),
                  progressCallback,
                  IntPtr.Zero,
                  native), "sort lazy directory children")
        Return New NativeStoreDirectorySortResultData With {
            .FileCount = native.FileCount,
            .DirectoryCount = native.DirectoryCount}
    End Function

    Friend Function ReadStoreFileRecord(handle As IntPtr, recordOffset As Long, recordLength As Long) As Byte()
        If recordOffset < 0 OrElse recordLength < 0 Then Throw New InvalidDataException("Invalid native schema file record.")
        Dim required As ULong = 0
        Dim status As Integer = lsc_store_copy_file_record(handle, recordOffset, CULng(recordLength), IntPtr.Zero, 0UL, required)
        If status <> StatusBufferTooSmall AndAlso status <> StatusOk Then Check(status, "size schema file record")
        If required = 0UL Then Return Array.Empty(Of Byte)()
        If required > Integer.MaxValue Then Throw New InvalidDataException("Native schema file record is too large.")
        Dim buffer As IntPtr = Marshal.AllocHGlobal(CInt(required))
        Try
            Dim written As ULong = 0
            Check(lsc_store_copy_file_record(handle, recordOffset, CULng(recordLength), buffer, required, written), "read schema file record")
            If written > Integer.MaxValue Then Throw New InvalidDataException("Native schema file record is too large.")
            If written = 0UL Then Return Array.Empty(Of Byte)()
            Dim result(CInt(written) - 1) As Byte
            Marshal.Copy(buffer, result, 0, result.Length)
            Return result
        Finally
            Marshal.FreeHGlobal(buffer)
        End Try
    End Function

    Friend Function CopyStoreFileName(handle As IntPtr, recordOffset As Long, recordLength As Long) As String
        If recordOffset < 0 OrElse recordLength < 0 Then Throw New InvalidDataException("Invalid native schema file record.")
        'LTFS names are normally short.  Avoid the size-probe call for the
        'common case because the native side would otherwise read and parse
        'the same lazy record twice.
        Dim initialCapacity As UInteger = 512UI
        Dim required As UInteger = 0UI
        Dim builder As New StringBuilder(CInt(initialCapacity))
        Dim status As Integer = lsc_store_copy_file_name(handle, recordOffset, CULng(recordLength), builder, initialCapacity, required)
        If status = StatusOk Then Return builder.ToString()
        If status <> StatusBufferTooSmall Then Check(status, "copy schema file name")
        If required = 0UI Then Return String.Empty
        If required > Integer.MaxValue Then Throw New InvalidDataException("Schema file name is too long.")
        builder = New StringBuilder(CInt(required))
        Check(lsc_store_copy_file_name(handle, recordOffset, CULng(recordLength), builder, required, required), "copy schema file name")
        Return builder.ToString()
    End Function

    Friend Function CopyStoreFileSummary(handle As IntPtr, recordOffset As Long, recordLength As Long) As NativeStoreFileSummaryData
        If recordOffset < 0 OrElse recordLength < 0 Then Throw New InvalidDataException("Invalid native schema file record.")

        'The usual LTFS file name fits in this buffer, so the common path only
        'reads and parses the lazy record once.
        Dim initialCapacity As UInteger = 512UI
        Dim required As UInteger = 0UI
        Dim native As New NativeStoreFileSummary
        Dim builder As New StringBuilder(CInt(initialCapacity))
        Dim status As Integer = lsc_store_copy_file_summary(
            handle, recordOffset, CULng(recordLength), builder, initialCapacity, required, native)
        If status = StatusBufferTooSmall Then
            If required = 0UI Then Return New NativeStoreFileSummaryData With {
                .Name = String.Empty,
                .Length = native.Length,
                .Partition = native.Partition,
                .StartBlock = native.StartBlock,
                .ByteOffset = native.ByteOffset,
                .ByteCount = native.ByteCount}
            If required > Integer.MaxValue Then Throw New InvalidDataException("Schema file name is too long.")
            builder = New StringBuilder(CInt(required))
            Check(lsc_store_copy_file_summary(
                      handle, recordOffset, CULng(recordLength), builder, required, required, native),
                  "copy schema file summary")
        Else
            Check(status, "copy schema file summary")
        End If

        Return New NativeStoreFileSummaryData With {
            .Name = builder.ToString(),
            .Length = native.Length,
            .Partition = native.Partition,
            .StartBlock = native.StartBlock,
            .ByteOffset = native.ByteOffset,
            .ByteCount = native.ByteCount}
    End Function

    Private Function CopySchemaString(context As IntPtr, field As UInteger) As String
        Return CopyNativeString(Function(buffer As StringBuilder, capacity As UInteger, ByRef required As UInteger) As Integer
                                    Return lsc_schema_copy_string(context, field, buffer, capacity, required)
                                End Function, "copy schema string")
    End Function

    Private Function CopyFileString(context As IntPtr, field As UInteger) As String
        Return CopyNativeString(Function(buffer As StringBuilder, capacity As UInteger, ByRef required As UInteger) As Integer
                                    Return lsc_file_copy_string(context, field, buffer, capacity, required)
                                End Function, "copy file string")
    End Function

    Private Function CopyXattrString(context As IntPtr, index As UInteger, field As UInteger) As String
        Return CopyNativeString(Function(buffer As StringBuilder, capacity As UInteger, ByRef required As UInteger) As Integer
                                    Return lsc_file_copy_xattr_string(context, index, field, buffer, capacity, required)
                                End Function, "copy xattr string")
    End Function

    Friend Function ParseFileRecord(bytes As Byte()) As NativeParsedFile
        If bytes Is Nothing Then Throw New ArgumentNullException(NameOf(bytes))
        Dim nativeBytes As IntPtr = IntPtr.Zero
        Dim context As IntPtr = IntPtr.Zero
        Try
            If bytes.Length > 0 Then
                nativeBytes = Marshal.AllocHGlobal(bytes.Length)
                Marshal.Copy(bytes, 0, nativeBytes, bytes.Length)
            End If
            Check(lsc_file_parse(nativeBytes, CULng(bytes.Length), context), "parse file record")
            Dim info As New NativeFileInfo
            Check(lsc_file_get_info(context, info), "read file record info")
            Dim result As New NativeParsedFile With {
                .Scalars = New LazyFileScalarData With {
                    .Name = CopyFileString(context, 1),
                    .Length = info.Length,
                    .ReadOnly = info.[ReadOnly] <> 0,
                    .OpenForWrite = info.OpenForWrite <> 0,
                    .CreationTime = CopyFileString(context, 2),
                    .ChangeTime = CopyFileString(context, 3),
                    .ModifyTime = CopyFileString(context, 4),
                    .AccessTime = CopyFileString(context, 5),
                    .BackupTime = CopyFileString(context, 6),
                    .FileUid = info.FileUid,
                    .Symlink = CopyFileString(context, 7)},
                .ExtendedAttributes = New List(Of ltfsindex.file.xattr),
                .Extents = New List(Of ltfsindex.file.extent)}
            If info.XattrCount > 0UI Then
                For i As UInteger = 0UI To info.XattrCount - 1UI
                    result.ExtendedAttributes.Add(New ltfsindex.file.xattr With {
                        .key = CopyXattrString(context, i, 0),
                        .value = CopyXattrString(context, i, 1)})
                Next
            End If
            If info.ExtentCount > 0UI Then
                For i As UInteger = 0UI To info.ExtentCount - 1UI
                    Dim extent As New NativeExtent
                    Check(lsc_file_get_extent(context, i, extent), "read file extent")
                    result.Extents.Add(New ltfsindex.file.extent With {
                        .fileoffset = extent.FileOffset,
                        .partition = CType(extent.Partition, ltfsindex.PartitionLabel),
                        .startblock = extent.StartBlock,
                        .byteoffset = extent.ByteOffset,
                        .bytecount = extent.ByteCount})
                Next
            End If
            Return result
        Finally
            If context <> IntPtr.Zero Then lsc_file_destroy(context)
            If nativeBytes <> IntPtr.Zero Then Marshal.FreeHGlobal(nativeBytes)
        End Try
    End Function

    Friend Function SerializeFile(value As ltfsindex.file) As Byte()
        Using input As New NativeFileInputOwner(value)
            Dim written As UInteger = 0
            Dim nativeInput As NativeFileInput = input.Input
            Dim status As Integer = lsc_file_serialize(nativeInput, IntPtr.Zero, 0, written)
            If status <> StatusBufferTooSmall AndAlso status <> StatusOk Then Check(status, "size file record")
            If written = 0 Then Return Array.Empty(Of Byte)()
            Dim buffer As IntPtr = Marshal.AllocHGlobal(CInt(written))
            Try
                Dim capacity As UInteger = written
                nativeInput = input.Input
                Check(lsc_file_serialize(nativeInput, buffer, capacity, written), "serialize file record")
                Dim result(CInt(written) - 1) As Byte
                Marshal.Copy(buffer, result, 0, result.Length)
                Return result
            Finally
                Marshal.FreeHGlobal(buffer)
            End Try
        End Using
    End Function

    Private Function NewBackingPaths() As String()
        Return New String() {
            LazySchemaStore.CreateTempFilePath("files"),
            LazySchemaStore.CreateTempFilePath("directories"),
            LazySchemaStore.CreateTempFilePath("file-index"),
            LazySchemaStore.CreateTempFilePath("directory-index"),
            LazySchemaStore.CreateTempFilePath("selection")}
    End Function

    Private Sub DeleteBackingPaths(paths As String())
        If paths Is Nothing Then Return
        For Each path As String In paths
            Try
                If File.Exists(path) Then File.Delete(path)
            Catch
            End Try
        Next
    End Sub

    Private Function LoadNative(fileName As String) As ltfsindex
        Dim paths As String() = NewBackingPaths()
        Dim nativeContext As IntPtr = IntPtr.Zero
        Try
            Dim status As Integer = lsc_parse_schema_file(fileName, CUInt(fileName.Length),
                                                            paths(0), CUInt(paths(0).Length),
                                                            paths(1), CUInt(paths(1).Length),
                                                            paths(2), CUInt(paths(2).Length),
                                                            paths(3), CUInt(paths(3).Length),
                                                            paths(4), CUInt(paths(4).Length),
                                                            nativeContext)
            Check(status, "parse schema")

            Return ImportNativeContext(nativeContext, paths)
        Finally
            If nativeContext <> IntPtr.Zero Then lsc_schema_destroy(nativeContext)
            DeleteBackingPaths(paths)
        End Try
    End Function

    Private Function ImportNativeContext(nativeContext As IntPtr, ByRef paths As String()) As ltfsindex
        Dim store As LazySchemaStore = Nothing
        Try
            If nativeContext = IntPtr.Zero Then Throw New InvalidDataException("native schema context is null")

            Dim nativeResult As New NativeSchemaResult
            Check(lsc_schema_get_result(nativeContext, nativeResult), "read schema result")
            Dim metadata As New NativeSchemaMetadata
            Check(lsc_schema_get_metadata(nativeContext, metadata), "read schema metadata")
            store = LazySchemaStore.CreateForNativeImport(paths)
            store.SetNativeRootIndexes(nativeResult.RootFileIndexOffset,
                                       nativeResult.RootFileCount,
                                       nativeResult.RootDirectoryIndexOffset,
                                       nativeResult.RootDirectoryCount)
            paths = Nothing

            Dim result As New ltfsindex
            ApplyMetadata(result, nativeContext, metadata)
            For Each child As LazySchemaChildData In store.EnumerateNativeRootFileReferences(nativeResult.RootFileIndexOffset, CInt(nativeResult.RootFileCount))
                Dim rootFile As New ltfsindex.file
                rootFile.AttachLazyRecord(store, child.RecordOffset, child.RecordLength, child.SelectionIndex)
                result._file.Add(rootFile)
            Next
            For Each child As LazySchemaChildData In store.EnumerateNativeRootDirectoryReferences(nativeResult.RootDirectoryIndexOffset, CInt(nativeResult.RootDirectoryCount))
                Dim rootDirectory As New ltfsindex.directory
                rootDirectory.AttachLazyRecord(store, child.RecordOffset, selectionIndex:=child.SelectionIndex)
                result._directory.Add(rootDirectory)
            Next
            result.AttachLazyStore(store)
            store = Nothing
            Return result
        Catch
            If store IsNot Nothing Then store.AbortBuild()
            Throw
        End Try
    End Function

    Friend Function MergeIndexes(fileNames As IList(Of String), rootName As String) As ltfsindex
        If fileNames Is Nothing Then Return Nothing
        Dim paths As String() = NewBackingPaths()
        Dim nativeContext As IntPtr = IntPtr.Zero
        Try
            Dim joinedPaths As String = String.Join(ChrW(0), fileNames)
            Dim safeRootName As String = If(rootName, String.Empty)
            Dim status As Integer = lsc_merge_schema_files(
                joinedPaths,
                CUInt(joinedPaths.Length),
                safeRootName,
                CUInt(safeRootName.Length),
                paths(0),
                CUInt(paths(0).Length),
                paths(1),
                CUInt(paths(1).Length),
                paths(2),
                CUInt(paths(2).Length),
                paths(3),
                CUInt(paths(3).Length),
                paths(4),
                CUInt(paths(4).Length),
                nativeContext)
            Check(status, "merge schema files")
            Return ImportNativeContext(nativeContext, paths)
        Finally
            If nativeContext <> IntPtr.Zero Then lsc_schema_destroy(nativeContext)
            DeleteBackingPaths(paths)
        End Try
    End Function

    Private Sub ApplyMetadata(result As ltfsindex, context As IntPtr, metadata As NativeSchemaMetadata)
        If (metadata.PresentMask And 1UI) <> 0UI Then result.creator = CopySchemaString(context, SchemaStringCreator)
        If (metadata.PresentMask And 2UI) <> 0UI Then
            Dim volume As Guid
            If Guid.TryParse(CopySchemaString(context, SchemaStringVolumeUuid), volume) Then result.volumeuuid = volume
        End If
        If (metadata.PresentMask And 4UI) <> 0UI Then result.generationnumber = metadata.GenerationNumber
        If (metadata.PresentMask And 8UI) <> 0UI Then result.updatetime = CopySchemaString(context, SchemaStringUpdateTime)
        If (metadata.PresentMask And 16UI) <> 0UI Then result.location = New ltfsindex.LocationDef With {
            .partition = CType(metadata.LocationPartition, ltfsindex.PartitionLabel),
            .startblock = metadata.LocationStartBlock}
        If (metadata.PresentMask And 32UI) <> 0UI Then result.previousgenerationlocation = New ltfsindex.LocationDef With {
            .partition = CType(metadata.PreviousLocationPartition, ltfsindex.PartitionLabel),
            .startblock = metadata.PreviousLocationStartBlock}
        If (metadata.PresentMask And 64UI) <> 0UI Then result.allowpolicyupdate = metadata.AllowPolicyUpdate <> 0UI
        If (metadata.PresentMask And 128UI) <> 0UI Then result.dataplacementpolicy = New ltfsindex.policy
        If (metadata.PresentMask And 256UI) <> 0UI Then result.volumelockstate = CType(metadata.VolumeLockState, ltfsindex.volumelockstateValue)
        If (metadata.PresentMask And 512UI) <> 0UI Then result.highestfileuid = metadata.HighestFileUid
    End Sub

    Friend Function LoadIndex(fileName As String) As ltfsindex
        If String.IsNullOrWhiteSpace(fileName) OrElse Not File.Exists(fileName) Then Return Nothing
        Return LoadNative(fileName)
    End Function

    Friend Function LoadDirectory(fileName As String) As ltfsindex.directory
        Dim index As ltfsindex = LoadIndex(fileName)
        If index Is Nothing OrElse index._directory Is Nothing OrElse index._directory.Count = 0 Then Return Nothing
        Return index._directory(0)
    End Function

    Friend Function LoadText(text As String) As ltfsindex
        Dim path As String = LazySchemaStore.CreateTempFilePath("input")
        Try
            File.WriteAllText(path, If(text, String.Empty), New UTF8Encoding(False))
            Return LoadIndex(path)
        Finally
            Try
                If File.Exists(path) Then File.Delete(path)
            Catch
            End Try
        End Try
    End Function

    Friend Function LoadDirectoryText(text As String) As ltfsindex.directory
        Dim index As ltfsindex = LoadText(text)
        If index Is Nothing OrElse index._directory Is Nothing OrElse index._directory.Count = 0 Then Return Nothing
        Return index._directory(0)
    End Function

    Friend Sub WriteEagerIndex(index As ltfsindex, outputPath As String)
        Using writer As NativeSchemaWriter = NativeSchemaWriter.Open(outputPath)
            writer.StartElement("ltfsindex", "version", "2.4.0")
            writer.WriteElement("creator", index.creator)
            writer.WriteElement("volumeuuid", index.volumeuuid.ToString())
            writer.WriteElement("generationnumber", index.generationnumber.ToString(CultureInfo.InvariantCulture))
            writer.WriteElement("updatetime", index.updatetime)
            WriteEagerLocation(writer, "location", index.location)
            WriteEagerLocation(writer, "previousgenerationlocation", index.previousgenerationlocation)
            writer.WriteElement("allowpolicyupdate", index.allowpolicyupdate.ToString())
            If index.dataplacementpolicy IsNot Nothing Then writer.EmptyElement("dataplacementpolicy")
            writer.WriteElement("volumelockstate", index.volumelockstate.ToString())
            writer.WriteElement("highestfileuid", index.highestfileuid.ToString(CultureInfo.InvariantCulture))
            If index._file IsNot Nothing Then
                For Each value As ltfsindex.file In index._file
                    writer.WriteFile(value)
                Next
            End If
            If index._directory IsNot Nothing Then
                For Each value As ltfsindex.directory In index._directory
                    WriteEagerDirectory(writer, value, useCollectionWrappers:=False)
                Next
            End If
            writer.EndElement("ltfsindex")
            writer.Finish()
        End Using
    End Sub

    Friend Sub WriteEagerDirectory(directory As ltfsindex.directory, outputPath As String, useCollectionWrappers As Boolean)
        Using writer As NativeSchemaWriter = NativeSchemaWriter.Open(outputPath)
            WriteEagerDirectory(writer, directory, useCollectionWrappers)
            writer.Finish()
        End Using
    End Sub

    Private Sub WriteEagerDirectory(writer As NativeSchemaWriter, directory As ltfsindex.directory, useCollectionWrappers As Boolean)
        If directory Is Nothing Then Return
        writer.StartElement("directory")
        writer.WriteElement("name", directory.name)
        writer.WriteElement("readonly", directory.readonly.ToString())
        writer.WriteElement("creationtime", directory.creationtime)
        writer.WriteElement("changetime", directory.changetime)
        writer.WriteElement("modifytime", directory.modifytime)
        writer.WriteElement("accesstime", directory.accesstime)
        writer.WriteElement("backuptime", directory.backuptime)
        writer.WriteElement("fileuid", directory.fileuid.ToString(CultureInfo.InvariantCulture))
        writer.StartElement("contents")
        If useCollectionWrappers Then writer.StartElement("_file")
        For Each value As ltfsindex.file In directory.EnumerateLazyFiles()
            writer.WriteFile(value)
        Next
        If useCollectionWrappers Then writer.EndElement("_file")
        If useCollectionWrappers Then writer.StartElement("_directory")
        For Each value As ltfsindex.directory In directory.EnumerateLazyDirectories()
            WriteEagerDirectory(writer, value, useCollectionWrappers)
        Next
        If useCollectionWrappers Then writer.EndElement("_directory")
        writer.EndElement("contents")
        writer.EndElement("directory")
    End Sub

    Private Sub WriteEagerLocation(writer As NativeSchemaWriter, name As String, value As ltfsindex.LocationDef)
        If value Is Nothing Then Return
        writer.StartElement(name)
        writer.WriteElement("partition", value.partition.ToString())
        writer.WriteElement("startblock", value.startblock.ToString(CultureInfo.InvariantCulture))
        writer.EndElement(name)
    End Sub

    Friend NotInheritable Class NativeParsedFile
        Public Property Scalars As LazyFileScalarData
        Public Property ExtendedAttributes As List(Of ltfsindex.file.xattr)
        Public Property Extents As List(Of ltfsindex.file.extent)
    End Class

    Friend Sub CheckWriterOpen(path As String, ByRef handle As IntPtr)
        Check(lsc_writer_open(path, CUInt(path.Length), handle), "open schema writer")
    End Sub

    Friend Sub WriterStart(handle As IntPtr, name As String)
        Check(lsc_writer_start(handle, name, CUInt(name.Length)), "start XML element")
    End Sub

    Friend Sub WriterStartAttribute(handle As IntPtr, name As String, attributeName As String, attributeValue As String)
        Check(lsc_writer_start_attribute(handle, name, CUInt(name.Length), attributeName, CUInt(attributeName.Length), attributeValue, CUInt(attributeValue.Length)), "start XML element with attribute")
    End Sub

    Friend Sub WriterEmpty(handle As IntPtr, name As String)
        Check(lsc_writer_empty(handle, name, CUInt(name.Length)), "write empty XML element")
    End Sub

    Friend Sub WriterEnd(handle As IntPtr, name As String)
        Check(lsc_writer_end(handle, name, CUInt(name.Length)), "end XML element")
    End Sub

    Friend Sub WriterElement(handle As IntPtr, name As String, value As String)
        Check(lsc_writer_element(handle, name, CUInt(name.Length), value, CUInt(value.Length)), "write XML element")
    End Sub

    Friend Sub WriterFile(handle As IntPtr, value As ltfsindex.file)
        Using input As New NativeFileInputOwner(value)
            Dim nativeInput As NativeFileInput = input.Input
            Check(lsc_writer_file(handle, nativeInput), "write file element")
        End Using
    End Sub

    Friend Sub WriterRaw(handle As IntPtr, value As Byte())
        If value Is Nothing OrElse value.Length = 0 Then Return
        Dim pointer As IntPtr = Marshal.AllocHGlobal(value.Length)
        Try
            Marshal.Copy(value, 0, pointer, value.Length)
            Check(lsc_writer_raw(handle, pointer, CULng(value.Length)), "write raw XML fragment")
        Finally
            Marshal.FreeHGlobal(pointer)
        End Try
    End Sub

    Friend Sub WriterStoreFileRecord(handle As IntPtr,
                                     store As IntPtr,
                                     recordOffset As Long,
                                     recordLength As Long)
        If store = IntPtr.Zero Then Throw New ArgumentNullException(NameOf(store))
        If recordOffset < 0 OrElse recordLength <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(recordLength))
        Check(lsc_writer_store_file_record(handle,
                                           store,
                                           recordOffset,
                                           CULng(recordLength)),
              "write raw schema file record")
    End Sub

    Friend Sub WriterStoreDirectoryFiles(handle As IntPtr,
                                          store As IntPtr,
                                          directoryRecordOffset As Long)
        If store = IntPtr.Zero Then Throw New ArgumentNullException(NameOf(store))
        If directoryRecordOffset < 0 Then Throw New ArgumentOutOfRangeException(NameOf(directoryRecordOffset))
        Check(lsc_writer_store_directory_files(handle,
                                               store,
                                               directoryRecordOffset),
              "write raw schema directory files")
    End Sub

    Friend Sub WriterFinish(handle As IntPtr)
        Check(lsc_writer_finish(handle), "finish schema writer")
    End Sub

    Friend Sub WriterDestroy(handle As IntPtr)
        lsc_writer_destroy(handle)
    End Sub

    Private NotInheritable Class NativeFileInputOwner
        Implements IDisposable

        Private ReadOnly _allocations As New List(Of IntPtr)
        Private ReadOnly _arrayAllocations As New List(Of Tuple(Of IntPtr, Integer))
        Public ReadOnly Input As NativeFileInput

        Public Sub New(value As ltfsindex.file)
            If value Is Nothing Then Throw New ArgumentNullException(NameOf(value))
            Dim input As New NativeFileInput With {
                .StructSize = CUInt(Marshal.SizeOf(GetType(NativeFileInput))),
                .Name = AddString(value.name),
                .Length = value.length,
                .[ReadOnly] = If(value.readonly, 1UI, 0UI),
                .OpenForWrite = If(value.openforwrite, 1UI, 0UI),
                .CreationTime = AddString(value.creationtime),
                .ChangeTime = AddString(value.changetime),
                .ModifyTime = AddString(value.modifytime),
                .AccessTime = AddString(value.accesstime),
                .BackupTime = AddString(value.backuptime),
                .FileUid = value.fileuid,
                .Symlink = AddString(value.symlink)}

            Dim xattrs As List(Of ltfsindex.file.xattr) = value.extendedattributes
            If xattrs IsNot Nothing AndAlso xattrs.Count > 0 Then
                Dim entries(xattrs.Count - 1) As NativeXattrInput
                For i As Integer = 0 To xattrs.Count - 1
                    entries(i).Key = AddString(xattrs(i).key)
                    entries(i).Value = AddString(xattrs(i).value)
                Next
                input.Xattrs = AddStructureArray(entries)
                input.XattrCount = CUInt(entries.Length)
            End If

            Dim extents As List(Of ltfsindex.file.extent) = value.extentinfo
            If extents IsNot Nothing AndAlso extents.Count > 0 Then
                Dim entries(extents.Count - 1) As NativeExtentInput
                For i As Integer = 0 To extents.Count - 1
                    entries(i) = New NativeExtentInput With {
                        .FileOffset = extents(i).fileoffset,
                        .Partition = CUInt(extents(i).partition),
                        .StartBlock = extents(i).startblock,
                        .ByteOffset = extents(i).byteoffset,
                        .ByteCount = extents(i).bytecount}
                Next
                input.Extents = AddStructureArray(entries)
                input.ExtentCount = CUInt(entries.Length)
            End If
            Me.Input = input
        End Sub

        Private Function AddString(value As String) As NativeUtf16Slice
            If value Is Nothing Then Return New NativeUtf16Slice
            Dim pointer As IntPtr = Marshal.StringToHGlobalUni(value)
            _allocations.Add(pointer)
            Return New NativeUtf16Slice With {.Pointer = pointer, .Length = CUInt(value.Length)}
        End Function

        Private Function AddStructureArray(Of T As Structure)(values As T()) As IntPtr
            Dim size As Integer = Marshal.SizeOf(GetType(T))
            Dim pointer As IntPtr = Marshal.AllocHGlobal(size * values.Length)
            _arrayAllocations.Add(Tuple.Create(pointer, values.Length))
            For i As Integer = 0 To values.Length - 1
                Marshal.StructureToPtr(values(i), IntPtr.Add(pointer, i * size), False)
            Next
            Return pointer
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            For Each allocation As Tuple(Of IntPtr, Integer) In _arrayAllocations
                If allocation.Item1 <> IntPtr.Zero Then Marshal.FreeHGlobal(allocation.Item1)
            Next
            For Each pointer As IntPtr In _allocations
                If pointer <> IntPtr.Zero Then Marshal.FreeHGlobal(pointer)
            Next
        End Sub
    End Class
End Module

Friend NotInheritable Class NativeSchemaWriter
    Implements IDisposable

    Private ReadOnly _handle As IntPtr
    Private _finished As Boolean

    Private Sub New(handle As IntPtr)
        _handle = handle
    End Sub

    Friend Shared Function Open(path As String) As NativeSchemaWriter
        Dim handle As IntPtr = IntPtr.Zero
        CheckWriterOpen(path, handle)
        Return New NativeSchemaWriter(handle)
    End Function

    Friend Sub StartElement(name As String)
        WriterStart(_handle, name)
    End Sub

    Friend Sub StartElement(name As String, attributeName As String, attributeValue As String)
        WriterStartAttribute(_handle, name, attributeName, attributeValue)
    End Sub

    Friend Sub EmptyElement(name As String)
        WriterEmpty(_handle, name)
    End Sub

    Friend Sub EndElement(name As String)
        WriterEnd(_handle, name)
    End Sub

    Friend Sub WriteElement(name As String, value As String)
        WriterElement(_handle, name, If(value, String.Empty))
    End Sub

    Friend Sub WriteFile(value As ltfsindex.file)
        WriterFile(_handle, value)
    End Sub

    Friend Sub WriteRaw(value As Byte())
        WriterRaw(_handle, value)
    End Sub

    Friend Sub WriteStoreFileRecord(store As IntPtr, recordOffset As Long, recordLength As Long)
        WriterStoreFileRecord(_handle, store, recordOffset, recordLength)
    End Sub

    Friend Sub WriteStoreDirectoryFiles(store As IntPtr, directoryRecordOffset As Long)
        WriterStoreDirectoryFiles(_handle, store, directoryRecordOffset)
    End Sub

    Friend Sub Finish()
        If _finished Then Return
        WriterFinish(_handle)
        _finished = True
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _handle <> IntPtr.Zero Then
            If Not _finished Then
                Try
                    WriterFinish(_handle)
                Catch
                End Try
            End If
            WriterDestroy(_handle)
        End If
    End Sub
End Class
