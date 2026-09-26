using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace KNetAgent.Desktop.WinUI.Services;

/// <summary>
/// Starts a fresh application process for a newly selected workspace. The
/// workspace root is passed as a single argument rather than through a shell,
/// so spaces and command characters cannot change process-launch semantics.
/// </summary>
public sealed class WorkspaceRelaunchService
{
    public void Relaunch(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot))
            throw new InvalidDataException("工作区路径无效，未重新启动 K.netagent。");

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("找不到当前 K.netagent 可执行文件，无法切换工作区。");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(Path.GetFullPath(workspaceRoot));

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("新的 K.netagent 进程未能启动，当前窗口将保持打开。");

        Application.Current.Exit();
    }
}
