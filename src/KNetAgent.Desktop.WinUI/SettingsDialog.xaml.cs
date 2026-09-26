using KNetAgent.Desktop.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace KNetAgent.Desktop.WinUI;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly SettingsSnapshot _original;
    private bool _saved;

    public SettingsDialog(ShellViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _original = SettingsSnapshot.Capture(ViewModel);

        InitializeComponent();
        DataContext = ViewModel;
    }

    public ShellViewModel ViewModel { get; }

    private async void SettingsDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        IsPrimaryButtonEnabled = false;
        SettingsErrorBar.IsOpen = false;

        try
        {
            var apiKey = ViewModel.ApiKey;
            await ViewModel.SaveSettingsAsync(apiKey);
            ViewModel.ApiKey = string.Empty;
            _saved = true;
            args.Cancel = false;
        }
        catch (Exception exception)
        {
            SettingsErrorBar.Message = exception.Message;
            SettingsErrorBar.IsOpen = true;
        }
        finally
        {
            IsPrimaryButtonEnabled = ViewModel.SaveSettingsCommand.CanExecute(null);
            deferral.Complete();
        }
    }

    private void SettingsDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (!_saved)
            _original.Restore(ViewModel);
    }

    private sealed record SettingsSnapshot(
        string ProviderId,
        string Endpoint,
        string Model,
        string ReasoningEffort,
        int MaxTokens,
        int TimeoutSeconds,
        bool SelfReviewEnabled,
        bool SupportsImageInput,
        string ApiKey)
    {
        public static SettingsSnapshot Capture(ShellViewModel viewModel) => new(
            viewModel.ProviderId,
            viewModel.Endpoint,
            viewModel.Model,
            viewModel.ReasoningEffort,
            viewModel.MaxTokens,
            viewModel.TimeoutSeconds,
            viewModel.SelfReviewEnabled,
            viewModel.SupportsImageInput,
            viewModel.ApiKey);

        public void Restore(ShellViewModel viewModel)
        {
            // ProviderId applies a preset, so restore the exact endpoint and model afterwards.
            viewModel.ProviderId = ProviderId;
            viewModel.Endpoint = Endpoint;
            viewModel.Model = Model;
            viewModel.ReasoningEffort = ReasoningEffort;
            viewModel.MaxTokens = MaxTokens;
            viewModel.TimeoutSeconds = TimeoutSeconds;
            viewModel.SelfReviewEnabled = SelfReviewEnabled;
            viewModel.SupportsImageInput = SupportsImageInput;
            viewModel.ApiKey = ApiKey;
        }
    }
}
