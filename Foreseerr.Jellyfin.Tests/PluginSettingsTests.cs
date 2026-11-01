using Foreseerr.Jellyfin.Configuration;
using Foreseerr.Jellyfin.Web;
using Xunit;

namespace Foreseerr.Jellyfin.Tests;

public class PluginSettingsTests
{
    [Fact]
    public void DefaultsRunForeseerrForEveryoneWithTheHeaderButton()
    {
        var settings = new PluginConfiguration();
        Assert.True(settings.Enabled);
        Assert.Equal(AccessMode.Everyone, settings.Access);
        Assert.True(settings.HeaderButton);
        Assert.Null(settings.PublicServerUrl);
    }

    [Fact]
    public void ValidateNormalizesThePublicUrl()
    {
        var settings = new PluginConfiguration { PublicServerUrl = " https://host/jellyfin/ " };
        settings.Validate();
        Assert.Equal("https://host/jellyfin", settings.PublicServerUrl);
    }

    [Fact]
    public void ValidateRejectsAnUnknownAccessMode()
    {
        var settings = new PluginConfiguration { Access = (AccessMode)7 };
        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void OnlySettingsForeseerrReadsRestartIt()
    {
        var before = new PluginConfiguration { PublicServerUrl = "https://host" }.ToSidecarSettings();
        // Access and the header button apply to the next request.
        Assert.Equal(before, new PluginConfiguration
        {
            PublicServerUrl = "https://host",
            Access = AccessMode.Administrators,
            HeaderButton = false,
        }.ToSidecarSettings());
        Assert.NotEqual(before, new PluginConfiguration { PublicServerUrl = "https://other" }.ToSidecarSettings());
        Assert.NotEqual(before, new PluginConfiguration { PublicServerUrl = "https://host", Enabled = false }.ToSidecarSettings());
    }

    [Theory]
    [InlineData(true, AccessMode.Everyone, false, null)]
    [InlineData(true, AccessMode.Everyone, true, null)]
    [InlineData(true, AccessMode.Administrators, true, null)]
    [InlineData(true, AccessMode.Administrators, false, 403)]
    [InlineData(false, AccessMode.Everyone, true, 503)]
    [InlineData(false, AccessMode.Administrators, false, 503)]
    public void AccessFollowsTheSwitchAndTheAccessMode(bool enabled, AccessMode access, bool isAdministrator, int? refused)
    {
        var settings = new PluginConfiguration { Enabled = enabled, Access = access };
        Assert.Equal(refused, PluginAccess.Check(settings, isAdministrator)?.StatusCode);
    }
}
