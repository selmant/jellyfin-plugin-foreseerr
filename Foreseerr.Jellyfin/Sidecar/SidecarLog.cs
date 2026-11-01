namespace Foreseerr.Jellyfin.Sidecar;

/// <summary>
/// The last lines Foreseerr and the supervisor wrote, for the plugin page.
/// Everything also goes to Jellyfin's log; this copy survives only until
/// Jellyfin restarts.
/// </summary>
public sealed class SidecarLog
{
    public const int Capacity = 500;

    /// <summary>Longer lines are cut, so one runaway line cannot fill the page.</summary>
    public const int MaxLineLength = 2000;

    private readonly Lock _lock = new();
    private readonly Queue<SidecarLogLine> _lines = new(Capacity);

    internal TimeProvider Time { get; init; } = TimeProvider.System;

    public void Add(SidecarLogLevel level, string message)
    {
        if (message.Length > MaxLineLength) message = message[..MaxLineLength] + "…";
        var line = new SidecarLogLine(Time.GetUtcNow(), level, message);
        lock (_lock)
        {
            if (_lines.Count == Capacity) _lines.Dequeue();
            _lines.Enqueue(line);
        }
    }

    public IReadOnlyList<SidecarLogLine> Snapshot()
    {
        lock (_lock) return [.. _lines];
    }
}

public enum SidecarLogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record SidecarLogLine(DateTimeOffset Time, SidecarLogLevel Level, string Message);
