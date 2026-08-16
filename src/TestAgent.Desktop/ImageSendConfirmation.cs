using System.Windows;

namespace TestAgent.Desktop;

public sealed record ImageSendConfirmation(
    string Target,
    string Model,
    int Width,
    int Height,
    int ByteLength,
    int MaxModelRequests);

public interface IImageSendConfirmationService
{
    bool Confirm(ImageSendConfirmation request);
}

public sealed class WpfImageSendConfirmationService : IImageSendConfirmationService
{
    public bool Confirm(ImageSendConfirmation request)
    {
        var result = MessageBox.Show(
            $"将把当前图片的可见像素发送到：\n{request.Target}\n\n" +
            $"模型：{request.Model}\n尺寸：{request.Width}×{request.Height}\n" +
            $"大小：{request.ByteLength / 1024d:F1} KB\n\n" +
            $"同一轮如需调用工具，最多 {request.MaxModelRequests} 次模型请求会携带这张图片；" +
            "含图请求不会自动网络重试。图片路径、文件名和元数据不会写入会话。继续？",
            "发送图片",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}
