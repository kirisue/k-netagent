using KNetAgent.Desktop.WinUI.Browser;
using Microsoft.Extensions.DependencyInjection;
using TestAgent.Core;
using TestAgent.Infrastructure;

namespace KNetAgent.Desktop.WinUI.Features.Media;

public static class MediaBrowserServiceCollectionExtensions
{
    /// <summary>
    /// Registers the feature slices. ISafeWebContentReader, AppPaths and IUiDispatcher
    /// must already be registered by the composition root.
    /// </summary>
    public static IServiceCollection AddWinUiMediaAndBrowser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<WinUiImageInputService>();
        services.AddSingleton<IImageInputService>(provider =>
            provider.GetRequiredService<WinUiImageInputService>());
        services.AddSingleton<IWinUiImagePicker>(provider =>
            provider.GetRequiredService<WinUiImageInputService>());
        services.AddSingleton<WinUiImageConfirmationPrompt>();
        services.AddSingleton<IImageConfirmationPrompt>(provider =>
            provider.GetRequiredService<WinUiImageConfirmationPrompt>());
        services.AddSingleton<MediaAttachmentViewModel>();
        services.AddSingleton<WinUiReadOnlyBrowserSession>();
        services.AddSingleton<IReadOnlyBrowserSession>(provider =>
            provider.GetRequiredService<WinUiReadOnlyBrowserSession>());
        services.AddSingleton<BrowserFeatureViewModel>();
        services.AddSingleton<IAgentTool, OpenBrowserSnapshotTool>();
        services.AddSingleton<IAgentTool, ReadBrowserDomTool>();
        services.AddSingleton<IAgentTool, CaptureBrowserViewportTool>();
        return services;
    }
}
