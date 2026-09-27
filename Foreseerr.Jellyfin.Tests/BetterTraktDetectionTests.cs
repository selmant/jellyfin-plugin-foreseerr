using Foreseerr.Jellyfin;
using Xunit;

namespace Foreseerr.Jellyfin.Tests;

public class BetterTraktDetectionTests
{
    [Theory]
    // Drop-in install: official Trakt assembly and GUID, Better Trakt name.
    [InlineData(new[] { "Trakt", "Better Trakt" }, true)]
    [InlineData(new[] { "Jellyfin.Plugin.BetterTrakt" }, true)]
    [InlineData(new[] { "Trakt" }, false)]
    [InlineData(new[] { "TMDb", "OMDb" }, false)]
    public void DetectsBetterTraktByPluginOrAssemblyName(string[] names, bool expected)
    {
        Assert.Equal(expected, JellyfinHostBootstrap.IsBetterTrakt(names));
    }
}
