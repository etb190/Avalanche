using System;
using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests;

// ============================================================
// v1.22.0 - the favicon decoder: the page's own icon arrives
// as a data URI and either becomes a frozen bitmap in memory
// or nothing at all. No network, no disk - and no crash.
// ============================================================

public sealed class FaviconCodecTests
{
    // A real 1x1 PNG, base64 - the smallest honest icon there is.
    private const string TinyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public void Decode_Accepts_A_Valid_Png_Data_Uri()
    {
        var bmp = FaviconCodec.Decode("data:image/png;base64," + TinyPng);
        Assert.NotNull(bmp);
        Assert.True(bmp!.IsFrozen);
        Assert.Equal(1, bmp.PixelWidth);
        Assert.Equal(1, bmp.PixelHeight);
    }

    [Fact]
    public void Decode_Tolerates_Any_Mime_Casing()
    {
        Assert.NotNull(FaviconCodec.Decode("DATA:IMAGE/PNG;BASE64," + TinyPng));
        Assert.NotNull(FaviconCodec.Decode("data:image/png;Base64," + TinyPng));
    }

    [Fact]
    public void Decode_Rejects_Null_Empty_And_Non_Images()
    {
        Assert.Null(FaviconCodec.Decode(null));
        Assert.Null(FaviconCodec.Decode(""));
        Assert.Null(FaviconCodec.Decode("data:text/html;base64,PGh0bWw+"));
        Assert.Null(FaviconCodec.Decode("data:image/svg+xml;utf8,<svg/>"));
    }

    [Fact]
    public void Decode_Rejects_Broken_Base64_And_Empty_Payloads()
    {
        Assert.Null(FaviconCodec.Decode("data:image/png;base64,!!!not-base64!!!"));
        Assert.Null(FaviconCodec.Decode("data:image/png;base64,"));
    }

    [Fact]
    public void Decode_Rejects_Oversize_Payloads()
    {
        Assert.Null(FaviconCodec.Decode("data:image/png;base64," + TinyPng + new string('A', 131072)));
    }
}
