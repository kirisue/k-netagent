# Changelog

All notable changes to K.netagent are documented in this file.

The project follows [Semantic Versioning](https://semver.org/). The public product label may use the shorter form `V0.1`, while Git tags and installer versions use `v0.1.0`.

## [Unreleased]

### Added

- explicit local PNG/JPEG attachment flow with metadata-stripping re-encoding, visual-model capability opt-in, endpoint confirmation, and request-only image payloads.
- safe VS Code workspace metadata tools for status, configured tasks, extension recommendations, and fixed official documentation links.
- structured static web snapshots with bounded heading and safe HTTPS link extraction.

### Security

- image bytes, paths, file names, hashes, and Base64 are excluded from sessions, memories, tool sessions, and audits.
- remote image input requires a redirect-free HTTPS endpoint; cleartext HTTP is limited to loopback, and query/fragment endpoints are rejected.
- image requests use an immutable byte snapshot, are never automatically retried, and are bounded to four model requests per Agent turn.
- raw `.vscode` and `*.code-workspace` configuration is hidden from generic file tools; dedicated tools omit command, arguments, environment, inputs, and setting values.

This section is development work only. No V0.2 tag or release has been published.

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
