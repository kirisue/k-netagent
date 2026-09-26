using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KNetAgent.Desktop.WinUI.Features.Media;

public sealed class WinUiImageConfirmationPrompt : IImageConfirmationPrompt
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private XamlRoot? _xamlRoot;

    public void Attach(XamlRoot xamlRoot) => _xamlRoot = xamlRoot ??
        throw new ArgumentNullException(nameof(xamlRoot));

    public void Detach() => _xamlRoot = null;

    public async Task<bool> ConfirmAsync(ImageSendConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = _xamlRoot;
            if (root is null) return false;
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "发送图片",
                Content = new TextBlock
                {
                    Text = $"将把当前图片的可见像素发送到：\n{request.Target}\n\n" +
                           $"模型：{request.Model}\n尺寸：{request.Width}×{request.Height}\n" +
                           $"大小：{request.ByteLength / 1024d:F1} KB\n\n" +
                           $"本轮最多 {request.MaxModelRequests} 次模型请求会携带这张图片。" +
                           "原文件名、路径和 EXIF 元数据不会发送。继续？",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                },
                PrimaryButtonText = "发送",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            using var registration = cancellationToken.Register(() =>
            {
                try { dialog.Hide(); } catch { }
            });
            var result = await dialog.ShowAsync().AsTask(cancellationToken);
            return result == ContentDialogResult.Primary;
        }
        finally
        {
            _gate.Release();
        }
    }
}
