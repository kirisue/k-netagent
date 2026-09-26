[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ExpectedRuntimeVersion = '10.0.12',
    [string]$BuildOutputDirectory,
    [string]$AssetsFile
)

$ErrorActionPreference = 'Stop'
$publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
$requiredFiles = @(
    'KNetAgent.Desktop.WinUI.exe', 'KNetAgent.Desktop.WinUI.dll',
    'KNetAgent.Desktop.WinUI.runtimeconfig.json', 'KNetAgent.Desktop.WinUI.deps.json',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll',
    'Microsoft.ui.xaml.dll', 'Microsoft.UI.Xaml.Controls.dll',
    'Microsoft.WindowsAppRuntime.dll', 'Microsoft.WindowsAppRuntime.pri',
    'Microsoft.UI.Xaml.Controls.pri', 'Microsoft.Windows.ApplicationModel.Resources.dll',
    'WinRT.Runtime.dll', 'WebView2Loader.dll',
    'KNetAgent.Desktop.WinUI.pri', 'App.xbf', 'MainWindow.xbf', 'Themes\KNetTheme.xbf'
)
foreach ($relativePath in $requiredFiles) {
    $file = Join-Path $publishRoot $relativePath
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Incomplete WinUI publish: missing or empty $relativePath."
    }
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishRoot 'KNetAgent.Desktop.WinUI.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks) {
    throw 'WinUI publish is framework-dependent; an installed .NET runtime would be required.'
}
$runtime = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')
if ($runtime.Count -ne 1 -or $runtime[0].version -ne $ExpectedRuntimeVersion) {
    throw "Expected self-contained Microsoft.NETCore.App $ExpectedRuntimeVersion in runtimeconfig.json."
}

$dependencies = Get-Content -LiteralPath (Join-Path $publishRoot 'KNetAgent.Desktop.WinUI.deps.json') -Raw | ConvertFrom-Json
$dependencyNames = @($dependencies.libraries.PSObject.Properties.Name)
if ($dependencyNames -notcontains "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/$ExpectedRuntimeVersion") {
    throw "The dependency manifest does not contain the win-x64 $ExpectedRuntimeVersion runtime pack."
}
if (-not ($dependencyNames | Where-Object { $_ -like 'Microsoft.WindowsAppSDK.WinUI/*' })) {
    throw 'The dependency manifest does not contain Microsoft.WindowsAppSDK.WinUI.'
}

$verifiedResourceCount = 0
if ($BuildOutputDirectory) {
    $buildRoot = [IO.Path]::GetFullPath($BuildOutputDirectory).TrimEnd('\')
    $resources = @(Get-ChildItem -LiteralPath $buildRoot -Recurse -File -Filter '*.xbf')
    $resources += Get-Item -LiteralPath (Join-Path $buildRoot 'KNetAgent.Desktop.WinUI.pri')
    foreach ($resource in $resources) {
        $relativePath = $resource.FullName.Substring($buildRoot.Length + 1)
        $publishedResource = Join-Path $publishRoot $relativePath
        if (-not (Test-Path -LiteralPath $publishedResource -PathType Leaf)) {
            throw "Compiled WinUI resource was not published: $relativePath."
        }
        if ((Get-FileHash -LiteralPath $resource.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $publishedResource -Algorithm SHA256).Hash) {
            throw "Compiled WinUI resource differs from the build output: $relativePath."
        }
        $verifiedResourceCount++
    }
}

$runtimePackHashVerified = $false
if ($AssetsFile) {
    $assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
    $runtimePack = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
        Join-Path $_ "microsoft.netcore.app.runtime.win-x64\$ExpectedRuntimeVersion\runtimes\win-x64\native"
    } | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
    if (-not $runtimePack) { throw "Restored .NET $ExpectedRuntimeVersion native runtime pack was not found." }
    foreach ($nativeRuntimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
        if ((Get-FileHash -LiteralPath (Join-Path $runtimePack $nativeRuntimeFile) -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $publishRoot $nativeRuntimeFile) -Algorithm SHA256).Hash) {
            throw "Published $nativeRuntimeFile does not match the restored $ExpectedRuntimeVersion runtime pack."
        }
    }
    $runtimePackHashVerified = $true
}

$files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File)
[pscustomobject]@{
    publishDirectory = $publishRoot
    runtimeVersion = $runtime[0].version
    coreClrFileVersion = (Get-Item -LiteralPath (Join-Path $publishRoot 'coreclr.dll')).VersionInfo.FileVersion
    runtimePackHashVerified = $runtimePackHashVerified
    windowsAppSdkDependencies = @($dependencyNames | Where-Object { $_ -like 'Microsoft.WindowsAppSDK.*/*' })
    requiredFilesChecked = $requiredFiles.Count
    compiledResourcesHashVerified = $verifiedResourceCount
    publishedFileCount = $files.Count
    publishedBytes = ($files | Measure-Object -Property Length -Sum).Sum
    webView2 = 'Loader bundled; browser feature uses the separately serviced Evergreen WebView2 Runtime.'
}
