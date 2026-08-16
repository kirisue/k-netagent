using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TestAgent.Core;

namespace TestAgent.Infrastructure;

public sealed record BackgroundCommandOptions(
    string StorageRoot,
    int MaxGlobalJobs = 4,
    int MaxJobsPerScope = 2,
    int MaxDurationSeconds = 600,
    long MaxOutputBytes = 1024 * 1024);

public sealed class BackgroundCommandService : IBackgroundCommandService, IDisposable
{
    private const long AbsoluteOutputLimit = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] SafeEnvironmentNames =
    [
        "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramData", "DOTNET_ROOT", "DOTNET_ROOT(x86)",
        "NUGET_PACKAGES", "LANG", "LC_ALL"
    ];

    private readonly string _storageRoot;
    private readonly int _maxGlobalJobs;
    private readonly int _maxJobsPerScope;
    private readonly int _maxDurationSeconds;
    private readonly long _maxOutputBytes;
    private readonly ConcurrentDictionary<string, LiveJob> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private bool _reconciled;
    private int _disposed;

    public BackgroundCommandService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestAgent",
            "background-commands"))
    {
    }

    public BackgroundCommandService(string storageRoot)
        : this(new BackgroundCommandOptions(storageRoot))
    {
    }

    public BackgroundCommandService(BackgroundCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.StorageRoot))
            throw new ArgumentException("A background command storage root is required.", nameof(options));

        _storageRoot = Path.GetFullPath(options.StorageRoot);
        _maxGlobalJobs = Math.Clamp(options.MaxGlobalJobs, 1, 4);
        _maxJobsPerScope = Math.Clamp(options.MaxJobsPerScope, 1, 2);
        _maxDurationSeconds = Math.Clamp(options.MaxDurationSeconds, 1, 600);
        _maxOutputBytes = Math.Clamp(options.MaxOutputBytes, 1024, AbsoluteOutputLimit);
    }

    public async Task<BackgroundCommandJob> StartAsync(
        BackgroundCommandSpec spec,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Validate(spec);
        cancellationToken.ThrowIfCancellationRequested();

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureReconciledUnderGateAsync(cancellationToken);
            var jobs = await LoadAllAsync(cancellationToken);
            var active = jobs.Where(job => IsActive(job.State)).ToArray();
            if (active.Length >= _maxGlobalJobs)
                throw new InvalidOperationException($"At most {_maxGlobalJobs} background commands may run at once.");
            if (active.Count(job => job.ScopeId.Equals(spec.ScopeId, StringComparison.OrdinalIgnoreCase)) >= _maxJobsPerScope)
                throw new InvalidOperationException($"At most {_maxJobsPerScope} background commands may run in scope '{spec.ScopeId}'.");

            var id = $"BG-{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow;
            var displayName = string.IsNullOrWhiteSpace(spec.DisplayName)
                ? $"{Path.GetFileName(spec.Executable)} background command"
                : SanitizeDisplayName(spec.DisplayName);
            var job = new BackgroundCommandJob(
                id,
                spec.ScopeId.Trim(),
                Path.GetFullPath(spec.Executable),
                Path.GetFullPath(spec.WorkingDirectory),
                displayName,
                BackgroundCommandState.Starting,
                now);
            await SaveAsync(job, cancellationToken);

            Process? process = null;
            try
            {
                process = new Process { StartInfo = CreateStartInfo(spec), EnableRaisingEvents = true };
                if (!process.Start()) throw new InvalidOperationException("The background process did not start.");
                process.StandardInput.Close();

                // From this point the command has its own lifetime. The Agent turn's token must not own it.
                job = job with
                {
                    State = BackgroundCommandState.Running,
                    StartedAt = DateTimeOffset.UtcNow,
                    ProcessId = process.Id
                };
                await SaveAsync(job, CancellationToken.None);

                var live = new LiveJob(job, process);
                if (!_live.TryAdd(id, live))
                    throw new InvalidOperationException("Could not register the background process.");

                var timeout = Math.Clamp(spec.TimeoutSeconds, 1, _maxDurationSeconds);
                live.MonitorTask = MonitorAsync(live, timeout);
                return job;
            }
            catch (Exception ex)
            {
                if (process is not null)
                {
                    TryKill(process);
                    process.Dispose();
                }
                var failed = job with
                {
                    State = BackgroundCommandState.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ProcessId = null,
                    Error = SafeError(ex)
                };
                await SaveAsync(failed, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<BackgroundCommandJob?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateJobId(jobId);
        cancellationToken.ThrowIfCancellationRequested();
        if (_live.TryGetValue(jobId, out var live)) return live.Snapshot;
        return await LoadAsync(jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<BackgroundCommandJob>> ListAsync(
        string? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var jobs = await LoadAllAsync(cancellationToken);
        var merged = jobs.Select(job => _live.TryGetValue(job.Id, out var live) ? live.Snapshot : job);
        if (!string.IsNullOrWhiteSpace(scopeId))
            merged = merged.Where(job => job.ScopeId.Equals(scopeId, StringComparison.OrdinalIgnoreCase));
        return merged.OrderByDescending(job => job.CreatedAt).ToArray();
    }

    public async Task<BackgroundCommandOutputChunk> ReadOutputAsync(
        string jobId,
        long cursor = 0,
        int maxBytes = 64 * 1024,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateJobId(jobId);
        if (cursor < 0) throw new ArgumentOutOfRangeException(nameof(cursor));
        var job = await GetAsync(jobId, cancellationToken)
            ?? throw new KeyNotFoundException($"Background command '{jobId}' was not found.");
        var path = OutputPath(jobId);
        if (!File.Exists(path))
            return new(jobId, 0, 0, string.Empty, IsTerminal(job.State), job.OutputTruncated);

        maxBytes = Math.Clamp(maxBytes, 256, 256 * 1024);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var start = Math.Min(cursor, stream.Length);
        stream.Position = start;
        var remaining = (int)Math.Min(maxBytes, stream.Length - start);
        var buffer = new byte[remaining];
        var read = remaining == 0
            ? 0
            : await stream.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
        var usable = ValidUtf8PrefixLength(buffer, read);
        var content = usable == 0 ? string.Empty : StrictUtf8.GetString(buffer, 0, usable);
        var next = start + usable;
        var currentLength = stream.Length;
        var truncated = job.OutputTruncated ||
            (_live.TryGetValue(jobId, out var live) && live.OutputTruncated);
        return new(
            jobId,
            start,
            next,
            content,
            IsTerminal(job.State) && next >= currentLength,
            truncated);
    }

    public async Task<BackgroundCommandJob> StopAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateJobId(jobId);
        LiveJob? live = null;

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var stored = await LoadAsync(jobId, cancellationToken)
                ?? throw new KeyNotFoundException($"Background command '{jobId}' was not found.");
            if (!_live.TryGetValue(jobId, out live))
            {
                if (IsActive(stored.State))
                {
                    // It belongs to an earlier process lifetime. Never reconnect to or kill its raw PID.
                    stored = stored with
                    {
                        State = BackgroundCommandState.NeedsReview,
                        ProcessId = null,
                        CompletedAt = DateTimeOffset.UtcNow,
                        Error = "The application restarted while this command was active; verify its external effects manually."
                    };
                    await SaveAsync(stored, cancellationToken);
                }
                return stored;
            }

            live.StopRequested = true;
            live.Update(live.Snapshot with { State = BackgroundCommandState.Stopping });
            await SaveAsync(live.Snapshot, cancellationToken);
            TryKill(live.Process);
        }
        finally
        {
            _operationGate.Release();
        }

        try { return await live!.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (TimeoutException)
        {
            if(live!.Completion.Task.IsCompletedSuccessfully)return await live.Completion.Task;
            var stopping=live.Snapshot with{State=BackgroundCommandState.Stopping,Error="Termination has not been confirmed yet; this job remains active and may be stopped again."};
            live.Update(stopping);await SaveAsync(stopping,CancellationToken.None);return stopping;
        }
    }

    public async Task<IReadOnlyList<BackgroundCommandJob>> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var result = await ReconcileUnderGateAsync(cancellationToken);
            _reconciled = true;
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var live in _live.Values)
        {
            live.StopRequested = true;
            TryKill(live.Process);
        }
    }

    private async Task MonitorAsync(LiveJob live, int timeoutSeconds)
    {
        Exception? monitorError = null;
        using var timerCancellation = new CancellationTokenSource();
        try
        {
            var stdout = PumpAsync(live, live.Process.StandardOutput, "[stdout] ");
            var stderr = PumpAsync(live, live.Process.StandardError, "[stderr] ");
            var exit = live.Process.WaitForExitAsync();
            var timeout = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), timerCancellation.Token);
            if (await Task.WhenAny(exit, timeout) == timeout)
            {
                live.TimedOut = true;
                TryKill(live.Process);
            }
            else
            {
                timerCancellation.Cancel();
            }

            try { await exit; }
            catch (Exception ex) { monitorError = ex; }
            await Task.WhenAll(stdout, stderr);
            if (monitorError is null && !string.IsNullOrWhiteSpace(live.PumpError))
                monitorError = new IOException("Background output could not be captured completely: " + live.PumpError);
        }
        catch (Exception ex)
        {
            monitorError = ex;
            TryKill(live.Process);
        }

        int? exitCode = null;
        try { if (live.Process.HasExited) exitCode = live.Process.ExitCode; }
        catch (InvalidOperationException) { }

        var state = live.TimedOut
            ? BackgroundCommandState.TimedOut
            : live.StopRequested
                ? BackgroundCommandState.Stopped
                : monitorError is not null
                    ? BackgroundCommandState.Failed
                    : exitCode == 0
                        ? BackgroundCommandState.Completed
                        : BackgroundCommandState.Failed;
        var error = monitorError is not null
            ? SafeError(monitorError)
            : state == BackgroundCommandState.Failed
                ? $"Process exited with code {exitCode?.ToString() ?? "unknown"}."
                : state == BackgroundCommandState.TimedOut
                    ? $"The command exceeded its {timeoutSeconds}-second time limit."
                    : null;
        var completed = live.Snapshot with
        {
            State = state,
            CompletedAt = DateTimeOffset.UtcNow,
            ProcessId = null,
            ExitCode = exitCode,
            OutputTruncated = live.OutputTruncated,
            Error = error
        };
        live.Update(completed);
        try { await SaveAsync(completed, CancellationToken.None); }
        catch (Exception ex)
        {
            completed = completed with
            {
                State = BackgroundCommandState.NeedsReview,
                Error = $"The final command state could not be persisted: {SafeError(ex)}"
            };
            live.Update(completed);
        }
        _live.TryRemove(completed.Id, out _);
        live.Completion.TrySetResult(completed);
        live.Process.Dispose();
        live.OutputGate.Dispose();
    }

    private async Task PumpAsync(LiveJob live, StreamReader reader, string channelPrefix)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory());
                if (count == 0) return;
                await AppendOutputAsync(live, channelPrefix + new string(buffer, 0, count));
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException ex) { live.PumpError = SafeError(ex); }
    }

    private async Task AppendOutputAsync(LiveJob live, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await live.OutputGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_storageRoot);
            var path = OutputPath(live.Snapshot.Id);
            await using var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous);
            stream.Position = stream.Length;
            var capacity = _maxOutputBytes - stream.Length;
            if (capacity <= 0)
            {
                live.MarkOutputTruncated();
                return;
            }

            var requested = (int)Math.Min(capacity, bytes.Length);
            var usable = ValidUtf8PrefixLength(bytes, requested);
            if (usable > 0) await stream.WriteAsync(bytes.AsMemory(0, usable));
            if (usable < bytes.Length) live.MarkOutputTruncated();
        }
        finally
        {
            live.OutputGate.Release();
        }
    }

    private async Task EnsureReconciledUnderGateAsync(CancellationToken cancellationToken)
    {
        if (_reconciled) return;
        await ReconcileUnderGateAsync(cancellationToken);
        _reconciled = true;
    }

    private async Task<IReadOnlyList<BackgroundCommandJob>> ReconcileUnderGateAsync(
        CancellationToken cancellationToken)
    {
        var jobs = await LoadAllAsync(cancellationToken);
        var result = new List<BackgroundCommandJob>(jobs.Count);
        foreach (var job in jobs)
        {
            if (IsActive(job.State) && !_live.ContainsKey(job.Id))
            {
                var reconciled = job with
                {
                    State = BackgroundCommandState.NeedsReview,
                    ProcessId = null,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = "The application restarted while this command was active; verify its external effects manually."
                };
                await SaveAsync(reconciled, cancellationToken);
                result.Add(reconciled);
            }
            else
            {
                result.Add(_live.TryGetValue(job.Id, out var live) ? live.Snapshot : job);
            }
        }
        return result.OrderByDescending(job => job.CreatedAt).ToArray();
    }

    private ProcessStartInfo CreateStartInfo(BackgroundCommandSpec spec)
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(spec.Executable),
            WorkingDirectory = Path.GetFullPath(spec.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (var name in SafeEnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) info.Environment[name] = value;
        }
        return info;
    }

    private async Task<IReadOnlyList<BackgroundCommandJob>> LoadAllAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storageRoot);
        var result = new List<BackgroundCommandJob>();
        foreach (var path in Directory.EnumerateFiles(_storageRoot, "BG-*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = JsonSerializer.Deserialize<BackgroundCommandJob>(
                    await File.ReadAllTextAsync(path, cancellationToken), Json);
                if (value is not null) result.Add(value);
            }
            catch (JsonException)
            {
                Quarantine(path);
            }
        }
        return result;
    }

    private async Task<BackgroundCommandJob?> LoadAsync(string jobId, CancellationToken cancellationToken)
    {
        var path = JobPath(jobId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<BackgroundCommandJob>(
                await File.ReadAllTextAsync(path, cancellationToken), Json);
        }
        catch (JsonException)
        {
            Quarantine(path);
            return null;
        }
    }

    private async Task SaveAsync(BackgroundCommandJob job, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storageRoot);
        await _storageGate.WaitAsync(cancellationToken);
        try
        {
            var path = JobPath(job.Id);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(job, Json),
                    new UTF8Encoding(false),
                    cancellationToken);
                File.Move(temporary, path, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }
        finally
        {
            _storageGate.Release();
        }
    }

    private string JobPath(string jobId) => Path.Combine(_storageRoot, jobId + ".json");
    private string OutputPath(string jobId) => Path.Combine(_storageRoot, jobId + ".output.log");

    private static void Validate(BackgroundCommandSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.ScopeId) || spec.ScopeId.Length > 200 || spec.ScopeId.Any(char.IsControl))
            throw new ArgumentException("A valid scope ID is required.", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Executable) || !Path.IsPathFullyQualified(spec.Executable))
            throw new ArgumentException("The executable must be an explicit absolute path.", nameof(spec));
        if (!File.Exists(spec.Executable)) throw new FileNotFoundException("The executable was not found.", spec.Executable);
        if (IsShellExecutable(spec.Executable))
            throw new InvalidOperationException("Interactive shells and command processors are not background-command executables.");
        if (string.IsNullOrWhiteSpace(spec.WorkingDirectory) || !Path.IsPathFullyQualified(spec.WorkingDirectory))
            throw new ArgumentException("The working directory must be an explicit absolute path.", nameof(spec));
        if (!Directory.Exists(spec.WorkingDirectory))
            throw new DirectoryNotFoundException($"Working directory '{spec.WorkingDirectory}' was not found.");
        if (spec.Arguments is null || spec.Arguments.Any(argument => argument is null || argument.IndexOf('\0') >= 0))
            throw new ArgumentException("Command arguments may not contain null values or null characters.", nameof(spec));
    }

    private static bool IsShellExecutable(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        return name.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("wsl", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeDisplayName(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).Take(160).ToArray()).Trim();
        return clean.Length == 0 ? "Background command" : clean;
    }

    private static void ValidateJobId(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 80 || !jobId.StartsWith("BG-", StringComparison.Ordinal) ||
            jobId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("The background command ID is invalid.", nameof(jobId));
    }

    private static int ValidUtf8PrefixLength(byte[] bytes, int count)
    {
        if (count == 0) return 0;
        for (var length = count; length >= Math.Max(0, count - 3); length--)
        {
            try
            {
                StrictUtf8.GetCharCount(bytes, 0, length);
                return length;
            }
            catch (DecoderFallbackException) { }
        }
        return 0;
    }

    private static bool IsActive(BackgroundCommandState state) =>
        state is BackgroundCommandState.Starting or BackgroundCommandState.Running or BackgroundCommandState.Stopping;

    private static bool IsTerminal(BackgroundCommandState state) => !IsActive(state);

    private static string SafeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 500 ? message : message[..500];
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void Quarantine(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, path + ".corrupt." + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true);
        }
        catch { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class LiveJob(BackgroundCommandJob job, Process process)
    {
        private readonly object _sync = new();
        private BackgroundCommandJob _job = job;
        private int _outputTruncated;

        public Process Process { get; } = process;
        public SemaphoreSlim OutputGate { get; } = new(1, 1);
        public TaskCompletionSource<BackgroundCommandJob> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task MonitorTask { get; set; } = Task.CompletedTask;
        public bool StopRequested { get; set; }
        public bool TimedOut { get; set; }
        public string? PumpError { get; set; }
        public bool OutputTruncated => Volatile.Read(ref _outputTruncated) != 0;
        public BackgroundCommandJob Snapshot { get { lock (_sync) return _job; } }
        public void Update(BackgroundCommandJob value) { lock (_sync) _job = value; }
        public void MarkOutputTruncated()
        {
            Interlocked.Exchange(ref _outputTruncated, 1);
            lock (_sync) _job = _job with { OutputTruncated = true };
        }
    }
}
