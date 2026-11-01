using Foreseerr.Jellyfin.Configuration;
using Microsoft.AspNetCore.Http;

namespace Foreseerr.Jellyfin.Web;

/// <summary>Why the plugin refuses to open Foreseerr, with the HTTP status that says so.</summary>
public sealed record Refusal(int StatusCode, string Message);

/// <summary>
/// The plugin's own gate, in front of Foreseerr's user checks: the
/// administrator can turn Foreseerr off, or open it to administrators only.
/// </summary>
public static class PluginAccess
{
    public static readonly Refusal TurnedOff = new(StatusCodes.Status503ServiceUnavailable,
        "Foreseerr is turned off. A Jellyfin administrator can turn it on from the Foreseerr plugin page.");

    public static readonly Refusal AdministratorsOnly = new(StatusCodes.Status403Forbidden,
        "Only Jellyfin administrators can open Foreseerr right now.");

    /// <summary>The refusal for this user, or null when the plugin lets them in.</summary>
    public static Refusal? Check(PluginConfiguration settings, bool isAdministrator)
    {
        if (!settings.Enabled) return TurnedOff;
        if (settings.Access == AccessMode.Administrators && !isAdministrator) return AdministratorsOnly;
        return null;
    }
}
