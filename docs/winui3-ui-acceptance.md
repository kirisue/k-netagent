# WinUI 3 UI acceptance gates

This checklist separates acceptance of the local WinUI Preview installer from the stronger evidence required before WinUI can replace the formal WPF desktop entry point. WPF v0.1.0 remains the published entry point. A working Preview package does not authorize a tag, GitHub Release, or replacement of that product.

## Automated, desktop-independent gates

The following tests must run with `dotnet test` on a Windows CI runner without clicking or displaying a window. UI state belongs in a platform-neutral view model; WinUI dispatching and dialogs are injected behind interfaces.

### Thread list and selection

- Initialization loads sessions in the store's ordering and selects a deterministic session.
- An empty store creates and persists one session.
- Creating a thread persists it, selects it, and exposes an empty message list.
- Selecting another thread replaces the visible message projection without duplicating messages.
- Clearing a thread persists an empty history and leaves the thread selected.
- Commands that mutate a thread are disabled while a generation is active.

### Streaming and terminal states

- `Content` events append in arrival order; `Revision` replaces the current streamed body.
- `Reasoning` remains separate from the answer and is collapsed by default in the view.
- Tool start/completion events update status without becoming assistant text.
- Completion removes the ephemeral streaming card after the persisted assistant message is projected.
- Cancellation ends in `Cancelled`, clears busy state, enables the composer, and permits another send.
- Failure exposes an understandable error, clears busy state, and never leaves a phantom assistant message.
- Token usage is visible after completion and does not carry into the next request before new usage arrives.

### Composer

- Send is disabled without a selected thread, with an empty draft, and while busy.
- Stop is enabled only while a request is active.
- Sending trims the submitted text, clears the consumed draft, and uses one immutable provider-settings snapshot.
- A failed or declined attachment/approval does not consume the user's draft.
- Enter-to-send and Shift+Enter-to-newline are view-level tests; the handler must call the same send command instead of duplicating send logic.

### Inspector and approval

- Inspector tab selection is stable while streaming and while switching threads.
- Closing the inspector never changes the selected thread or draft.
- Memory and tool-session collections refresh independently of chat messages.
- A tool approval carries request id, tool name, risk level, and summary unchanged to the interaction service.
- The non-interactive/default interaction implementation denies approval.
- Dismiss, cancellation, or interaction failure must not approve a tool.
- `NeedsReview` acknowledgement requires an explicit command and does not happen during initialization.

### Responsive state

- Wide state presents thread rail, conversation, and inspector simultaneously.
- Narrow state preserves the conversation and composer while moving the thread rail and inspector behind navigation/drawer affordances.
- State changes preserve selected thread, draft, streaming content, inspector tab, and pending approval.
- Responsive behavior is represented by named XAML visual states or an independently testable width classifier; it must not be implemented by recreating the view model.

## Build and static UI gates

Run from the repository root:

```powershell
dotnet restore TestAgent.slnx
dotnet build TestAgent.slnx -c Release --no-restore
dotnet test TestAgent.slnx -c Release --no-build --no-restore
```

The WinUI project must compile XAML in Release. A static check should additionally require:

- a thread list bound to the shell view model;
- a virtualized message list;
- a composer bound to Send and Stop commands;
- a named narrow and wide responsive state;
- an inspector that can be closed or moved into a drawer;
- no `System.Windows`, WPF `Dispatcher`, or WPF `MessageBox` reference in the WinUI project;
- no API key, session content, or memory content embedded in generated XAML or build output.

XAML compilation verifies markup and binding syntax that can be checked without starting a compositor. It does **not** prove rendering, focus, keyboard, DPI, accessibility, or WebView2 behavior.

## Interactive Windows smoke test

Run this small test on Windows 10 and Windows 11 in an interactive desktop session:

1. Launch from a clean user profile and create two threads.
2. Send a response from a local fake SSE endpoint; observe at least three content chunks and stop a later response midway.
3. Verify Enter sends, Shift+Enter inserts a newline, focus returns to the composer, and the list follows new output without trapping manual scrollback.
4. Resize across the wide/narrow breakpoint and confirm the central conversation and draft survive.
5. Open every inspector tab and reject one tool approval; confirm no tool execution occurs.
6. Close and reopen the app; verify thread history and non-secret settings restore.
7. Check 100%, 150%, and 200% scale, keyboard-only navigation, high contrast, and screen-reader names for icon-only buttons.

Do not claim an interactive startup result from a service session or from a process that was merely observed alive. WinUI window rendering needs an interactive user desktop; CI should report that test as a separate manual/VM gate.

## Installer impact

### Current WPF and WinUI Preview products

`build-installer.ps1` now accepts an explicit `-Desktop Wpf` or `-Desktop WinUI`. Its default remains WPF. The WinUI selection creates an unpackaged, `win-x64`, multi-file self-contained application inside a double-click Setup.exe. It bundles .NET and Windows App SDK, keeps trimming and single-file application publishing disabled, and points its shortcuts to `KNetAgent.Desktop.WinUI.exe`.

The Preview is intentionally a second installed product:

| Product | Inno AppId | Default installation directory |
|---|---|---|
| Formal WPF | `{74C0F60B-2D53-4E3A-A71F-3980323A2D6A}` | `{userpf}\K.netagent` |
| WinUI Preview | `{9A0976AE-7584-4AF4-92F1-FD33F180118B}` | `{userpf}\K.netagent Preview` |

Keep the Preview AppId stable for subsequent Preview updates. A Preview installation must not overwrite or uninstall the formal WPF application. This separates installed binaries, shortcuts, and uninstall registration; it does **not** isolate user data. Both applications still use `%LOCALAPPDATA%\TestAgent` for configuration, sessions, memory, protected secrets, and other application state. Their shared-data compatibility and simultaneous-use behavior need explicit validation; do not describe the two installations as fully isolated.

WebView2 is a separate dependency boundary: the Preview includes its Loader, but the read-only browser uses the machine's Evergreen WebView2 Runtime. Test a missing-runtime scenario and its recovery guidance separately. Git, the .NET SDK, and other optional development tools are also not bundled.

### Local evidence already available

The development tree has the following local evidence; it is not a clean-machine certification:

- Executed automated-test reports are available under `artifacts/validation/hour-20260926/`; the README records the local Release build result. A test command returning exit code zero without an executed-test report is insufficient.
- The WinUI packaging path has produced a Setup.exe, a SHA-256 file, and a JSON build manifest under `artifacts/installer/winui-preview/`.
- `scripts/Test-WinUiPublish.ps1` checks the self-contained runtime configuration, required native dependencies, and application XBF/PRI resources. The packaging invocation compares compiled resources with the current isolated build output and .NET native binaries with the restored runtime pack; a historical `bin` directory is not packaging evidence.
- `artifacts/validation/installer-20260926/` contains successful installation, same-Preview reinstallation, final-package installation and uninstall logs. The final package's installed files matched its publish output; the final uninstall removed the test app, Preview shortcuts and registration while preserving the current user-data fingerprints. This is a development-machine result, not a WPF-to-WinUI upgrade or clean-machine certification; see `preview-20260926-validation.md` for the exact evidence.
- The README records a local process-startup smoke test with an explicit workspace. Process/window startup evidence does not replace the interactive rendering, keyboard, DPI, accessibility, and workflow checks above.

Use the manifest, actual file hash, and test reports from the final frozen build when reviewing a candidate. This checklist intentionally does not pin a test count or installer hash: later source changes and rebuilt packages require refreshed evidence.

### Remaining Preview and stable-release acceptance

- Install and launch on clean supported Windows 10 and Windows 11 machines without .NET or Windows App SDK preinstalled; verify current-user installation without elevation. Test WebView2 availability separately.
- Exercise the interactive smoke checklist and the installed entry points, including `--page events` and `--page services`. Opening a diagnostic page must not silently read system data or send a model request.
- Repeat the full Preview uninstall test on clean machines and representative coexistence configurations. The current development-machine test passed for Preview removal and existing user-data preservation; it does not prove every supported WPF coexistence or upgrade scenario.
- Test an update from an older Preview with representative existing configuration, sessions, memory, and workspaces. A same-version reinstall alone does not cover schema changes, interrupted updates, or recovery.
- Verify shared-data compatibility between the supported WPF and Preview versions, including the supported behavior when both are open. Record any incompatibility and the recovery path before claiming safe coexistence.
- Record signature status and expected distribution prompts. An unsigned local build may be offered as an explicitly identified preview, but a successful local install does not establish signing or SmartScreen reputation.

### Future replacement of the formal WPF entry point

An in-place replacement of the formal product is a different migration from the Preview installer. It requires a reviewed release change that **preserves the original WPF AppId**, so existing formal installations upgrade rather than becoming an unrelated second product. Do not accomplish that migration by silently changing the Preview AppId.

Before that switch, verify upgrade over the published WPF version, data compatibility or explicit migrations, failure recovery, first launch, uninstall, and preservation of `%LOCALAPPDATA%\TestAgent`. Also decide how an existing separate Preview installation is handled. Passing Preview installation tests does not satisfy these formal-upgrade gates.

### Publication boundary

The current GitHub `v*` tag workflow still calls the default WPF packaging path, collects the WPF output directory, and does not explicitly mark a release as a prerelease. It must not be used as evidence that tagging the current source will publish the WinUI Preview correctly. Changing the workflow and publishing an exact version are separate release actions; neither is authorized by this acceptance document.
