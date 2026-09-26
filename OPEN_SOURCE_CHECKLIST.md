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

## Existing repository and version boundary

The .NET repository already exists at `https://github.com/kirisue/k-netagent`.
Do not reinitialize Git, replace its history, or push the former Python repository's
history into it. The historical first release is WPF `v0.1.0`.

The current WinUI artifact is a local `0.1.1-preview.20260926` installer, not an
already published release. The shorthand `0.11` must be clarified before choosing
an official tag: `0.1.1` and `0.11.0` are different versions. Local packaging does
not authorize a commit, push, tag, or GitHub Release.

Before preparing the next commit:

- Review both tracked modifications and untracked source files; do not omit the
  WinUI project, Windows diagnostic services, or their tests.
- Exclude `artifacts`, `dist`, user memory, sessions, logs, credentials, `bin` and
  `obj`. Keep generated EXEs as release assets rather than Git source files.
- Review the complete proposed diff and run a secret scan. Pattern matching alone
  is not proof that a repository contains no sensitive data.
- Preserve the existing WPF release and clearly label the WinUI preview and its
  unsupported features in README and release notes.

## Repository settings

- Enable Issues and private vulnerability reporting.
- Enable Dependabot security updates.
- Require pull requests and the CI check before merging to `main`.
- Keep workflow permissions minimal; the release workflow needs `contents: write` only for version tags.
- Plan Authenticode signing to identify the publisher and build signing reputation; signing a new file does not guarantee that SmartScreen will show no warning.

## Next WinUI preview release gates

1. Confirm the exact version and obtain explicit publishing authorization.
2. Align source version metadata, installer version, changelog and the intended
   tag. Do not silently promote the preview to a stable release.
3. Update and review the release workflow before pushing any `v*` tag. The current
   workflow defaults to WPF, only collects the installer root directory, and does
   not pass `--prerelease`; it is not the WinUI preview publishing path.
4. Run the tests and inspect the TRX counters to prove that tests actually executed.
   Build WinUI XAML in Release and package with `-Desktop WinUI`. Validate bundled
   .NET / Windows App SDK files and the matching XBF / PRI resources.
5. Verify the final EXE hash, install, launch both diagnostic shortcuts, reinstall,
   uninstall, and preservation of user data. Keep the preview's product identity
   separate from WPF; shared `%LOCALAPPDATA%\TestAgent` data still needs a
   cross-version compatibility test.
6. Perform clean-machine and real-provider GUI acceptance separately. A test pass,
   a live process, or an accessibility tree alone does not prove the complete GUI
   works. Follow `docs/winui3-ui-acceptance.md` and report missing evidence.
7. Document the unsigned-installer warning and the browser's Evergreen WebView2
   Runtime dependency. .NET and Windows App SDK are bundled; Git and the .NET SDK
   remain optional dependencies for development-related features.
8. Upload only the approved installer and checksum, plus deliberately reviewed
   release metadata. Local validation manifests may contain machine paths and
   must not be uploaded without review.

No commit, tag, push, or release is performed by this checklist.
