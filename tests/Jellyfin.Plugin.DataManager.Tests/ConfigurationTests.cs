// SPDX-License-Identifier: GPL-3.0-only
using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.DataManager.Configuration;
using Xunit;

namespace Jellyfin.Plugin.DataManager.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void A_settings_file_from_before_the_panel_starts_sections_on_last_choice()
    {
        const string Old = "<?xml version=\"1.0\"?><PluginConfiguration><KeepOriginalWhenSpaceAllows>false</KeepOriginalWhenSpaceAllows></PluginConfiguration>";
        using var reader = new StringReader(Old);

        var config = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;

        Assert.False(config.KeepOriginalWhenSpaceAllows);
        Assert.Equal(SectionStart.Remember, config.EncodingSectionStart);
        Assert.Equal(SectionStart.Remember, config.CleanUpSectionStart);
    }
}
