using KNetAgent.Desktop.WinUI.Features.WindowsServices;
using KNetAgent.Desktop.WinUI.Services;
using TestAgent.Core;
using TestAgent.Infrastructure;
using Xunit;

namespace TestAgent.Tests;

public sealed class WindowsServiceCenterViewModelTests
{
    [Fact]
    public async Task Open_performs_no_read_or_automatic_draft_action()
    {
        var reader = new Reader();
        var stages = 0;
        var vm = Create(reader);
        vm.Open(_ => { stages++; return Task.CompletedTask; });
        Assert.Equal(0, reader.Calls);
        Assert.Equal(0, stages);
        Assert.Empty(vm.Services);
        Assert.True(vm.CanQuery);
        Assert.False(vm.CanStage);
        await vm.StageEvidenceCommand.ExecuteAsync();
        Assert.Equal(0, stages);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task Query_forwards_structured_filters_and_bounds_visible_rows()
    {
        var reader = new Reader { Result = Enumerable.Range(0, 150).Select(i => Item($"Service{i}")).ToArray() };
        var vm = Create(reader);
        vm.Open(_ => Task.CompletedTask);
        vm.Filter = " Display ";
        vm.MaxServices = "100";
        await vm.QueryCommand.ExecuteAsync();
        Assert.Equal(new WindowsServiceQuery(Filter: "Display", MaxServices: 100), reader.LastQuery);
        Assert.Equal(100, vm.Services.Count);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanQuery);
        Assert.False(vm.CanStage);
        vm.Filter = "";
        vm.Name = " Service7 ";
        await vm.QueryAsync();
        Assert.Equal(new WindowsServiceQuery(Name: "Service7", MaxServices: 100), reader.LastQuery);
        await vm.CloseAsync();
    }

    [Theory]
    [InlineData("0", "", "")]
    [InlineData("101", "", "")]
    [InlineData("not-a-number", "", "")]
    [InlineData("50", "Exact", "Filter")]
    public async Task Invalid_input_does_not_reach_reader(string limit, string name, string filter)
    {
        var reader = new Reader();
        var vm = Create(reader);
        vm.Open(_ => Task.CompletedTask);
        vm.MaxServices = limit;
        vm.Name = name;
        vm.Filter = filter;
        await vm.QueryAsync();
        Assert.Equal(0, reader.Calls);
        Assert.False(vm.IsBusy);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task Pending_query_blocks_duplicate_reads_and_close_cancels_then_reopen_works()
    {
        var pending = new TaskCompletionSource<WindowsServiceQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { Pending = pending };
        var vm = Create(reader);
        vm.Open(_ => Task.CompletedTask);
        var query = vm.QueryAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanQuery);
        Assert.False(vm.CanStage);
        await vm.QueryAsync();
        Assert.Equal(1, reader.Calls);
        await vm.CloseAsync();
        await query;
        Assert.True(reader.LastToken.IsCancellationRequested);
        Assert.Empty(vm.Services);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanQuery);
        Assert.False(vm.CanStage);
        reader.Pending = null;
        vm.Open(_ => Task.CompletedTask);
        Assert.Equal(1, reader.Calls);
        await vm.QueryAsync();
        Assert.Equal(2, reader.Calls);
        Assert.Single(vm.Services);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task A_late_reader_result_after_close_cannot_repopulate_closed_view()
    {
        var pending = new TaskCompletionSource<WindowsServiceQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { Pending = pending, IgnoreCancellation = true };
        var vm = Create(reader);
        vm.Open(_ => Task.CompletedTask);
        var query = vm.QueryAsync();
        var close = vm.CloseAsync();
        pending.SetResult(new([Item("Late")], false, Now));
        await Task.WhenAll(query, close);
        Assert.Empty(vm.Services);
        Assert.Null(vm.SelectedService);
        Assert.False(vm.CanStage);
        Assert.False(vm.CanQuery);
    }

    [Fact]
    public async Task Selected_only_evidence_is_snapshotted_and_staged_exactly_once_on_explicit_command()
    {
        var reader = new Reader { Result = [Item("First"), Item("Second")] };
        var evidence = new Evidence();
        var vm = Create(reader, evidence);
        var drafts = new List<string>();
        vm.Open(text => { drafts.Add(text); return Task.CompletedTask; });
        await vm.QueryAsync();
        Assert.False(vm.CanStage);
        Assert.Equal(0, evidence.Calls);
        await vm.StageEvidenceCommand.ExecuteAsync();
        Assert.Empty(drafts);
        vm.SelectedService = vm.Services[1];
        var displayedPreview = vm.EvidencePreview;
        Assert.Equal("Second", evidence.LastItem!.Name);
        Assert.Equal(Now, evidence.LastTime);
        Assert.DoesNotContain("First", displayedPreview);
        Assert.True(vm.CanStage);
        Assert.Empty(drafts);
        await vm.StageEvidenceCommand.ExecuteAsync();
        Assert.Equal(displayedPreview, Assert.Single(drafts));
        Assert.Equal(1, evidence.Calls); // Stage copies the reviewed text, rather than regenerating it.
        vm.SelectedService = null;
        Assert.False(vm.CanStage);
        await vm.StageEvidenceCommand.ExecuteAsync();
        Assert.Single(drafts);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task Arbitrary_or_prequery_selection_is_not_eligible_to_share()
    {
        var vm = Create(new Reader());
        var calls = 0;
        vm.Open(_ => { calls++; return Task.CompletedTask; });
        vm.SelectedService = new(Item("Invented"));
        Assert.False(vm.CanStage);
        await vm.QueryAsync();
        vm.SelectedService = new(Item("Invented"));
        Assert.False(vm.CanStage);
        await vm.StageEvidenceCommand.ExecuteAsync();
        Assert.Equal(0, calls);
        await vm.CloseAsync();
    }

    [Fact]
    public async Task Real_evidence_builder_redacts_the_draft_while_local_detail_stays_readable()
    {
        var item = Item("Service") with { DisplayName = @"C:\Users\private-person\service.exe" };
        var vm = Create(new Reader { Result = [item] }, new WindowsServiceEvidenceBuilder());
        string? draft = null;
        vm.Open(text => { draft = text; return Task.CompletedTask; });
        await vm.QueryAsync();
        vm.SelectedService = vm.Services[0];
        Assert.Contains("private-person", vm.SelectedDetail);
        Assert.DoesNotContain("private-person", vm.EvidencePreview);
        Assert.Null(draft);
        await vm.StageEvidenceCommand.ExecuteAsync();
        Assert.Equal(vm.EvidencePreview, draft);
        Assert.DoesNotContain("private-person", draft);
        await vm.CloseAsync();
    }

    [Theory]
    [InlineData("denied", "未允许")]
    [InlineData("timeout", "时间预算")]
    [InlineData("other", "无法读取")]
    public async Task Reader_failures_leave_query_available_without_exposing_exception_text(string kind, string safeText)
    {
        var reader = new Reader
        {
            Error = kind switch
            {
                "denied" => new UnauthorizedAccessException("PRIVATE-ERROR"),
                "timeout" => new TimeoutException("PRIVATE-ERROR"),
                _ => new InvalidOperationException("PRIVATE-ERROR")
            }
        };
        var vm = Create(reader);
        vm.Open(_ => Task.CompletedTask);
        await vm.QueryAsync();
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanQuery);
        Assert.False(vm.CanStage);
        Assert.Empty(vm.Services);
        Assert.Contains(safeText, vm.Status);
        Assert.DoesNotContain("PRIVATE-ERROR", vm.Status);
        await vm.CloseAsync();
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private static WindowsServiceItem Item(string name) => new(name, $"Display {name}", "Stopped", "Manual", []);
    private static ServiceCenterViewModel Create(Reader reader, IWindowsServiceEvidenceBuilder? evidence = null) =>
        new(reader, evidence ?? new Evidence(), new InlineUiDispatcher());

    private sealed class Reader : IWindowsServiceReader
    {
        public int Calls;
        public WindowsServiceQuery? LastQuery;
        public CancellationToken LastToken;
        public IReadOnlyList<WindowsServiceItem> Result = [Item("Default")];
        public Exception? Error;
        public TaskCompletionSource<WindowsServiceQueryResult>? Pending;
        public bool IgnoreCancellation;
        public Task<WindowsServiceQueryResult> QueryAsync(WindowsServiceQuery query, CancellationToken ct = default)
        {
            Calls++; LastQuery = query; LastToken = ct;
            if (Error is not null) return Task.FromException<WindowsServiceQueryResult>(Error);
            if (Pending is not null) return IgnoreCancellation ? Pending.Task : Pending.Task.WaitAsync(ct);
            return Task.FromResult(new WindowsServiceQueryResult(Result, Result.Count > query.MaxServices, Now));
        }
    }

    private sealed class Evidence : IWindowsServiceEvidenceBuilder
    {
        public int Calls;
        public WindowsServiceItem? LastItem;
        public DateTimeOffset LastTime;
        public string Build(WindowsServiceItem service, DateTimeOffset queriedAt)
        {
            Calls++; LastItem = service; LastTime = queriedAt;
            return $"selected={service.Name}; snapshot={queriedAt:O}; build={Calls}";
        }
    }
}
