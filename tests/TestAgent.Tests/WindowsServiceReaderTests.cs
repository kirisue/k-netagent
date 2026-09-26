using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class WindowsServiceReaderTests
{
    [Fact]
    public async Task Exact_name_is_case_insensitive_and_returns_only_safe_metadata()
    {
        var source = new Source([Item("First"), Item("Second")]);
        var result = await new WindowsServiceReader(source).QueryAsync(new(Name: "first"));
        Assert.Equal("First", Assert.Single(result.Services).Name);
        var fields = JsonSerializer.SerializeToElement(result.Services[0]).EnumerateObject().Select(x => x.Name).ToArray();
        Assert.Equal(["Name", "DisplayName", "Status", "StartType", "Dependencies", "DetailsUnavailable"], fields);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task List_filter_and_limit_are_enforced_even_for_a_native_source_with_excess_results()
    {
        var source = new Source([Item("First"), Item("Second"), Item("Third")]);
        var reader = new WindowsServiceReader(source);
        var result = await reader.QueryAsync(new(Filter: "display", MaxServices: 2));
        Assert.Equal(2, result.Services.Count);
        Assert.True(result.Truncated);
        Assert.Single((await reader.QueryAsync(new(Filter: "second"))).Services);
    }

    [Fact]
    public async Task Partial_denial_keeps_status_and_replaces_native_notice()
    {
        var source = new Source([Item("First") with { DetailsUnavailable = true }]);
        var result = await new WindowsServiceReader(source).QueryAsync(new());
        Assert.Equal("Running", Assert.Single(result.Services).Status);
        Assert.NotNull(result.Notice);
        Assert.DoesNotContain("PRIVATE-NATIVE-MESSAGE", result.Notice);
    }

    [Theory]
    [InlineData("{\"remote\":\"machine\"}")]
    [InlineData("{\"operation\":\"restart\"}")]
    [InlineData("{\"maxServices\":101}")]
    [InlineData("{\"maxServices\":0}")]
    [InlineData("{\"maxServices\":null}")]
    [InlineData("{\"maxServices\":1.5}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":\"\"}")]
    [InlineData("{\"name\":\"a\",\"filter\":\"b\"}")]
    [InlineData("{\"name\":\"a\",\"name\":\"b\"}")]
    [InlineData("{\"filter\":\"a\\nb\"}")]
    [InlineData("{\"name\":\"a/b\"}")]
    [InlineData("[]")]
    public async Task Invalid_arguments_never_call_native_reader(string arguments)
    {
        var source = new Source([]);
        var result = await new QueryWindowsServicesTool(new WindowsServiceReader(source)).ExecuteAsync(Request(arguments));
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task Tool_is_approval_gated_and_caps_output_without_echoing_source_notice()
    {
        var source = new Source(Enumerable.Range(0, 100).Select(i => Item($"Service{i}") with
        { DisplayName = new string('x', 256), Dependencies = Enumerable.Range(0, 32).Select(n => new string('y', 256)).ToArray() }).ToArray());
        var tool = new QueryWindowsServicesTool(new WindowsServiceReader(source));
        Assert.Equal(ToolRiskLevel.LocalEnvironmentRead, tool.Definition.RiskLevel);
        var result = await tool.ExecuteAsync(Request("{\"maxServices\":100}"));
        Assert.Equal(ToolExecutionStatus.Success, result.Status);
        Assert.True(result.Truncated);
        Assert.InRange(result.Output.Length, 1, 16_000);
        using var document = JsonDocument.Parse(result.Output);
        Assert.True(document.RootElement.GetProperty("count").GetInt32() > 0);
        Assert.DoesNotContain("PRIVATE-NATIVE-MESSAGE", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("Service0", result.Summary);
    }

    [Fact]
    public async Task Caller_cancellation_and_timeout_are_distinct_and_do_not_leak_errors()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsServiceReader(new Source([])).QueryAsync(new(), cancelled.Token));
        var source = new WaitingSource();
        var reader = new WindowsServiceReader(source, TimeSpan.FromMilliseconds(40));
        var result = await new QueryWindowsServicesTool(reader).ExecuteAsync(Request("{}"));
        Assert.Equal("windows_services_timeout", result.ErrorCode);
        source.Release.Set();
    }

    [Fact]
    public async Task Timed_out_native_call_retains_slot_until_it_finishes()
    {
        var source = new WaitingSource();
        var reader = new WindowsServiceReader(source, TimeSpan.FromMilliseconds(100));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => reader.QueryAsync(new()));
            await Assert.ThrowsAsync<TimeoutException>(() => reader.QueryAsync(new()));
            Assert.Equal(1, source.Calls);
        }
        finally { source.Release.Set(); }
    }

    [Fact]
    public async Task Native_access_denied_becomes_safe_blocked_error()
    {
        var tool = new QueryWindowsServicesTool(new WindowsServiceReader(new ThrowingSource()));
        var result = await tool.ExecuteAsync(Request("{}"));
        Assert.Equal(ToolExecutionStatus.Blocked, result.Status);
        Assert.Equal("windows_services_access_denied", result.ErrorCode);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Tool_and_evidence_redact_identifying_metadata_but_preserve_local_query_names()
    {
        const string privatePath = @"C:\Users\private-person\daemon.exe";
        var item = Item("LocalService") with { DisplayName = privatePath, Dependencies = ["S-1-5-21-1234-5678-9012-1001"] };
        var reader = new WindowsServiceReader(new Source([item]));
        Assert.Equal(privatePath, Assert.Single((await reader.QueryAsync(new())).Services).DisplayName);
        var result = await new QueryWindowsServicesTool(reader).ExecuteAsync(Request("{}"));
        Assert.DoesNotContain("private-person", result.Output);
        Assert.DoesNotContain("S-1-5-21-1234", result.Output);
        var evidence = new WindowsServiceEvidenceBuilder().Build(item, DateTimeOffset.UtcNow);
        Assert.DoesNotContain("private-person", evidence);
        Assert.DoesNotContain("S-1-5-21-1234", evidence);
        Assert.Contains("LocalService", evidence);
    }

    [Fact]
    public async Task Native_local_smoke_reads_at_most_one_metadata_record()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await new WindowsServiceReader().QueryAsync(new(MaxServices: 1));
        Assert.InRange(result.Services.Count, 0, 1);
        Assert.All(result.Services, item => Assert.InRange(item.Name.Length, 1, 256));
    }

    [Fact]
    public void Native_string_decoder_accepts_utf16_and_a_terminator_at_the_final_code_unit()
    {
        using var buffer = new NativeBuffer("padding\0服务😀\0");
        var strings = new WindowsServiceNativeBuffer(buffer.Pointer, buffer.Bytes);
        Assert.Equal("服务😀", strings.ReadString(buffer.Pointer + 16));
        Assert.Equal("", strings.ReadString(buffer.Pointer + buffer.Bytes - 2));
    }

    [Fact]
    public void Native_string_decoder_rejects_pointers_outside_the_owned_range_before_dereferencing()
    {
        using var buffer = new NativeBuffer("safe\0");
        var strings = new WindowsServiceNativeBuffer(buffer.Pointer, buffer.Bytes);
        foreach (var pointer in new[]
        {
            IntPtr.Zero, buffer.Pointer - 2, buffer.Pointer + buffer.Bytes,
            buffer.Pointer + buffer.Bytes + 2, buffer.Pointer + 1, new IntPtr(-1)
        })
            Assert.Throws<InvalidDataException>(() => strings.ReadString(pointer));
        // All tested addresses are rejected by arithmetic; no out-of-allocation memory is read.
        Assert.Equal("safe", strings.ReadString(buffer.Pointer));
    }

    [Fact]
    public void Native_string_decoder_rejects_missing_terminators()
    {
        using var buffer = new NativeBuffer("abc");
        var strings = new WindowsServiceNativeBuffer(buffer.Pointer, buffer.Bytes);
        Assert.Throws<InvalidDataException>(() => strings.ReadString(buffer.Pointer));
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void Native_string_decoder_rejects_invalid_utf16(int unpairedSurrogate)
    {
        using var buffer = new NativeBuffer(new string([(char)unpairedSurrogate, '\0']));
        var strings = new WindowsServiceNativeBuffer(buffer.Pointer, buffer.Bytes);
        Assert.Throws<InvalidDataException>(() => strings.ReadString(buffer.Pointer));
    }

    [Fact]
    public void Native_string_decoder_preserves_the_256_character_limit_without_accepting_truncated_data()
    {
        using var valid = new NativeBuffer(new string('x', 256) + "\0");
        Assert.Equal(256, new WindowsServiceNativeBuffer(valid.Pointer, valid.Bytes).ReadString(valid.Pointer).Length);
        using var oversized = new NativeBuffer(new string('x', 257) + "\0");
        Assert.Throws<InvalidDataException>(() => new WindowsServiceNativeBuffer(oversized.Pointer, oversized.Bytes)
            .ReadString(oversized.Pointer));
    }

    [Fact]
    public void Native_multisz_decoder_requires_its_terminators_and_enforces_item_cap()
    {
        using var valid = new NativeBuffer("First\0Second\0\0");
        Assert.Equal(["First", "Second"], new WindowsServiceNativeBuffer(valid.Pointer, valid.Bytes).ReadMultiString(valid.Pointer));
        using var empty = new NativeBuffer("\0\0");
        var strings = new WindowsServiceNativeBuffer(empty.Pointer, empty.Bytes);
        Assert.Empty(strings.ReadMultiString(empty.Pointer));
        Assert.Empty(strings.ReadMultiString(IntPtr.Zero));
        using var many = new NativeBuffer(string.Join('\0', Enumerable.Range(1, 40).Select(index => $"Svc{index}")) + "\0\0");
        var bounded = new WindowsServiceNativeBuffer(many.Pointer, many.Bytes).ReadMultiString(many.Pointer);
        Assert.Equal(32, bounded.Count);
        Assert.Equal("Svc32", bounded[^1]);
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\0X")]
    [InlineData("Service\0")]
    [InlineData("Service\0Next")]
    public void Native_multisz_decoder_rejects_malformed_end_without_crossing_buffer(string contents)
    {
        using var buffer = new NativeBuffer(contents);
        var strings = new WindowsServiceNativeBuffer(buffer.Pointer, buffer.Bytes);
        Assert.Throws<InvalidDataException>(() => strings.ReadMultiString(buffer.Pointer));
    }

    [Fact]
    public void Native_multisz_decoder_checks_remaining_data_even_after_32_items()
    {
        using var buffer = new NativeBuffer(string.Concat(Enumerable.Repeat("Safe\0", 32)) + "Unterminated");
        var strings = new WindowsServiceNativeBuffer(buffer.Pointer, buffer.Bytes);
        Assert.Throws<InvalidDataException>(() => strings.ReadMultiString(buffer.Pointer));
    }

    [Fact]
    public void Native_buffer_range_rejects_invalid_or_overflowing_boundaries_before_any_read()
    {
        using var buffer = new NativeBuffer("safe\0");
        Assert.Throws<InvalidDataException>(() => new WindowsServiceNativeBuffer(IntPtr.Zero, 2));
        Assert.Throws<InvalidDataException>(() => new WindowsServiceNativeBuffer(buffer.Pointer, 0));
        Assert.Throws<InvalidDataException>(() => new WindowsServiceNativeBuffer(buffer.Pointer, -2));
        Assert.Throws<InvalidDataException>(() => new WindowsServiceNativeBuffer(buffer.Pointer, 3));
        Assert.Throws<InvalidDataException>(() => new WindowsServiceNativeBuffer(buffer.Pointer + 1, 2));
        Assert.Throws<InvalidDataException>(() => new WindowsServiceNativeBuffer(new IntPtr(-2), 4));
        Assert.Throws<InvalidDataException>(() => new WindowsServiceNativeBuffer(buffer.Pointer, 256 * 1024 + 2));
    }

    private static WindowsServiceItem Item(string name) => new(name, $"Display {name}", "Running", "Automatic", ["Dependency"]);
    private static ToolRequest Request(string args) => new("test", "query_windows_services", args, "session");
    private sealed class Source(IReadOnlyList<WindowsServiceItem> items) : IWindowsServiceNativeSource
    {
        public int Calls;
        public WindowsServiceQueryResult Query(WindowsServiceQuery query, CancellationToken ct)
        { Calls++; return new(items, false, DateTimeOffset.UtcNow, "PRIVATE-NATIVE-MESSAGE"); }
    }
    private sealed class WaitingSource : IWindowsServiceNativeSource
    {
        public int Calls;
        public ManualResetEventSlim Release { get; } = new();
        public WindowsServiceQueryResult Query(WindowsServiceQuery query, CancellationToken ct)
        { Interlocked.Increment(ref Calls); Release.Wait(TimeSpan.FromSeconds(10)); return new([], false, DateTimeOffset.UtcNow); }
    }
    private sealed class ThrowingSource : IWindowsServiceNativeSource
    {
        public WindowsServiceQueryResult Query(WindowsServiceQuery query, CancellationToken ct) => throw new Win32Exception(5, "PRIVATE");
    }

    private sealed class NativeBuffer : IDisposable
    {
        public IntPtr Pointer { get; }
        public int Bytes { get; }

        public NativeBuffer(string contents)
        {
            // Write raw UTF-16 code units so malformed-surrogate fixtures are not replaced by Encoding.
            Bytes = contents.Length * 2;
            Pointer = Marshal.AllocHGlobal(Bytes);
            for (var index = 0; index < contents.Length; index++)
                Marshal.WriteInt16(Pointer, index * 2, unchecked((short)contents[index]));
        }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
}
