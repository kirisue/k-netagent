# Contributing

K.netagent is a Windows-native .NET 10/WPF project. Keep contributions focused on the public single-Agent edition and preserve its approval, workspace, secret-storage, and recovery boundaries.

## Development

```powershell
dotnet restore TestAgent.slnx
dotnet build TestAgent.slnx
dotnet test TestAgent.slnx
.\run-dotnet.ps1
```

Before opening a pull request:

- Add or update tests for behavior changes.
- Do not commit API keys, local sessions, memory, audit logs, build artifacts, or installer output.
- Do not weaken approval gates or workspace path checks to make a test pass.
- Keep ToolSession as tool telemetry; do not turn it into a hidden sub-Agent.
- Explain new network, process, filesystem, or persistence permissions in the pull request.

The Windows installer is built with:

```powershell
.\build-installer.ps1 -Version 0.1.0 -InstallBuildTools
```

Inno Setup is a build-time dependency only. End users receive a self-contained Setup executable and do not need to install .NET or Python.
