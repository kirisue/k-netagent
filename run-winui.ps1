[CmdletBinding()]
param(
    [ValidateSet('chat', 'events', 'services')]
    [string]$Page = 'chat',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnetCommand) {
    Write-Error 'dotnet was not found. This source-code launcher requires a .NET 10 SDK on PATH. The installed K.netagent Preview app does not require an SDK.' -ErrorAction Continue
    exit 127
}

$project = Join-Path $PSScriptRoot 'src\KNetAgent.Desktop.WinUI\KNetAgent.Desktop.WinUI.csproj'
$runArguments = @('run', '--project', $project, '--configuration', $Configuration)
if ($Page -ne 'chat') { $runArguments += @('--', '--page', $Page.ToLowerInvariant()) }

# Preserve dotnet's exit code even when the caller enables PowerShell 7's
# conversion of native nonzero exit codes into terminating errors.
$PSNativeCommandUseErrorActionPreference = $false
& $dotnetCommand.Source @runArguments
exit $LASTEXITCODE
