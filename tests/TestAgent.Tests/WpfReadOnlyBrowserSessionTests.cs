using System.Reflection;
using System.Windows.Threading;
using TestAgent.Core;
using TestAgent.Desktop;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class WpfReadOnlyBrowserSessionTests
{
    [Fact]
    public Task Cancelling_second_navigation_clears_old_page_and_capture_and_blocks_dom_and_capture() =>
        RunStaAsync(async () =>
        {
            var navigationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var navigationCalls = 0;
            var captureCalls = 0;
            var hooks = new BrowserSessionTestHooks(
                async (_, ct) =>
                {
                    if (Interlocked.Increment(ref navigationCalls) == 1) return;
                    navigationStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                },
                _ =>
                {
                    captureCalls++;
                    return Task.FromResult(OnePixelPng());
                });

            using var session = new WpfReadOnlyBrowserSession(new FakeReader(), new AppPaths(), hooks);
            var oldPage = await session.OpenSnapshotAsync("https://example.com/old");
            Assert.Same(oldPage, session.Current);
            Assert.Equal("old page body", (await session.ReadDomAsync()).Text);

            await session.CaptureViewportAsync();
            Assert.True(session.HasLatestCapture);
            var oldCapture = PeekLatestCapture(session);
            Assert.Contains(oldCapture.Data, value => value != 0);

            using var cancellation = new CancellationTokenSource();
            var opening = session.OpenSnapshotAsync("https://example.com/new", ct: cancellation.Token);
            await navigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);

            Assert.Null(session.Current);
            Assert.False(session.HasLatestCapture);
            Assert.All(oldCapture.Data, value => Assert.Equal((byte)0, value));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadDomAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.CaptureViewportAsync());
            Assert.Equal(1, captureCalls);
        });

    [Fact]
    public Task Failed_second_navigation_leaves_display_non_capturable() => RunStaAsync(async () =>
    {
        var navigationCalls = 0;
        var captureCalls = 0;
        var hooks = new BrowserSessionTestHooks(
            (_, _) => Interlocked.Increment(ref navigationCalls) == 1
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("simulated render failure")),
            _ =>
            {
                captureCalls++;
                return Task.FromResult(OnePixelPng());
            });

        using var session = new WpfReadOnlyBrowserSession(new FakeReader(), new AppPaths(), hooks);
        await session.OpenSnapshotAsync("https://example.com/old");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.OpenSnapshotAsync("https://example.com/new"));
        Assert.Contains("simulated render failure", error.Message);
        Assert.Null(session.Current);
        Assert.False(session.HasLatestCapture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadDomAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CaptureViewportAsync());
        Assert.Equal(0, captureCalls);
    });

    private static ImageInput PeekLatestCapture(WpfReadOnlyBrowserSession session)
    {
        var field = typeof(WpfReadOnlyBrowserSession).GetField("_latestCapture",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<ImageInput>(field?.GetValue(session));
    }

    private static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private sealed class FakeReader : ISafeWebContentReader
    {
        public Task<WebContentResult> ReadAsync(string url, int maxChars, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var name = url.EndsWith("/old", StringComparison.Ordinal) ? "old" : "new";
            return Task.FromResult(new WebContentResult(
                $"https://example.com/{name}", "text/html", $"{name} page", $"{name} page body",
                false, 100));
        }
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }
}
