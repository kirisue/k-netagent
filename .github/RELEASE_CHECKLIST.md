# Release checklist

- [ ] Release version and `CHANGELOG.md` are updated.
- [ ] `global.json` and CI use a supported .NET SDK with the latest security runtime.
- [ ] Release tests pass on Windows.
- [ ] The self-contained installer builds from a clean staging directory.
- [ ] Installer SHA-256 sidecar matches the generated Setup executable.
- [ ] Install, launch, window-response, and uninstall smoke tests pass.
- [ ] No secrets, local sessions, memory, logs, or generated artifacts are committed.
- [ ] The release comes from the new clean Git history, not the former Python repository.
- [ ] SmartScreen/code-signing status is stated in the release notes.
