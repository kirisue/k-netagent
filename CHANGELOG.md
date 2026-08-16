# Changelog

All notable changes to K.netagent are documented in this file.

The project follows [Semantic Versioning](https://semver.org/). The public product label may use the shorter form `V0.1`, while Git tags and installer versions use `v0.1.0`.

## [Unreleased]

### Added

- explicit local PNG/JPEG attachment flow with metadata-stripping re-encoding, visual-model capability opt-in, endpoint confirmation, and request-only image payloads.
- safe VS Code workspace metadata tools for status, configured tasks, extension recommendations, and fixed official documentation links.
- structured static web snapshots with bounded heading and safe HTTPS link extraction.
- an isolated visible browser for public HTTPS text snapshots, bounded DOM summaries, and explicitly approved viewport capture handoff.
- a current-user-only VS Code named-pipe bridge for approved active-editor, diagnostics, task metadata, and installed-extension metadata reads.

### Security

- image bytes, paths, file names, hashes, and Base64 are excluded from sessions, memories, tool sessions, and audits.
- remote image input requires a redirect-free HTTPS endpoint; cleartext HTTP is limited to loopback, and query/fragment endpoints are rejected.
- image requests use an immutable byte snapshot, are never automatically retried, and are bounded to four model requests per Agent turn.
- raw `.vscode` and `*.code-workspace` configuration is hidden from generic file tools; dedicated tools omit command, arguments, environment, inputs, and setting values.
- browser content is rendered as encoded static text with scripts, remote resources, navigation, login state, downloads, permissions, and new windows blocked.
- viewport capture is classified as `SensitiveCapture`; pixels remain private to the browser session and transfer only once when the user explicitly attaches the capture to the next model turn.
- VS Code live pairing uses random in-memory credentials, mutual HMAC authentication, bounded frames, exact trusted-workspace matching, and a read-only metadata allowlist.
- VS Code diagnostics omit free-form messages, task metadata is limited to the paired workspace, pairing clipboard data is cleared on successful connection, the bridge listener never captures the WPF Dispatcher context, and authenticated heartbeats detect idle disconnects before accepting a fresh pairing.

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
