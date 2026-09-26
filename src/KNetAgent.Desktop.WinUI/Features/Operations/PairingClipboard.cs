using KNetAgent.Desktop.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;

namespace KNetAgent.Desktop.WinUI.Features.Operations;

public interface IPairingClipboard
{
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
    Task<bool> ClearIfMatchesAsync(string expectedText, CancellationToken cancellationToken = default);
}

/// <summary>
/// Clipboard adapter that clears only the pairing payload written by this feature.
/// It deliberately never clears arbitrary user clipboard contents.
/// </summary>
public sealed class WinUiPairingClipboard(IUiDispatcher dispatcher) : IPairingClipboard
{
    private const string PairingFormat = "K.netagent.vscode-pairing.v1";

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return dispatcher.InvokeAsync(() =>
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            package.SetData(PairingFormat, text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }, cancellationToken);
    }

    public async Task<bool> ClearIfMatchesAsync(
        string expectedText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedText);
        DataPackageView? snapshot = null;
        await dispatcher.InvokeAsync(() => snapshot = Clipboard.GetContent(), cancellationToken);
        if (snapshot is null || !snapshot.Contains(StandardDataFormats.Text) ||
            !snapshot.Contains(PairingFormat))
            return false;

        var current = await snapshot.GetTextAsync().AsTask(cancellationToken);
        if (!string.Equals(current, expectedText, StringComparison.Ordinal))
            return false;

        // Re-read before clearing to reduce the chance of replacing clipboard data that
        // the user copied while the asynchronous text request was in flight.
        DataPackageView? latest = null;
        await dispatcher.InvokeAsync(() => latest = Clipboard.GetContent(), cancellationToken);
        if (latest is null || !latest.Contains(StandardDataFormats.Text) || !latest.Contains(PairingFormat))
            return false;
        var latestText = await latest.GetTextAsync().AsTask(cancellationToken);
        if (!string.Equals(latestText, expectedText, StringComparison.Ordinal))
            return false;

        await dispatcher.InvokeAsync(Clipboard.Clear, cancellationToken);
        return true;
    }
}
