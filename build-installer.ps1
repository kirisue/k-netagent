[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',
    [ValidateSet('Wpf', 'WinUI')]
    [string]$Desktop = 'Wpf',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$RuntimeFrameworkVersion = '10.0.12',
    [switch]$SkipTests,
    [switch]$InstallBuildTools,
    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot ('staging\' + $Desktop.ToLowerInvariant() + '-win-x64-' + [Guid]::NewGuid().ToString('N'))))
$publishRoot = Join-Path $stagingRoot 'publish'
$buildRoot = Join-Path $stagingRoot 'build'
$installerOutput = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'installer'))
$project = Join-Path $repoRoot 'src\TestAgent.Desktop\TestAgent.Desktop.csproj'
$solution = Join-Path $repoRoot 'TestAgent.slnx'
$innoScript = Join-Path $repoRoot 'installer\KNetAgent.iss'
$appExeName = 'KNetAgent.exe'
$setupFileName = "k-netagent-$Version-win-x64-setup.exe"
if ($Desktop -eq 'WinUI') {
    $project = Join-Path $repoRoot 'src\KNetAgent.Desktop.WinUI\KNetAgent.Desktop.WinUI.csproj'
    $innoScript = Join-Path $repoRoot 'installer\KNetAgent.WinUI.Preview.iss'
    $appExeName = 'KNetAgent.Desktop.WinUI.exe'
    $installerOutput = Join-Path $installerOutput 'winui-preview'
    $setupFileName = "k-netagent-$Version-winui-win-x64-setup.exe"
}
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
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $requiredPrefix = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'staging')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected staging path: $resolvedPath"
    }
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        try {
            Remove-Item -LiteralPath $resolvedPath -Recurse -Force
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

if (-not $SkipTests) {
    $testResults = Join-Path $stagingRoot 'tests'
    dotnet test $solution -c Release --nologo --logger trx --results-directory $testResults
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
    $testReports = @(Get-ChildItem -LiteralPath $testResults -Filter '*.trx' -File -ErrorAction SilentlyContinue)
    if ($testReports.Count -eq 0) { throw 'Test command returned without a TRX report; refusing to package unverified code.' }
    foreach ($testReport in $testReports) {
        [xml]$testXml = Get-Content -LiteralPath $testReport.FullName -Raw
        $counters = $testXml.TestRun.ResultSummary.Counters
        if ([int]$counters.executed -lt 1 -or [int]$counters.failed -gt 0 -or [int]$counters.error -gt 0) {
            throw "Tests did not execute successfully: $($testReport.FullName)."
        }
    }
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

try {
    $publishArguments = @(
        'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '--artifacts-path', $buildRoot,
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', "-p:Version=$Version",
        '-o', $publishRoot, '--nologo'
    )
    if ($Desktop -eq 'WinUI') {
        $publishArguments += @('-p:WindowsAppSDKSelfContained=true', "-p:KNetPublishRuntimeVersion=$RuntimeFrameworkVersion")
    }
    else { $publishArguments += "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion" }
    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $appExeName))) {
        throw "Self-contained publish did not produce $appExeName."
    }

    $publishValidation = $null
    if ($Desktop -eq 'WinUI') {
        # The unpackaged WinUI targets emit XBF/PRI to TargetDir, but dotnet publish
        # does not include the app's compiled resources in ResolvedFileToPublish.
        $desktopBuildOutput = Join-Path $buildRoot 'bin\KNetAgent.Desktop.WinUI\release_win-x64'
        $resourceFiles = @(Get-ChildItem -LiteralPath $desktopBuildOutput -Recurse -File -Filter '*.xbf')
        $resourceFiles += Get-Item -LiteralPath (Join-Path $desktopBuildOutput 'KNetAgent.Desktop.WinUI.pri')
        foreach ($resourceFile in $resourceFiles) {
            $relativePath = $resourceFile.FullName.Substring($desktopBuildOutput.TrimEnd('\').Length + 1)
            $destination = Join-Path $publishRoot $relativePath
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
            Copy-Item -LiteralPath $resourceFile.FullName -Destination $destination -Force
        }
        $publishValidation = & (Join-Path $repoRoot 'scripts\Test-WinUiPublish.ps1') `
            -PublishDirectory $publishRoot -ExpectedRuntimeVersion $RuntimeFrameworkVersion `
            -BuildOutputDirectory $desktopBuildOutput `
            -AssetsFile (Join-Path $buildRoot 'obj\KNetAgent.Desktop.WinUI\project.assets.json')
    }

    & $iscc "/DMyAppVersion=$Version" "/DMyVersionInfo=$versionInfo" "/DPublishDir=$publishRoot" "/DOutputDir=$installerOutput" "/DRepoRoot=$repoRoot" $innoScript
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }

    $setup = Join-Path $installerOutput $setupFileName
    if (-not (Test-Path -LiteralPath $setup)) { throw "Expected installer was not created: $setup" }
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashFile = $setup + '.sha256'
    Set-Content -LiteralPath $hashFile -Value "$hash  $([IO.Path]::GetFileName($setup))" -Encoding ascii
    $manifest = [ordered]@{
        desktop = $Desktop
        version = $Version
        runtimeFrameworkVersion = $RuntimeFrameworkVersion
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
        installer = $setup
        sha256 = $hash
        publishDirectory = $publishRoot
        stagingRetained = [bool]$KeepStaging
        testsSkipped = [bool]$SkipTests
        validation = $publishValidation
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($setup + '.json') -Encoding utf8
    Write-Output "Installer: $setup"
    Write-Output "SHA256:   $hash"
    if ($KeepStaging) { Write-Output "Staging:  $stagingRoot" }
}
finally {
    if (-not $KeepStaging -and (Test-Path -LiteralPath $stagingRoot)) {
        Remove-StagingDirectory $stagingRoot
    }
}
