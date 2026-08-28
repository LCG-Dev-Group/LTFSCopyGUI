[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installerDirectory = $PSScriptRoot
$releaseDirectory = [System.IO.Path]::GetFullPath((Join-Path $installerDirectory '..\LTFSCopyGUI\bin\x64\Release'))
$deployDirectory = [System.IO.Path]::GetFullPath((Join-Path $installerDirectory 'deploy'))

if (-not [System.IO.Directory]::Exists($releaseDirectory)) {
    throw "Release output directory does not exist: $releaseDirectory"
}

if (-not [System.IO.Directory]::Exists($deployDirectory)) {
    throw "Installer deploy directory does not exist: $deployDirectory"
}

if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $archivePath = [System.IO.Path]::GetFullPath($OutputPath)
} else {
    $archivePath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputPath))
}

$archiveDirectory = Split-Path -Parent $archivePath
if (-not [System.IO.Directory]::Exists($archiveDirectory)) {
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
}

$stagingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "LTFSCopyGUI-portable-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

function Copy-PayloadFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string] $DestinationDirectory,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]] $ExcludedTopLevelDirectories
    )

    $sourceRoot = (Get-Item -LiteralPath $SourceDirectory).FullName.TrimEnd('\')

    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse) {
        $relativePath = $sourceFile.FullName.Substring($sourceRoot.Length + 1)
        $topLevelName = $relativePath.Split('\')[0]

        if ($ExcludedTopLevelDirectories -contains $topLevelName) {
            continue
        }

        $destinationFile = Join-Path $DestinationDirectory $relativePath
        $destinationParent = Split-Path -Parent $destinationFile

        if (-not [System.IO.Directory]::Exists($destinationParent)) {
            New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        }

        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationFile -Force
    }
}

try {
    # Match the [Files] entries in LCG.iss for a fresh install. These Release
    # directories are supplied by deploy instead, so avoid copying them twice.
    Copy-PayloadFiles -SourceDirectory $releaseDirectory `
        -DestinationDirectory $stagingDirectory `
        -ExcludedTopLevelDirectories @('config', 'log', 'logpages', 'schema')

    # The default ISS task state keeps the PsExec file's disabled filename.
    Copy-PayloadFiles -SourceDirectory $deployDirectory `
        -DestinationDirectory $stagingDirectory `
        -ExcludedTopLevelDirectories @()

    foreach ($requiredFile in @('LTFSCopyGUI.exe', 'ltfscopy_fastreader.dll')) {
        $requiredPath = Join-Path $stagingDirectory $requiredFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Portable payload is missing required file: $requiredFile"
        }
    }

    Compress-Archive -Path (Join-Path $stagingDirectory '*') `
        -DestinationPath $archivePath `
        -CompressionLevel Optimal `
        -Force
} finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    throw "Portable archive was not created: $archivePath"
}

Write-Host "Created portable archive: $archivePath"
