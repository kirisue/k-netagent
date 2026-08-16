[CmdletBinding()]
param(
    [switch]$SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$extensionRoot = Join-Path $repositoryRoot 'vscode-extension'
$manifestPath = Join-Path $extensionRoot 'package.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$outputDirectory = Join-Path $repositoryRoot 'artifacts\vscode-extension'
$outputFile = Join-Path $outputDirectory "$($manifest.name)-$($manifest.version).vsix"
$vsceCommand = Join-Path $extensionRoot 'node_modules\.bin\vsce.cmd'
$sourceLicense = Join-Path $repositoryRoot 'LICENSE'
$extensionLicense = Join-Path $extensionRoot 'LICENSE'
$temporaryLicense = $false

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

Push-Location $extensionRoot
try {
    if (-not $SkipRestore) {
        & npm.cmd ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
    }

    if (-not (Test-Path -LiteralPath $vsceCommand -PathType Leaf)) {
        throw 'The pinned @vscode/vsce package is missing. Run without -SkipRestore first.'
    }

    & npm.cmd test
    if ($LASTEXITCODE -ne 0) { throw "npm test failed with exit code $LASTEXITCODE." }

    if (-not (Test-Path -LiteralPath $extensionLicense -PathType Leaf)) {
        if (-not (Test-Path -LiteralPath $sourceLicense -PathType Leaf)) {
            throw 'The repository MIT LICENSE file is missing.'
        }
        Copy-Item -LiteralPath $sourceLicense -Destination $extensionLicense
        $temporaryLicense = $true
    }

    & $vsceCommand package --no-dependencies --out $outputFile
    if ($LASTEXITCODE -ne 0) { throw "vsce package failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
    if ($temporaryLicense -and (Test-Path -LiteralPath $extensionLicense -PathType Leaf)) {
        Remove-Item -LiteralPath $extensionLicense -Force
    }
}

& (Join-Path $PSScriptRoot 'Test-VsCodeVsix.ps1') -Path $outputFile
Write-Host "Local VSIX ready: $outputFile"
