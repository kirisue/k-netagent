# Changelog

All notable changes to K.netagent are documented in this file.

The project follows [Semantic Versioning](https://semver.org/). The public product label may use the shorter form `V0.1`, while Git tags and installer versions use `v0.1.0`.

## [0.1.0] - 2026-08-16

### Added

- Windows-native .NET 10/WPF single-Agent desktop application.
- OpenAI-compatible streaming chat and provider configuration.
- persistent sessions and User, Project, and Session memory scopes.
- controlled filesystem, patch, command, web, background-job, and memory tools.
- bounded task graphs, checkpoints, interruption recovery, and human approval gates.
- self-contained win-x64 installer, CI, release workflow, security policy, and contribution guide.

### Security

- API keys are protected with Windows DPAPI for the current user.
- sensitive files, path escapes, reparse points, private network destinations, and unsafe command recipes are blocked.
