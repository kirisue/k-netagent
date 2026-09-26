using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public interface IWindowsServiceNativeSource
{
    WindowsServiceQueryResult Query(WindowsServiceQuery query, CancellationToken ct);
}

public sealed class WindowsServiceReader : IWindowsServiceReader
{
    private readonly IWindowsServiceNativeSource _source;
    private readonly TimeSpan _budget;
    // A timed-out native call keeps this slot until it returns; retries cannot spawn unbounded native calls.
    private readonly SemaphoreSlim _nativeSlot = new(1, 1);

    public WindowsServiceReader(IWindowsServiceNativeSource? source = null, TimeSpan? queryBudget = null)
    {
        _source = source ?? new WindowsServiceNativeSource();
        _budget = queryBudget ?? TimeSpan.FromSeconds(10);
        if (_budget <= TimeSpan.Zero || _budget > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(queryBudget));
    }

    public async Task<WindowsServiceQueryResult> QueryAsync(WindowsServiceQuery query, CancellationToken ct = default)
    {
        query = WindowsServiceQueryPolicy.Validate(query);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_budget);
        var token = budget.Token;
        try
        {
            await _nativeSlot.WaitAsync(token).ConfigureAwait(false);
            var work = Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var result = _source.Query(query, token);
                    token.ThrowIfCancellationRequested();
                    var matching = result.Services.Where(item => WindowsServiceQueryPolicy.Matches(item, query))
                        .DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Take(query.MaxServices + 1).ToArray();
                    var bounded = matching.Take(query.MaxServices).Select(WindowsServiceQueryPolicy.Sanitize).ToArray();
                    return new WindowsServiceQueryResult(bounded, result.Truncated || matching.Length > query.MaxServices,
                        result.QueriedAt, bounded.Any(item => item.DetailsUnavailable)
                            ? "部分服务的详细配置不可读取；已保留可读取的状态。" : null);
                }
                finally { _nativeSlot.Release(); }
            }, CancellationToken.None);
            // Observe faults even if the time budget expires before a native API returns.
            _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await work.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Windows 服务查询超过时间预算（最多 10 秒）。请缩小查询范围后重试。"); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        { throw new UnauthorizedAccessException("Windows 未允许读取本机服务状态。"); }
        catch (Win32Exception)
        { throw new InvalidOperationException("Windows 服务查询未完成。请在本地服务管理器中检查状态。"); }
    }
}

public static class WindowsServiceQueryPolicy
{
    public static WindowsServiceQuery Validate(WindowsServiceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.MaxServices is < 1 or > 100) throw new ArgumentException("结果上限必须在 1–100 之间。");
        string? ValidateText(string? value, int maxLength)
        {
            if (value is null) return null;
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength ||
                value.Any(c => char.IsControl(c) || c is '\\' or '/'))
                throw new ArgumentException("服务筛选值无效。请输入本机服务名称或显示名称片段。");
            return value.Trim();
        }
        var name = ValidateText(query.Name, 256);
        var filter = ValidateText(query.Filter, 128);
        if (name is not null && filter is not null) throw new ArgumentException("精确名称与模糊筛选不能同时使用。");
        return query with { Name = name, Filter = filter };
    }

    public static bool Matches(WindowsServiceItem item, WindowsServiceQuery query) =>
        query.Name is not null ? string.Equals(item.Name, query.Name, StringComparison.OrdinalIgnoreCase) :
        query.Filter is null || item.Name.Contains(query.Filter, StringComparison.OrdinalIgnoreCase) ||
        item.DisplayName.Contains(query.Filter, StringComparison.OrdinalIgnoreCase);

    public static WindowsServiceItem Sanitize(WindowsServiceItem item) => item with
    {
        Name = Clean(item.Name), DisplayName = Clean(item.DisplayName),
        Status = item.Status is "Stopped" or "StartPending" or "StopPending" or "Running" or "ContinuePending" or "PausePending" or "Paused" ? item.Status : "Unknown",
        StartType = item.StartType is "Boot" or "System" or "Automatic" or "Manual" or "Disabled" ? item.StartType : "Unknown",
        Dependencies = item.Dependencies.Take(32).Select(Clean).ToArray()
    };

    private static string Clean(string value) => new(value.Where(c => !char.IsControl(c) &&
        char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).Take(256).ToArray());
}

// Local SCM only. All access flags below are query/enumeration flags; no mutation API is declared.
public sealed class WindowsServiceNativeSource : IWindowsServiceNativeSource
{
    private const int ErrorMoreData = 234, ErrorInsufficientBuffer = 122, ErrorServiceDoesNotExist = 1060;

    public WindowsServiceQueryResult Query(WindowsServiceQuery query, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 服务查询仅支持 Windows。");
        using var manager = OpenSCManager(null, null, query.Name is null ? 0x0001u | 0x0004u : 0x0001u);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var results = new List<WindowsServiceItem>();
        if (query.Name is not null)
        {
            ct.ThrowIfCancellationRequested();
            var item = ReadService(manager, query.Name, query.Name, 0, ct);
            if (item is not null) results.Add(item);
            return new(results, false, DateTimeOffset.UtcNow);
        }

        // SCM supports pages of at most 256 KiB. No executable/account fields are in enumeration records.
        const int bufferSize = 256 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        uint resume = 0;
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var ok = EnumServicesStatusEx(manager, 0, 0x30, 0x3, buffer, bufferSize,
                    out _, out var returned, ref resume, null);
                var error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != ErrorMoreData) throw new Win32Exception(error);
                var stride = Marshal.SizeOf<ServiceEnumeration>();
                if (returned < 0 || returned > bufferSize / stride) throw new InvalidDataException("Windows 服务枚举返回了无效长度。");
                var strings = new WindowsServiceNativeBuffer(buffer, bufferSize);
                for (var index = 0; index < returned; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var value = Marshal.PtrToStructure<ServiceEnumeration>(buffer + index * stride);
                    var name = strings.ReadString(value.Name);
                    var display = strings.ReadString(value.DisplayName);
                    var basic = new WindowsServiceItem(name, display, Status(value.Status.CurrentState), "Unknown", []);
                    if (!WindowsServiceQueryPolicy.Matches(basic, query)) continue;
                    if (results.Count == query.MaxServices) return new(results, true, DateTimeOffset.UtcNow);
                    results.Add(ReadService(manager, name, display, value.Status.CurrentState, ct) ?? basic with { DetailsUnavailable = true });
                }
                if (ok) break;
                if (returned == 0) throw new InvalidDataException("Windows 服务枚举无法继续。");
            } while (true);
            return new(results, false, DateTimeOffset.UtcNow);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static WindowsServiceItem? ReadService(ServiceHandle manager, string name, string display, uint knownState, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var statusHandle = OpenService(manager, name, 0x0004); // QUERY_STATUS
        if (statusHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist) return null;
            return new(name, display, Status(knownState), "Unknown", [], true);
        }
        var state = knownState;
        if (QueryServiceStatus(statusHandle, out var current)) state = current.CurrentState;
        var unavailable = state == 0;
        // Some services permit status reads while denying config reads. Keep the status in that case.
        using var service = OpenService(manager, name, 0x0001); // QUERY_CONFIG
        if (service.IsInvalid) return new(name, display, Status(state), "Unknown", [], true);
        QueryServiceConfig(service, IntPtr.Zero, 0, out var required);
        var queryError = Marshal.GetLastWin32Error();
        if (queryError != ErrorInsufficientBuffer || required < Marshal.SizeOf<ServiceConfig>() || required > 8192)
            return new(name, display, Status(state), "Unknown", [], true);
        var configBuffer = Marshal.AllocHGlobal(required);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!QueryServiceConfig(service, configBuffer, required, out _))
                return new(name, display, Status(state), "Unknown", [], true);
            // Only dereference display name and dependency pointers; never read binary path/account strings.
            var config = Marshal.PtrToStructure<ServiceConfig>(configBuffer);
            var strings = new WindowsServiceNativeBuffer(configBuffer, required);
            var dependencies = strings.ReadMultiString(config.Dependencies);
            return new(name, strings.ReadString(config.DisplayName), Status(state), StartType(config.StartType),
                dependencies, unavailable);
        }
        finally { Marshal.FreeHGlobal(configBuffer); }
    }

    private static string Status(uint value) => value switch
    { 1 => "Stopped", 2 => "StartPending", 3 => "StopPending", 4 => "Running", 5 => "ContinuePending", 6 => "PausePending", 7 => "Paused", _ => "Unknown" };
    private static string StartType(uint value) => value switch
    { 0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => "Unknown" };

    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint Type, CurrentState, ControlsAccepted, ExitCode, ServiceExitCode, CheckPoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatusProcess
    { public uint Type, CurrentState, ControlsAccepted, ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceEnumeration
    { public IntPtr Name, DisplayName; public ServiceStatusProcess Status; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceAccount, DisplayName;
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumServicesStatusEx(ServiceHandle manager, int level, uint type, uint state, IntPtr buffer, int size, out int needed, out int returned, ref uint resume, string? group);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(ServiceHandle service, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr buffer, int size, out int needed);
}

/// <summary>
/// Decodes only strings inside an allocated SCM output buffer owned by the caller.
/// The caller keeps that buffer alive and unchanged for the complete read. No string
/// pointer is dereferenced before its alignment and remaining byte range are checked.
/// </summary>
public sealed class WindowsServiceNativeBuffer
{
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);
    private readonly IntPtr _buffer;
    private readonly int _bytes;
    private readonly nuint _start;

    public WindowsServiceNativeBuffer(IntPtr buffer, int bytes)
    {
        _start = (nuint)buffer;
        if (buffer == IntPtr.Zero || bytes is < 2 or > 256 * 1024 || (bytes & 1) != 0 ||
            (_start & 1) != 0 || _start > nuint.MaxValue - (nuint)bytes)
            throw InvalidBuffer();
        _buffer = buffer;
        _bytes = bytes;
    }

    public string ReadString(IntPtr pointer)
    {
        var offset = OffsetOf(pointer);
        return ReadStringAt(offset, out _);
    }

    public IReadOnlyList<string> ReadMultiString(IntPtr pointer)
    {
        // A null lpDependencies represents no dependencies. Other fields cannot be null.
        if (pointer == IntPtr.Zero) return [];
        var offset = OffsetOf(pointer);
        var names = new List<string>();
        var consumedString = false;
        while (true)
        {
            if (ReadCodeUnit(offset) == 0)
            {
                // Non-empty lists already consumed their first terminating NUL with the last item.
                if (!consumedString && ReadCodeUnit(offset + 2) != 0) throw InvalidBuffer();
                return names;
            }
            var name = ReadStringAt(offset, out var consumedBytes);
            if (names.Count < 32) names.Add(name);
            offset += consumedBytes;
            consumedString = true;
            // Continue validating the complete MULTI_SZ even when the output item cap is reached.
        }
    }

    private string ReadStringAt(int offset, out int consumedBytes)
    {
        for (var length = 0; length <= 256; length++)
        {
            if (ReadCodeUnit(offset + length * 2) != 0) continue;
            consumedBytes = (length + 1) * 2;
            if (length == 0) return "";
            var bytes = new byte[length * 2];
            Marshal.Copy(_buffer + offset, bytes, 0, bytes.Length);
            try { return StrictUtf16.GetString(bytes); }
            catch (DecoderFallbackException) { throw InvalidBuffer(); }
        }
        throw InvalidBuffer();
    }

    private ushort ReadCodeUnit(int offset)
    {
        if (offset < 0 || (offset & 1) != 0 || offset > _bytes - 2) throw InvalidBuffer();
        return unchecked((ushort)Marshal.ReadInt16(_buffer, offset));
    }

    private int OffsetOf(IntPtr pointer)
    {
        var address = (nuint)pointer;
        if (pointer == IntPtr.Zero || address < _start || address >= _start + (nuint)_bytes ||
            (address & 1) != 0) throw InvalidBuffer();
        return checked((int)(address - _start));
    }

    private static InvalidDataException InvalidBuffer() => new("Windows 服务元数据缓冲区格式无效。");
}
