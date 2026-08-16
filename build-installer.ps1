[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',
    [switch]$SkipTests,
    [switch]$InstallBuildTools,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot ('staging\win-x64-' + [Guid]::NewGuid().ToString('N'))))
$installerOutput = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'installer'))
$project = Join-Path $repoRoot 'src\TestAgent.Desktop\TestAgent.Desktop.csproj'
$solution = Join-Path $repoRoot 'TestAgent.slnx'
$innoScript = Join-Path $repoRoot 'installer\KNetAgent.iss'
$versionInfo = ([regex]::Match($Version, '^\d+\.\d+\.\d+')).Value + '.0'

function Find-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    $candidates = @(
        $command.Source,
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    return $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Remove-StagingDirectory([string]$Path) {
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        try {
            [IO.Directory]::Delete($Path, $true)
            return
        }
        catch {
            if ($attempt -eq 8) {
                Write-Warning "Installer was created, but temporary staging could not be removed: $Path ($($_.Exception.Message))"
                return
            }
            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
}

if (-not $SkipTests) {
    dotnet test $solution -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

try {
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false `
        -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $publishRoot --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'KNetAgent.exe'))) {
        throw 'Self-contained publish did not produce KNetAgent.exe.'
    }

    $iscc = Find-InnoCompiler
    if (-not $iscc -and $InstallBuildTools) {
        winget install --id JRSoftware.InnoSetup -e --scope user --source winget `
            --accept-package-agreements --accept-source-agreements --silent
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup installation failed with exit code $LASTEXITCODE." }
        $iscc = Find-InnoCompiler
    }
    if (-not $iscc) {
        throw 'Inno Setup 6 was not found. Re-run with -InstallBuildTools, or install JRSoftware.InnoSetup for the build account.'
    }

    & $iscc "/DMyAppVersion=$Version" "/DMyVersionInfo=$versionInfo" "/DPublishDir=$publishRoot" "/DOutputDir=$installerOutput" "/DRepoRoot=$repoRoot" $innoScript
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }

    $setup = Join-Path $installerOutput "k-netagent-$Version-win-x64-setup.exe"
    if (-not (Test-Path -LiteralPath $setup)) { throw "Expected installer was not created: $setup" }
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashFile = $setup + '.sha256'
    Set-Content -LiteralPath $hashFile -Value "$hash  $([IO.Path]::GetFileName($setup))" -Encoding ascii
    Write-Output "Installer: $setup"
    Write-Output "SHA256:   $hash"
}
finally {
    if (-not $KeepStaging -and (Test-Path -LiteralPath $publishRoot)) {
        $requiredPrefix = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'staging')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $publishRoot.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected staging path: $publishRoot"
        }
        Remove-StagingDirectory $publishRoot
    }
}
