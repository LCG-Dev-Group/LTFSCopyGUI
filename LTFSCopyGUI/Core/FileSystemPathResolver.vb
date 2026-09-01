Imports System.IO

Public Enum ExistingPathKind
    Any
    File
    Directory
End Enum

Public Module FileSystemPathResolver
    Public Function ResolveExistingFilePath(path As String) As String
        Return ResolveExistingPath(path, ExistingPathKind.File)
    End Function

    Public Function ResolveExistingDirectoryPath(path As String) As String
        Return ResolveExistingPath(path, ExistingPathKind.Directory)
    End Function

    Public Function ResolveExistingPath(path As String,
                                        Optional kind As ExistingPathKind = ExistingPathKind.Any) As String
        If String.IsNullOrWhiteSpace(path) Then Return path

        Dim candidate As String
        Try
            candidate = System.IO.Path.GetFullPath(path)
        Catch
            Return path
        End Try

        ' This is the common path.  It also preserves the caller's exact case,
        ' which is important when two case-only names coexist.
        If IsExisting(candidate, kind) Then Return candidate

        Try
            Dim root As String = System.IO.Path.GetPathRoot(candidate)
            If String.IsNullOrEmpty(root) OrElse candidate.Length <= root.Length Then Return candidate

            Dim current As String = root
            Dim relative As String = candidate.Substring(root.Length)
            Dim parts() As String = relative.Split(
                New Char() {System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar},
                StringSplitOptions.RemoveEmptyEntries)

            If parts.Length = 0 Then Return candidate

            For i As Integer = 0 To parts.Length - 1
                Dim childKind As ExistingPathKind = If(i = parts.Length - 1,
                                                        kind,
                                                        ExistingPathKind.Directory)
                Dim child As FileSystemInfo = FindChild(current, parts(i), childKind)
                If child Is Nothing Then Return candidate
                current = child.FullName
            Next

            Return current
        Catch
            ' Keep the original path so the caller receives the normal I/O
            ' exception instead of a path-resolution implementation detail.
            Return candidate
        End Try
    End Function

    Private Function IsExisting(path As String, kind As ExistingPathKind) As Boolean
        Select Case kind
            Case ExistingPathKind.File
                Return File.Exists(path)
            Case ExistingPathKind.Directory
                Return Directory.Exists(path)
            Case Else
                Return File.Exists(path) OrElse Directory.Exists(path)
        End Select
    End Function

    Private Function FindChild(parentPath As String,
                               requestedName As String,
                               kind As ExistingPathKind) As FileSystemInfo
        Dim caseInsensitiveMatch As FileSystemInfo = Nothing
        Dim caseInsensitiveMatchCount As Integer = 0
        Dim exactNameWithWrongKind As Boolean = False

        For Each entry As FileSystemInfo In New DirectoryInfo(parentPath).EnumerateFileSystemInfos()
            Dim exactName As Boolean = String.Equals(entry.Name, requestedName, StringComparison.Ordinal)
            If exactName AndAlso Not MatchesKind(entry, kind) Then
                exactNameWithWrongKind = True
                Continue For
            End If

            If Not MatchesKind(entry, kind) Then Continue For
            If exactName Then Return entry

            If String.Equals(entry.Name, requestedName, StringComparison.OrdinalIgnoreCase) Then
                caseInsensitiveMatch = entry
                caseInsensitiveMatchCount += 1
            End If
        Next

        If exactNameWithWrongKind OrElse caseInsensitiveMatchCount <> 1 Then Return Nothing
        Return caseInsensitiveMatch
    End Function

    Private Function MatchesKind(entry As FileSystemInfo, kind As ExistingPathKind) As Boolean
        Select Case kind
            Case ExistingPathKind.File
                Return Not entry.Attributes.HasFlag(FileAttributes.Directory)
            Case ExistingPathKind.Directory
                Return entry.Attributes.HasFlag(FileAttributes.Directory)
            Case Else
                Return True
        End Select
    End Function
End Module
