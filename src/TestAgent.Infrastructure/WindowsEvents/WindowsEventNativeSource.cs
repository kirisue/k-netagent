using System.Diagnostics.Eventing.Reader;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

// Native handles and bookmarks stay below the Core boundary. The injectable adapter also
// allows lifecycle, cancellation and overflow tests without changing Windows event logs.
public sealed record WindowsEventNativeData(WindowsEventItem Item, string? BookmarkXml = null);

public interface IWindowsEventNativeCursor : IDisposable
{
    WindowsEventNativeData? Read(TimeSpan timeout);
    void Cancel();
}

public interface IWindowsEventNativeWatch : IDisposable
{
    void Start();
}

public interface IWindowsEventNativeSource
{
    bool IsChannelAvailable(string channel);
    IWindowsEventNativeCursor OpenReader(string channel, string xpath);
    IWindowsEventNativeWatch CreateWatch(string channel, string xpath, string? bookmarkXml,
        Action<WindowsEventNativeData> received, Action<Exception> failed);
}

public sealed class WindowsEventNativeSource : IWindowsEventNativeSource
{
    public bool IsChannelAvailable(string channel)
    {
        ValidateChannel(channel);
        using var config = new EventLogConfiguration(channel);
        return config.IsEnabled;
    }

    public IWindowsEventNativeCursor OpenReader(string channel, string xpath)
    {
        ValidateChannel(channel);
        return new Cursor(new EventLogReader(new EventLogQuery(channel, PathType.LogName, xpath)
        { ReverseDirection = true, TolerateQueryErrors = false }), channel);
    }

    public IWindowsEventNativeWatch CreateWatch(string channel, string xpath, string? bookmarkXml,
        Action<WindowsEventNativeData> received, Action<Exception> failed)
    {
        ValidateChannel(channel);
        if (bookmarkXml is not null)
        {
            var recordId = WindowsEventBookmarks.ReadRecordId(bookmarkXml, channel);
            using var probe = new EventLogReader(new EventLogQuery(channel, PathType.LogName,
                $"*[System[EventRecordID={recordId}]]"));
            using var existing = probe.ReadEvent(TimeSpan.FromSeconds(2));
            if (existing is null)
                throw new InvalidDataException("监控书签已过期，日志可能已轮转或被清空。请取消恢复书签，从现在开始监控。");
        }
        var watcher = new EventLogWatcher(new EventLogQuery(channel, PathType.LogName, xpath)
        { TolerateQueryErrors = false }, bookmarkXml is null ? null : new EventBookmark(bookmarkXml),
            readExistingEvents: bookmarkXml is not null);
        return new Watch(watcher, channel, received, failed);
    }

    private static void ValidateChannel(string channel) => WindowsEventQueryPolicy.Validate(new(Channel: channel));

    private static WindowsEventItem Convert(EventRecord record, string channel)
    {
        var unavailable = false;
        string? description;
        try { description = record.FormatDescription(); unavailable = string.IsNullOrWhiteSpace(description); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { description = null; unavailable = true; }
        return new(channel, record.RecordId ?? 0, record.Id,
            WindowsEventPrivacy.Sanitize(record.ProviderName, 200), record.Level ?? 0,
            record.TimeCreated is { } time ? new DateTimeOffset(time) : null,
            unavailable ? "此事件的本地说明资源不可用，仅显示事件元数据。" : WindowsEventPrivacy.Sanitize(description), unavailable);
    }

    private sealed class Cursor(EventLogReader reader, string channel) : IWindowsEventNativeCursor
    {
        public WindowsEventNativeData? Read(TimeSpan timeout)
        {
            using var record = reader.ReadEvent(timeout);
            return record is null ? null : new(Convert(record, channel));
        }
        public void Cancel() { try { reader.CancelReading(); } catch (ObjectDisposedException) { } }
        public void Dispose() => reader.Dispose();
    }

    private sealed class Watch : IWindowsEventNativeWatch
    {
        private readonly EventLogWatcher _watcher;
        private readonly string _channel;
        private readonly Action<WindowsEventNativeData> _received;
        private readonly Action<Exception> _failed;

        public Watch(EventLogWatcher watcher, string channel, Action<WindowsEventNativeData> received, Action<Exception> failed)
        {
            _watcher = watcher; _channel = channel; _received = received; _failed = failed;
            _watcher.EventRecordWritten += OnEvent;
        }
        private void OnEvent(object? sender, EventRecordWrittenEventArgs args)
        {
            using var record = args.EventRecord;
            if (args.EventException is { } error) { _failed(error); return; }
            if (record is null) return;
            try { _received(new(Convert(record, _channel), record.Bookmark.BookmarkXml)); }
            catch (Exception ex) { _failed(ex); }
        }
        public void Start() => _watcher.Enabled = true;
        public void Dispose()
        {
            _watcher.EventRecordWritten -= OnEvent;
            _watcher.Dispose();
        }
    }
}

internal static class WindowsEventErrors
{
    public static Exception Wrap(Exception error, bool bookmark = false) =>
        error is UnauthorizedAccessException
            ? new UnauthorizedAccessException("当前用户无权读取此 Windows 事件频道。应用不会自动提权。")
            : new InvalidOperationException(Describe(error, bookmark));

    public static string Describe(Exception error, bool bookmark = false) => error switch
    {
        UnauthorizedAccessException => "当前用户无权读取此频道。请在 Windows 中检查权限；应用不会自动提权。",
        EventLogNotFoundException => "此 Windows 事件频道当前不可用。",
        InvalidDataException when bookmark => "监控书签不可用或已过期。请取消恢复书签，从现在开始监控。",
        EventLogException when bookmark => "无法恢复 Windows 事件监控，书签可能已过期。请取消恢复书签后重试。",
        EventLogException => "Windows 事件日志读取失败。请确认事件日志服务可用后重试。",
        IOException => "无法读取或保存本地事件监控位置。",
        _ => "Windows 事件读取未完成，请稍后重试。"
    };
}
