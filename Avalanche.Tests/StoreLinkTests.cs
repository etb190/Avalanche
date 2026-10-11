using System;
using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests;

// ============================================================
// v1.23.0 - the store's front door: a pasted Chrome Web Store
// link (or a bare id) must yield the one 32-letter id the
// crate endpoint understands - or honestly refuse.
// ============================================================

public sealed class StoreLinkTests
{
    private const string LazyScholarId = "fpbdcofpbclblalghaepibbagkkgpkak";

    [Fact]
    public void Store_Detail_Url_Yields_The_Id()
    {
        Assert.True(StoreLink.TryParseExtId(
            "https://chromewebstore.google.com/detail/lazy-scholar/" + LazyScholarId,
            out string id));
        Assert.Equal(LazyScholarId, id);
    }

    [Fact]
    public void Legacy_Webstore_Url_Yields_The_Id()
    {
        Assert.True(StoreLink.TryParseExtId(
            "https://chrome.google.com/webstore/detail/lazy-scholar/" + LazyScholarId,
            out string id));
        Assert.Equal(LazyScholarId, id);
    }

    [Fact]
    public void Url_With_Query_Yields_The_Id()
    {
        Assert.True(StoreLink.TryParseExtId(
            "chromewebstore.google.com/detail/lazy-scholar/" + LazyScholarId + "?hl=en",
            out string id));
        Assert.Equal(LazyScholarId, id);
    }

    [Fact]
    public void Crate_Endpoint_Url_Yields_The_Id()
    {
        Assert.True(StoreLink.TryParseExtId(
            "https://update.googleapis.com/service/update2/crx?response=redirect&x=id%3D"
            + LazyScholarId + "%26uc", out string id));
        Assert.Equal(LazyScholarId, id);
    }

    [Fact]
    public void Bare_Id_Is_Accepted_In_Any_Casing()
    {
        Assert.True(StoreLink.TryParseExtId(LazyScholarId, out string lower));
        Assert.Equal(LazyScholarId, lower);
        Assert.True(StoreLink.TryParseExtId(LazyScholarId.ToUpperInvariant(), out string upper));
        Assert.Equal(LazyScholarId, upper);
    }

    [Fact]
    public void Surrounded_Id_Is_Found()
    {
        Assert.True(StoreLink.TryParseExtId("  " + LazyScholarId + "\n", out string id));
        Assert.Equal(LazyScholarId, id);
    }

    [Fact]
    public void Garbage_Is_Refused()
    {
        Assert.False(StoreLink.TryParseExtId(null, out _));
        Assert.False(StoreLink.TryParseExtId("", out _));
        Assert.False(StoreLink.TryParseExtId("   ", out _));
        Assert.False(StoreLink.TryParseExtId("https://example.com", out _));
        Assert.False(StoreLink.TryParseExtId(
            "https://chromewebstore.google.com/category/extensions", out _));
    }

    [Fact]
    public void A_Longer_Letter_Run_Is_Never_Cut()
    {
        Assert.False(StoreLink.TryParseExtId(new string('a', 33), out _));
        Assert.False(StoreLink.TryParseExtId(new string('b', 64), out _));
        Assert.True(StoreLink.TryParseExtId(new string('c', 32), out string exact));
        Assert.Equal(new string('c', 32), exact);
    }
}
