using System.Reflection;
using Foreseerr.Jellyfin.Configuration;
using Foreseerr.Jellyfin.Sidecar;
using Foreseerr.Jellyfin.Web;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Foreseerr.Jellyfin.Api;

/// <summary>
/// The plugin's own API under /ForeseerrPlugin: the plugin page's status,
/// restart and log view, and what Jellyfin Web's header button needs.
/// Foreseerr itself is served by <see cref="ForeseerrProxyController"/>.
/// </summary>
[ApiController]
[Route("ForeseerrPlugin")]
public class ForeseerrPluginController(
    SidecarSupervisor supervisor,
    SidecarLog log,
    JellyfinHostBootstrap bootstrap,
    IAuthService auth) : ControllerBase
{
    private const string RequiresElevation = "RequiresElevation";

    [HttpGet("loader.js")]
    [AllowAnonymous]
    public IActionResult Loader()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Foreseerr.Jellyfin.Web.loader.js");
        if (stream == null) return NotFound();
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/javascript");
    }

    [HttpGet("Status")]
    [Authorize(Policy = RequiresElevation)]
    public IActionResult Status()
    {
        var status = supervisor.Status();
        var endpoint = supervisor.Endpoint;
        var settings = Settings;
        return Ok(new
        {
            state = status.State switch
            {
                SidecarState.Starting => "starting",
                SidecarState.Running => "running",
                SidecarState.Waiting => "waiting",
                SidecarState.Stopping => "stopping",
                SidecarState.Disabled => "disabled",
                _ => "unknown",
            },
            running = status.Pid != null,
            ready = endpoint != null,
            pid = status.Pid,
            sidecarPort = endpoint?.Port ?? 0,
            binary = status.Binary,
            foreseerrVersion = status.ForeseerrVersion,
            readySince = status.ReadySince,
            unresponsiveSince = status.UnresponsiveSince,
            nextAttempt = status.NextAttempt,
            restarts = status.Restarts,
            lastExitCode = status.LastExitCode,
            lastError = status.LastError,
            version = typeof(ForeseerrPlugin).Assembly.GetName().Version?.ToString(),
            url = Request.PathBase + "/Foreseerr/",
            publicUrl = bootstrap.PublicUrl(settings.PublicServerUrl),
            headerButton = HeaderButtonRegistration.Registered,
            dataDirectory = bootstrap.ConfigDirectory,
        });
    }

    [HttpPost("Restart")]
    [Authorize(Policy = RequiresElevation)]
    public IActionResult Restart()
    {
        if (!Settings.Enabled) return Problem(PluginAccess.TurnedOff.Message, statusCode: StatusCodes.Status409Conflict);
        supervisor.RequestRestart();
        return Accepted();
    }

    [HttpGet("Logs")]
    [Authorize(Policy = RequiresElevation)]
    public IActionResult Logs() => Ok(new
    {
        lines = log.Snapshot().Select(line => new
        {
            time = line.Time,
            level = line.Level switch
            {
                SidecarLogLevel.Warning => "warning",
                SidecarLogLevel.Error => "error",
                _ => "info",
            },
            message = line.Message,
        }),
    });

    /// <summary>What Jellyfin Web shows the signed-in user: the header button, or nothing.</summary>
    [HttpGet("Client")]
    [Authorize]
    public async Task<IActionResult> Client()
    {
        var authorization = await auth.Authenticate(Request).ConfigureAwait(false);
        var settings = Settings;
        var allowed = authorization.User != null
            && PluginAccess.Check(settings, authorization.User.HasPermission(PermissionKind.IsAdministrator)) == null;
        Response.Headers.CacheControl = "no-store";
        return Ok(new { headerButton = settings.HeaderButton && allowed });
    }

    private static PluginConfiguration Settings => ForeseerrPlugin.Instance?.Configuration ?? new PluginConfiguration();
}
