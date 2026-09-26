using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed class WindowsEventReader(IWindowsEventNativeSource? source = null) : IWindowsEventReader
{
    private readonly IWindowsEventNativeSource _source = source ?? new WindowsEventNativeSource();
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<WindowsEventChannelInfo>> ListChannelsAsync(CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        var token = budget.Token;
        try
        {
            return await Task.Run<IReadOnlyList<WindowsEventChannelInfo>>(() =>
            {
                var channels = new List<WindowsEventChannelInfo>();
                foreach (var channel in new[] { "Application", "System" })
                {
                    token.ThrowIfCancellationRequested();
                    try { channels.Add(new(channel, _source.IsChannelAvailable(channel))); }
                    catch (Exception ex) { channels.Add(new(channel, false, WindowsEventErrors.Describe(ex))); }
                }
                return channels;
            }, token).WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested && !ct.IsCancellationRequested)
        { throw new TimeoutException("Windows 事件频道读取超过 10 秒，请稍后重试。"); }
    }

    public async Task<WindowsEventQueryResult> QueryAsync(WindowsEventQuery query, CancellationToken ct = default)
    {
        query = WindowsEventQueryPolicy.Validate(query);
        var xpath = WindowsEventQueryPolicy.BuildXPath(query);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        // Capture the token before the source may outlive the caller's ten-second budget.
        var token = budget.Token;
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var reader = _source.OpenReader(query.Channel, xpath);
                using var cancel = token.Register(() => { try { reader.Cancel(); } catch { } });
                var events = new List<WindowsEventItem>();
                var truncated = false;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var next = reader.Read(TimeSpan.FromSeconds(1));
                    token.ThrowIfCancellationRequested();
                    if (next is null) break;
                    if (events.Count == query.MaxEvents) { truncated = true; break; }
                    events.Add(next.Item with
                    {
                        Channel = query.Channel,
                        Provider = WindowsEventPrivacy.Sanitize(next.Item.Provider, 200),
                        Message = WindowsEventPrivacy.Sanitize(next.Item.Message)
                    });
                }
                return new WindowsEventQueryResult(events, truncated,
                    truncated && events[^1].RecordId > 0 ? events[^1].RecordId : null, DateTimeOffset.UtcNow);
            }, token).WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested && !ct.IsCancellationRequested)
        { throw new TimeoutException("Windows 事件读取超过 10 秒。请缩小时间范围后重试。"); }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException and not TimeoutException)
        { throw WindowsEventErrors.Wrap(ex); }
    }
}
