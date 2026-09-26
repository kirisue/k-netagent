using System.Text.Json;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public void Provider_settings_constructor_uses_recommended_runtime_limits()
    {
        var provider = new ProviderSettings("custom", "https://model.example/v1", "model");

        Assert.Equal(32_768, provider.MaxOutputTokens);
        Assert.Equal(300, provider.TimeoutSeconds);
        Assert.Equal("medium", provider.ReasoningEffort);
    }

    [Fact]
    public async Task Missing_config_uses_openai_gpt_5_6_defaults()
    {
        using var directory = new TemporaryDirectory();
        var settings = await new JsonSettingsStore(new AppPaths(directory.Path)).LoadAsync();

        Assert.Equal("openai", settings.Provider.ProviderId);
        Assert.Equal("https://api.openai.com/v1", settings.Provider.Endpoint);
        Assert.Equal("gpt-5.6-sol", settings.Provider.Model);
        Assert.Equal(32_768, settings.Provider.MaxOutputTokens);
        Assert.Equal(300, settings.Provider.TimeoutSeconds);
        Assert.Equal("medium", settings.Provider.ReasoningEffort);
    }

    [Fact]
    public async Task Legacy_config_without_reasoning_effort_loads_as_medium()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        Directory.CreateDirectory(paths.Root);
        const string legacyJson = """
            {
              "provider": {
                "providerId": "custom",
                "endpoint": "https://model.example/v1",
                "model": "legacy-model",
                "maxOutputTokens": 4096,
                "timeoutSeconds": 120,
                "maxContextMessages": 30,
                "selfReviewEnabled": true,
                "maxSelfReviewRounds": 1,
                "supportsImageInput": false
              },
              "systemPrompt": "legacy",
              "version": 1
            }
            """;
        await File.WriteAllTextAsync(paths.Config, legacyJson);

        var settings = await new JsonSettingsStore(paths).LoadAsync();

        Assert.Equal("legacy-model", settings.Provider.Model);
        Assert.Equal(4_096, settings.Provider.MaxOutputTokens);
        Assert.Equal("medium", settings.Provider.ReasoningEffort);
    }

    [Fact]
    public async Task Invalid_reasoning_effort_is_normalized_when_saved_and_loaded()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(directory.Path);
        var store = new JsonSettingsStore(paths);
        var settings = new AppSettings(new ProviderSettings("openai", "https://api.openai.com/v1",
            "gpt-5.6-sol", ReasoningEffort: "ultra"));

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal("medium", loaded.Provider.ReasoningEffort);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.Config));
        Assert.Equal("medium", document.RootElement.GetProperty("provider")
            .GetProperty("reasoningEffort").GetString());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "TestAgent.Tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
