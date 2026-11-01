using MediaBrowser.Model.Plugins;

namespace Foreseerr.Jellyfin.Configuration;

/// <summary>Who may open Foreseerr through the plugin.</summary>
public enum AccessMode
{
    /// <summary>Every Jellyfin user; Foreseerr's own user settings still apply.</summary>
    Everyone,

    /// <summary>Only Jellyfin administrators, for example while Foreseerr is being set up.</summary>
    Administrators,
}

/// <summary>
/// The settings an administrator edits on the plugin page. Everything the
/// plugin manages itself (the sidecar's shared secret, Jellyfin's API key)
/// is not stored here.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Run the Foreseerr server. Off stops it without uninstalling the plugin.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Who can open Foreseerr from Jellyfin.</summary>
    public AccessMode Access { get; set; } = AccessMode.Everyone;

    /// <summary>Add a Foreseerr button to the Jellyfin Web header (needs File Transformation).</summary>
    public bool HeaderButton { get; set; } = true;

    /// <summary>
    /// The address browsers use for Jellyfin. Notification links are built from it.
    /// </summary>
    public string? PublicServerUrl { get; set; }

    /// <summary>Returns the URL without a trailing slash, or null when empty.</summary>
    /// <exception cref="ArgumentException">The value is not an http(s) URL.</exception>
    public static string? NormalizePublicServerUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException(
                "Public server URL must be an http or https address, for example https://jellyfin.example.com.",
                nameof(value));
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>Normalizes the values and rejects the ones the plugin cannot use.</summary>
    /// <exception cref="ArgumentException">A value is invalid.</exception>
    public void Validate()
    {
        if (!Enum.IsDefined(Access))
            throw new ArgumentException($"Unknown access mode {(int)Access}.", nameof(Access));
        PublicServerUrl = NormalizePublicServerUrl(PublicServerUrl);
    }

    /// <summary>What the Foreseerr process reads when it starts.</summary>
    internal SidecarSettings ToSidecarSettings() => new(Enabled, PublicServerUrl);
}

/// <summary>
/// The settings the running sidecar was started with. A change in one of
/// them needs a restart; the others apply to the next request.
/// </summary>
internal sealed record SidecarSettings(bool Enabled, string? PublicServerUrl);
