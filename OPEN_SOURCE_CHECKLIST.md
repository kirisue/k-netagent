# GitHub open-source checklist

## Repository profile

Recommended repository name:

```text
k-netagent
```

Recommended GitHub Description:

```text
K.netagent is a Windows-native single-agent desktop app built with .NET 10 and WPF, featuring streaming chat, cross-session memory, controlled tools, task recovery, and human approval.
```

Recommended topics:

```text
dotnet wpf windows ai-agent llm openai-compatible desktop-app memory task-automation
```

## Create the repository

1. Create a new empty public GitHub repository. Do not ask GitHub to generate a README, license, or `.gitignore`; this project already includes them.
2. Start from a clean copy of this working tree that excludes `.git`, `artifacts`, `dist`, `memory`, `bin`, `obj`, logs, sessions, and local configuration.
3. Initialize a new Git history in that clean copy. Do not push the current `kirisue/v4-agent` history because it belongs to the former Python project.
4. Make the first commit, push `main`, then enable branch protection requiring the Windows CI job.

## Repository settings

- Enable Issues and private vulnerability reporting.
- Enable Dependabot security updates.
- Require pull requests and the CI check before merging to `main`.
- Keep workflow permissions minimal; the release workflow needs `contents: write` only for version tags.
- Add an Authenticode certificate later if public releases must avoid the Windows SmartScreen unknown-publisher warning.

## First release

1. Confirm `dotnet test TestAgent.slnx -c Release` passes.
2. Push tag `v0.1.0` from the clean repository.
3. Verify the release contains only `k-netagent-0.1.0-win-x64-setup.exe` and its SHA-256 file.
4. Download the release on a clean Windows 10/11 machine and verify install, launch, provider setup, chat, restart recovery, and uninstall.
