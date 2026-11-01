using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Foreseerr.Jellyfin.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foreseerr.Jellyfin.Sidecar;

/// <summary>Where the running Foreseerr listens, and the secret it accepts. Every start gets new values.</summary>
public sealed record SidecarEndpoint(int Port, string Secret, Guid Generation)
{
    public string Origin => $"http://127.0.0.1:{Port}";
}

public enum SidecarState
{
    /// <summary>Started, not answering yet.</summary>
    Starting,

    /// <summary>Ready for requests.</summary>
    Running,

    /// <summary>Failed; the next start is scheduled.</summary>
    Waiting,

    /// <summary>Being stopped, for a restart or because it was turned off.</summary>
    Stopping,

    /// <summary>Turned off on the plugin page.</summary>
    Disabled,
}

/// <summary>What the plugin page shows about the Foreseerr process.</summary>
public sealed record SidecarStatus(
    SidecarState State,
    int? Pid,
    string? Binary,
    string? ForeseerrVersion,
    DateTimeOffset? ReadySince,
    DateTimeOffset? UnresponsiveSince,
    DateTimeOffset? NextAttempt,
    int Restarts,
    int? LastExitCode,
    string? LastError);

/// <summary>
/// Owns the Foreseerr process: starts it once Jellyfin is up, restarts it
/// after a crash (with backoff) or when a setting it reads changes, and stops
/// it gracefully with Jellyfin.
/// </summary>
public sealed class SidecarSupervisor(
    JellyfinHostBootstrap bootstrap,
    SidecarLog log,
    IHostApplicationLifetime lifetime,
    IHttpClientFactory clients,
    ILogger<SidecarSupervisor> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);

    /// <summary>A run that stayed ready this long restarts with the first retry delay.</summary>
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(2);

    /// <summary>Database migrations run before Foreseerr listens, so slow disks need a while.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cycle;
    private TaskCompletionSource _wake = NewWake();
    private SidecarSettings? _applied;
    private volatile SidecarEndpoint? _endpoint;

    // Status, guarded by _lock.
    private SidecarState _state = SidecarState.Starting;
    private int? _pid;
    private string? _binary;
    private string? _version;
    private DateTimeOffset? _readySince;
    private DateTimeOffset? _lastRunReady;
    private DateTimeOffset? _unresponsiveSince;
    private DateTimeOffset? _nextAttempt;
    private int _restarts;
    private int? _lastExitCode;
    private string? _lastError;

    /// <summary>The ready Foreseerr, or null while it is starting, failed, or turned off.</summary>
    public SidecarEndpoint? Endpoint => _endpoint;

    public SidecarStatus Status()
    {
        lock (_lock)
        {
            return new SidecarStatus(_state, _pid, _binary, _version, _readySince, _unresponsiveSince,
                _nextAttempt, _restarts, _lastExitCode, _lastError);
        }
    }

    /// <summary>Restarts Foreseerr now, or starts it at once if it is waiting after a failure.</summary>
    public void RequestRestart()
    {
        log.Add(SidecarLogLevel.Info, "Restart requested from the plugin page.");
        Interrupt();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A plugin must not hold up Jellyfin's HTTP listener while booting its child.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
        var plugin = ForeseerrPlugin.Instance
            ?? throw new InvalidOperationException("The Foreseerr plugin is not loaded.");
        plugin.ConfigurationChanged += OnConfigurationChanged;
        try
        {
            await SuperviseAsync(plugin, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }
    }

    private async Task SuperviseAsync(ForeseerrPlugin plugin, CancellationToken stoppingToken)
    {
        var retry = FirstRetry;
        var starts = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            Task wake;
            lock (_lock)
            {
                if (_wake.Task.IsCompleted) _wake = NewWake();
                wake = _wake.Task;
            }

            var settings = plugin.Configuration.ToSidecarSettings();
            if (!settings.Enabled)
            {
                lock (_lock)
                {
                    _applied = settings;
                    _state = SidecarState.Disabled;
                    _nextAttempt = null;
                }

                // Turned off also means no Foreseerr left over from a crashed Jellyfin.
                try
                {
                    await SidecarProcess.StopLeftoverAsync(ProcessFile, logger).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    lock (_lock) _lastError = ex.Message;
                    log.Add(SidecarLogLevel.Error, ex.Message);
                }

                await wake.WaitAsync(stoppingToken).ConfigureAwait(false);
                retry = FirstRetry;
                continue;
            }

            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            bool interrupted;
            lock (_lock)
            {
                // A settings change or restart request since the read above found no run to end:
                // read the settings again rather than start with the old ones.
                interrupted = wake.IsCompleted;
                if (!interrupted)
                {
                    _cycle = cycle;
                    _applied = settings;
                    _state = SidecarState.Starting;
                    _lastRunReady = null;
                    if (starts++ > 0) _restarts++;
                }
            }

            if (interrupted) continue;

            try
            {
                await RunProcessAsync(settings, cycle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cycle.IsCancellationRequested)
            {
                // A restart, a settings change, or Jellyfin shutting down. The process is stopped.
            }
            catch (Exception ex)
            {
                lock (_lock) _lastError = ex.Message;
                log.Add(SidecarLogLevel.Error, ex.Message);
                logger.LogError(ex, "Foreseerr sidecar failed; retrying in {Seconds}s", retry.TotalSeconds);
            }
            finally
            {
                lock (_lock) _cycle = null;
            }

            if (stoppingToken.IsCancellationRequested) break;
            if (cycle.IsCancellationRequested)
            {
                retry = FirstRetry;
                continue;
            }

            lock (_lock)
            {
                // Back off only while Foreseerr keeps failing soon after it starts.
                if (_lastRunReady is { } ready && DateTimeOffset.UtcNow - ready >= HealthyRun) retry = FirstRetry;
                _state = SidecarState.Waiting;
                _readySince = null;
                _nextAttempt = DateTimeOffset.UtcNow + retry;
            }

            // A restart request or a settings change ends the wait early, and starts the backoff over.
            var woken = await Task.WhenAny(wake, Task.Delay(retry, stoppingToken)).ConfigureAwait(false) == wake;
            retry = woken ? FirstRetry : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration current) return;
        var settings = current.ToSidecarSettings();
        bool wasEnabled;
        lock (_lock)
        {
            // Access and the header button apply to the next request.
            if (settings == _applied) return;
            wasEnabled = _applied?.Enabled != false;
        }

        var message = (wasEnabled, settings.Enabled) switch
        {
            (true, false) => "Foreseerr was turned off on the plugin page.",
            (false, true) => "Foreseerr was turned on on the plugin page.",
            // Still off: the new values apply when it is turned on.
            (false, false) => null,
            _ => "Settings changed; restarting Foreseerr.",
        };
        if (message != null) log.Add(SidecarLogLevel.Info, message);
        Interrupt();
    }

    /// <summary>Ends the current run (or wait); the loop then reads the settings again.</summary>
    private void Interrupt()
    {
        CancellationTokenSource? cycle;
        lock (_lock)
        {
            cycle = _cycle;
            _wake.TrySetResult();
        }

        try
        {
            // Stopping the process can take seconds; don't do it on the caller's request.
            _ = cycle?.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // That run already ended.
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string ProcessFile => Path.Combine(bootstrap.PluginDirectory, "sidecar-process.json");

    private async Task RunProcessAsync(SidecarSettings settings, CancellationToken cancellationToken)
    {
        await SidecarProcess.StopLeftoverAsync(ProcessFile, logger).ConfigureAwait(false);
        bootstrap.WriteHostFile(settings);
        var binary = ResolveBinaryPath() ?? throw new InvalidOperationException(
            "No Foreseerr sidecar binary for this OS/architecture in the plugin sidecar directory.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        // Only this process and its Foreseerr know the secret, and only until Foreseerr stops.
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var generation = Guid.NewGuid();
        var runtime = Path.GetDirectoryName(binary)!;
        var start = new ProcessStartInfo(binary)
        {
            WorkingDirectory = runtime,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.Environment["FORESEERR_PLUGIN"] = "1";
        start.Environment["FORESEERR_PLUGIN_SECRET"] = secret;
        start.Environment["FORESEERR_PLUGIN_INSTANCE"] = generation.ToString("N");
        start.Environment["FORESEERR_BASE_PATH"] = "/Foreseerr";
        start.Environment["FORESEERR_PUBLIC_BASE_PATH"] = bootstrap.GetBasePath() + "/Foreseerr";
        start.Environment["CONFIG_DIRECTORY"] = bootstrap.ConfigDirectory;
        start.Environment["HOST"] = "127.0.0.1";
        // The OS picks a free loopback port; the child reports it when ready.
        start.Environment["PORT"] = "0";
        start.Environment["NODE_ENV"] = "production";
        start.Environment.Remove("FORESEERR_RUNTIME");
        // The binaries share seerr-api.yml, dist/public and committag.json in
        // the sidecar directory, and run the whole backend themselves.
        start.Environment["FORESEERR_APP_ROOT"] = runtime;
        start.Environment.Remove("FORESEERR_PUBLIC_PATH");
        start.Environment["FORESEERR_STANDALONE"] = "1";

        using var process = new Process { StartInfo = start };
        var listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data?.StartsWith("FORESEERR_PLUGIN_READY ", StringComparison.Ordinal) == true)
            {
                try
                {
                    using var message = JsonDocument.Parse(args.Data["FORESEERR_PLUGIN_READY ".Length..]);
                    if (message.RootElement.GetProperty("instance").GetString() == generation.ToString("N"))
                    {
                        var port = message.RootElement.GetProperty("port").GetInt32();
                        if (port is > 0 and <= 65535) listening.TrySetResult(port);
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    // Not a readiness record for this start.
                }

                return;
            }

            Forward(args.Data, LogLevel.Information);
        };
        process.ErrorDataReceived += (_, args) => Forward(args.Data, LogLevel.Warning);

        var started = false;
        try
        {
            // Turned off or restarted while the leftover check or host file took their time.
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("Could not start Foreseerr.");
            started = true;
            lock (_lock)
            {
                _state = SidecarState.Starting;
                _pid = process.Id;
                _binary = Path.GetFileName(binary);
                _readySince = null;
                _unresponsiveSince = null;
                _nextAttempt = null;
            }

            log.Add(SidecarLogLevel.Info, $"Started {Path.GetFileName(binary)} as process {process.Id}.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                SidecarProcess.Record(ProcessFile, process);
            }
            catch (InvalidOperationException)
            {
                // It already exited; the exit below reports why.
            }

            var exit = process.WaitForExitAsync(cancellationToken);
            SidecarEndpoint endpoint;
            string? version;
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                startup.CancelAfter(StartupTimeout);
                try
                {
                    var bound = listening.Task.WaitAsync(startup.Token);
                    if (await Task.WhenAny(bound, exit).ConfigureAwait(false) == exit)
                    {
                        await exit.ConfigureAwait(false);
                        throw new InvalidOperationException(
                            $"Foreseerr exited with code {process.ExitCode} before opening its listener.");
                    }

                    endpoint = new SidecarEndpoint(await bound.ConfigureAwait(false), secret, generation);
                    version = await CheckAsync(endpoint, startup.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Foreseerr did not become ready within {StartupTimeout.TotalSeconds:0} seconds.");
                }
            }

            if (process.HasExited) throw new InvalidOperationException("Foreseerr exited during startup.");
            _endpoint = endpoint;
            lock (_lock)
            {
                _state = SidecarState.Running;
                _readySince = DateTimeOffset.UtcNow;
                _lastRunReady = _readySince;
                _version = version;
                _lastError = null;
            }

            logger.LogInformation("Foreseerr sidecar ready on {Origin}", endpoint.Origin);
            log.Add(SidecarLogLevel.Info, $"Foreseerr {version} is ready.");
            using (var watch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var watching = WatchAsync(endpoint, watch.Token);
                try
                {
                    await exit.ConfigureAwait(false);
                }
                finally
                {
                    await watch.CancelAsync().ConfigureAwait(false);
                    await watching.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }

            throw new InvalidOperationException($"Foreseerr exited with code {process.ExitCode}.");
        }
        finally
        {
            _endpoint = null;
            if (started) await StopAsync(process).ConfigureAwait(false);
        }
    }

    /// <summary>A Foreseerr console line into Jellyfin's log at its own level, and into the page's output.</summary>
    private void Forward(string? line, LogLevel fallback)
    {
        if (string.IsNullOrEmpty(line)) return;
        var (level, message) = SidecarConsole.Parse(line, fallback);
        if (message.Length == 0) return;
        logger.Log(level, "[foreseerr] {Line}", message);
        // Debug lines would push the useful ones out of the page's short history.
        if (level < LogLevel.Information) return;
        log.Add(level switch
        {
            LogLevel.Warning => SidecarLogLevel.Warning,
            >= LogLevel.Error => SidecarLogLevel.Error,
            _ => SidecarLogLevel.Info,
        }, message);
    }

    private async Task StopAsync(Process process)
    {
        lock (_lock) _state = SidecarState.Stopping;
        try
        {
            await SidecarProcess.StopAsync(process, logger).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception)
        {
            logger.LogWarning("Foreseerr sidecar {Pid} did not stop cleanly: {Message}", process.Id, ex.Message);
        }

        int? code = null;
        try
        {
            if (process.HasExited) code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // No exit status to report.
        }

        // A process that is still running stays recorded; the next start stops it.
        if (code != null) SidecarProcess.Forget(ProcessFile);
        lock (_lock)
        {
            _pid = null;
            _readySince = null;
            _unresponsiveSince = null;
            if (code != null) _lastExitCode = code;
        }

        log.Add(SidecarLogLevel.Info, code == null ? "Foreseerr did not stop." : $"Foreseerr stopped with exit code {code}.");
    }

    /// <summary>Notes when Foreseerr stops answering, without restarting it: long imports block it for minutes.</summary>
    private async Task WatchAsync(SidecarEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HealthInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var answered = true;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(HealthTimeout);
                await CheckAsync(endpoint, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                answered = false;
            }

            DateTimeOffset? since;
            lock (_lock)
            {
                since = _unresponsiveSince;
                _unresponsiveSince = answered ? null : since ?? DateTimeOffset.UtcNow;
            }

            if (!answered && since == null)
                log.Add(SidecarLogLevel.Warning, "Foreseerr is not answering its status check.");
            else if (answered && since != null)
                log.Add(SidecarLogLevel.Info, "Foreseerr is answering again.");
        }
    }

    /// <summary>Checks Foreseerr's status with the shared secret and returns its version.</summary>
    private async Task<string?> CheckAsync(SidecarEndpoint endpoint, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(PluginServiceRegistrator.SidecarHttpClient);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Origin + "/Foreseerr/api/v1/status");
        request.Headers.Add("X-Foreseerr-Plugin-Secret", endpoint.Secret);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        try
        {
            using var body = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                    ? version.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ResolveBinaryPath()
    {
        var directory = Path.GetDirectoryName(typeof(SidecarSupervisor).Assembly.Location);
        var name = (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, _, Architecture.X64) => "foreseerr-windows-x64.exe",
            (_, true, Architecture.X64) => "foreseerr-linux-x64",
            (_, true, Architecture.Arm64) => "foreseerr-linux-arm64",
            _ => null,
        };
        if (directory == null || name == null) return null;
        var path = Path.Combine(directory, "sidecar", name);
        return File.Exists(path) ? path : null;
    }
}
