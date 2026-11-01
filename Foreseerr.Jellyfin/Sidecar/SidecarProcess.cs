using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Foreseerr.Jellyfin.Sidecar;

/// <summary>
/// Stopping the Foreseerr process, and finding one an earlier Jellyfin
/// process left running (after a crash, or on Windows, where children
/// outlive their parent). Two Foreseerr processes must never share a database.
/// </summary>
internal static class SidecarProcess
{
    /// <summary>How long Foreseerr gets to finish its requests and close its database.</summary>
    public static readonly TimeSpan GracefulStop = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(30);

    /// <summary>Asks Foreseerr to stop (SIGTERM), and kills it if it does not in time.</summary>
    public static async Task StopAsync(Process process, ILogger logger)
    {
        if (process.HasExited) return;
        // Foreseerr only stops gracefully on a signal; Windows has none to send.
        if (!OperatingSystem.IsWindows() && Signals.Terminate(process.Id))
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(GracefulStop).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Foreseerr {Pid} did not stop within {Seconds}s; killing it",
                    process.Id, GracefulStop.TotalSeconds);
            }
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KillWait).ConfigureAwait(false);
    }

    /// <summary>Remembers the running process, so the next start can find it if Jellyfin dies.</summary>
    /// <exception cref="InvalidOperationException">The process already exited.</exception>
    public static void Record(string path, Process process)
    {
        var record = new ProcessRecord(process.Id, process.StartTime.ToUniversalTime());
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
            JsonSerializer.Serialize(stream, record);
        File.Move(temporary, path, overwrite: true);
    }

    public static void Forget(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start checks the record again.
        }
    }

    /// <summary>Stops the process in the record if it is still the Foreseerr that was started.</summary>
    /// <exception cref="InvalidOperationException">It still runs and could not be stopped; the record stays.</exception>
    public static async Task StopLeftoverAsync(string path, ILogger logger)
    {
        ProcessRecord? record;
        try
        {
            if (!File.Exists(path)) return;
            await using var stream = File.OpenRead(path);
            record = await JsonSerializer.DeserializeAsync<ProcessRecord>(stream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            record = null;
        }

        if (record is { Pid: > 0 } && Find(record) is { } process)
        {
            using (process)
            {
                logger.LogWarning("Stopping Foreseerr {Pid}, left running by an earlier Jellyfin process", record.Pid);
                try
                {
                    await StopAsync(process, logger).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
                {
                    logger.LogWarning("Could not stop the earlier Foreseerr {Pid}: {Message}", record.Pid, ex.Message);
                }

                // Starting another would put two Foreseerr processes on one database.
                if (!HasExited(process))
                    throw new InvalidOperationException(
                        $"An earlier Foreseerr (process {record.Pid}) is still running and could not be stopped.");
            }
        }

        Forget(path);
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// The recorded process, if it still runs. The name and start time rule
    /// out a process that got the same id later, for example in a new container.
    /// </summary>
    /// <remarks>
    /// Start times are compared loosely: on Linux they derive from the boot
    /// time, which moves when the clock is stepped. Another Foreseerr with the
    /// same id that started within that window does not happen in practice.
    /// </remarks>
    private static Process? Find(ProcessRecord record)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(record.Pid);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            if (process.ProcessName.StartsWith("foreseerr", StringComparison.OrdinalIgnoreCase)
                && (process.StartTime.ToUniversalTime() - record.StartedAt).Duration() < StartTimeTolerance)
                return process;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Exited meanwhile, or not ours to inspect.
        }

        process.Dispose();
        return null;
    }

    internal sealed record ProcessRecord(int Pid, DateTimeOffset StartedAt);

    /// <summary>POSIX kill(2), resolved at run time so a missing libc only costs the graceful stop.</summary>
    private static class Signals
    {
        private const int SigTerm = 15;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int KillFunction(int pid, int signal);

        private static readonly Lazy<KillFunction?> Kill = new(Resolve);

        public static bool Terminate(int pid) => Kill.Value?.Invoke(pid, SigTerm) == 0;

        private static KillFunction? Resolve()
        {
            foreach (var name in (string[])["libc.so.6", "libc"])
            {
                if (NativeLibrary.TryLoad(name, out var library)
                    && NativeLibrary.TryGetExport(library, "kill", out var address))
                    return Marshal.GetDelegateForFunctionPointer<KillFunction>(address);
            }

            return null;
        }
    }
}
