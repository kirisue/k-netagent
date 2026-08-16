[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
if ([System.IO.Path]::GetExtension($resolvedPath) -ne '.vsix') {
    throw "Expected a .vsix file: $resolvedPath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\\', '/') })
    $requiredEntries = @(
        'extension.vsixmanifest',
        'extension/package.json',
        'extension/extension.js',
        'extension/protocol.js',
        'extension/workspacePolicy.js',
        'extension/LICENSE.txt'
    )

    foreach ($requiredEntry in $requiredEntries) {
        if ($entryNames -notcontains $requiredEntry) {
            throw "VSIX is missing required entry '$requiredEntry'."
        }
    }

    $forbiddenPath = '(?i)(^|/)(test|tests|node_modules|secrets?|credentials?|\.git|\.github)(/|$)|(^|/)(package-lock\.json|\.env(?:\..*)?|[^/]+\.(?:key|pem|p12|pfx))$'
    $forbiddenEntries = @($entryNames | Where-Object { $_ -match $forbiddenPath })
    if ($forbiddenEntries.Count -gt 0) {
        throw "VSIX contains forbidden development or credential paths: $($forbiddenEntries -join ', ')"
    }

    $duplicateEntries = @($entryNames | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    if ($duplicateEntries.Count -gt 0) {
        throw "VSIX contains duplicate paths: $($duplicateEntries -join ', ')"
    }

    $expandedBytes = ($archive.Entries | Measure-Object -Property Length -Sum).Sum
    if ($expandedBytes -gt 2MB) {
        throw "VSIX expanded content is unexpectedly large: $expandedBytes bytes."
    }

    Write-Host "Verified VSIX: $resolvedPath"
    Write-Host "Entries: $($entryNames.Count); expanded bytes: $expandedBytes"
}
finally {
    $archive.Dispose()
}
