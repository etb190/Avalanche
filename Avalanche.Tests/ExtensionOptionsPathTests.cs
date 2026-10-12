using System.Text.Json;
using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests;

/// <summary>Where an extension's options page lives, read the way
/// Chrome reads it: MV3 spells it options_ui.page - the key the reader
/// once missed, leaving every standard options declaration with no
/// Options row at all - and the MV2 years answered to a root
/// options_page.</summary>
public sealed class ExtensionOptionsPathTests
{
    private static string Resolve(string json)
        => ExtensionOptionsPath.Resolve(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Mv3_OptionsUi_Page_Is_Read()
        => Assert.Equal("opts.html", Resolve(
            "{\"options_ui\":{\"page\":\"opts.html\",\"open_in_tab\":true}}"));

    [Fact]
    public void Mv2_Root_OptionsPage_Is_Read()
        => Assert.Equal("opts.html", Resolve("{\"options_page\":\"opts.html\"}"));

    [Fact]
    public void Nonstandard_OptionsUi_OptionsPage_Is_Tolerated()
        => Assert.Equal("opts.html", Resolve(
            "{\"options_ui\":{\"options_page\":\"opts.html\"}}"));

    [Fact]
    public void OptionsUi_Page_Wins_Over_The_Root_Spelling()
        => Assert.Equal("ui.html", Resolve(
            "{\"options_ui\":{\"page\":\"ui.html\"},\"options_page\":\"root.html\"}"));

    [Fact]
    public void No_Options_At_All_Is_An_Empty_Word()
        => Assert.Equal("", Resolve("{\"action\":{}}"));
}
