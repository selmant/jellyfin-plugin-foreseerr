using System.Diagnostics;
using System.Text.Json;
using Foreseerr.Jellyfin.Sidecar;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Foreseerr.Jellyfin.Tests;

public sealed class SidecarTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("foreseerr-sidecar-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void LogKeepsTheNewestLines()
    {
        var log = new SidecarLog();
        for (var i = 0; i < SidecarLog.Capacity + 5; i++) log.Add(SidecarLogLevel.Info, $"line {i}");
        var lines = log.Snapshot();
        Assert.Equal(SidecarLog.Capacity, lines.Count);
        Assert.Equal("line 5", lines[0].Message);
        Assert.Equal($"line {SidecarLog.Capacity + 4}", lines[^1].Message);
    }

    [Fact]
    public void LogCutsRunawayLines()
    {
        var log = new SidecarLog();
        log.Add(SidecarLogLevel.Warning, new string('x', SidecarLog.MaxLineLength * 2));
        Assert.Equal(SidecarLog.MaxLineLength + 1, log.Snapshot()[0].Message.Length);
    }

    [Fact]
    public async Task LeftoverRecordOfAnEndedProcessIsDropped()
    {
        var path = Path.Combine(_directory, "sidecar-process.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Pid = int.MaxValue, StartedAt = DateTimeOffset.UtcNow }));
        await SidecarProcess.StopLeftoverAsync(path, NullLogger.Instance);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task LeftoverCheckNeverStopsAnotherProgram()
    {
        // This test process has a recorded id but is not a Foreseerr.
        using var self = Process.GetCurrentProcess();
        var path = Path.Combine(_directory, "sidecar-process.json");
        SidecarProcess.Record(path, self);
        await SidecarProcess.StopLeftoverAsync(path, NullLogger.Instance);
        Assert.False(self.HasExited);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task LeftoverForeseerrIsStopped()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sleep")) return;
        // A Foreseerr-named process, as an earlier Jellyfin would have left it.
        var binary = Path.Combine(_directory, "foreseerr-linux-x64");
        File.Copy("/bin/sleep", binary);
        File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var leftover = Process.Start(binary, "300");
        var path = Path.Combine(_directory, "sidecar-process.json");
        SidecarProcess.Record(path, leftover);
        await SidecarProcess.StopLeftoverAsync(path, NullLogger.Instance);
        Assert.True(leftover.WaitForExit(TimeSpan.FromSeconds(5)));
        // SIGTERM, not a kill: a graceful stop gets its chance first.
        Assert.Equal(143, leftover.ExitCode);
        Assert.False(File.Exists(path));
    }
}

public class SidecarConsoleTests
{
    [Theory]
    [InlineData("2026-11-01T20:15:14.998Z [\u001b[32minfo\u001b[39m][Server]: Server ready on 127.0.0.1 port 46383/Foreseerr ",
        Microsoft.Extensions.Logging.LogLevel.Information, "[Server]: Server ready on 127.0.0.1 port 46383/Foreseerr")]
    [InlineData("2026-11-01T20:15:14.946Z [\u001b[34mdebug\u001b[39m][Settings Migrator]: Checking migration",
        Microsoft.Extensions.Logging.LogLevel.Debug, "[Settings Migrator]: Checking migration")]
    [InlineData("t [\u001b[33mwarn\u001b[39m]: Server ready {\"userId\":1}",
        Microsoft.Extensions.Logging.LogLevel.Warning, "Server ready {\"userId\":1}")]
    [InlineData("t [\u001b[31merror\u001b[39m][Jobs]: failed", Microsoft.Extensions.Logging.LogLevel.Error, "[Jobs]: failed")]
    [InlineData("thread 'main' panicked at src/main.rs", Microsoft.Extensions.Logging.LogLevel.Warning, "thread 'main' panicked at src/main.rs")]
    public void ConsoleLinesKeepTheirLevelAndLoseTheirColours(string line, Microsoft.Extensions.Logging.LogLevel level, string message) =>
        Assert.Equal((level, message), SidecarConsole.Parse(line, Microsoft.Extensions.Logging.LogLevel.Warning));
}
