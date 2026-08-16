# K.netagent Read-Only Bridge

This extension connects one trusted, local, single-folder VS Code workspace to the matching K.netagent workspace through a Windows named pipe.

Pairing is explicit: copy the temporary pairing JSON from K.netagent, run **K.netagent: Pair Read-Only Bridge**, and paste it into the password-style prompt. The random pipe name and 256-bit secret remain only in process memory. They are not written to VS Code settings, workspace storage, global storage, files, logs, or source control. Disconnecting, exhausting eight reconnect attempts, or closing VS Code forgets and clears the pairing secret. K.netagent clears its matching clipboard value as soon as the connection succeeds, or after 75 seconds if no connection succeeds.

After K.netagent asks for approval, the bridge can return only four bounded metadata sets:

- active editor relative path, language, dirty state, selection and visible line ranges;
- diagnostics with relative paths, severity, code, source and ranges; diagnostic messages are deliberately excluded;
- current paired-workspace `tasks.fetchTasks()` names and classification metadata; global and other-folder tasks are excluded;
- installed extension IDs and basic package metadata.

`tasks.fetchTasks()` can wake task providers contributed by already-installed extensions, but this bridge never returns command lines, arguments, environment values or execution objects, and never starts a task.

The bridge never returns file contents or absolute paths. It cannot run commands, type into an editor, edit files, execute tasks, open terminals, activate extension exports, or install, update or remove extensions. Untrusted, remote, virtual, multi-root and mismatched workspaces are rejected by both the extension and K.netagent.

## Install a local VSIX

From the repository root, a maintainer can build and verify the local package with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-VsCodeExtension.ps1
```

The ignored output is written to `artifacts/vscode-extension/`. In Visual Studio Code, open **Extensions**, choose **Views and More Actions (...)**, select **Install from VSIX...**, and choose that file. Building requires Node.js, but installing and using the resulting VSIX does not require a separate Node.js installation. The packaging script does not install or publish the extension.
