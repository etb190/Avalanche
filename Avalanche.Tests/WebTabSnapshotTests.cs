using System;
using System.Collections.Generic;
using Avalanche.Controls;
using Avalanche.Services;
using Xunit;

namespace Avalanche.Tests;

public sealed class WebTabSnapshotTests
{
    private const string TinyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public void ProcessCapture_ValidPng_GeneratesScaledImageAndJpegDataUri()
    {
        byte[] pngBytes = Convert.FromBase64String(TinyPng);
        var (image, dataUri) = WebThumbCodec.ProcessCapture(pngBytes);

        Assert.NotNull(image);
        Assert.True(image!.IsFrozen);
        Assert.NotNull(dataUri);
        Assert.StartsWith("data:image/jpeg;base64,", dataUri);
    }

    [Fact]
    public void ProcessCapture_NullOrEmpty_ReturnsNull()
    {
        var (img1, uri1) = WebThumbCodec.ProcessCapture(null);
        Assert.Null(img1);
        Assert.Null(uri1);

        var (img2, uri2) = WebThumbCodec.ProcessCapture([]);
        Assert.Null(img2);
        Assert.Null(uri2);
    }

    [Fact]
    public void Decode_ValidJpegDataUri_ReturnsFrozenBitmap()
    {
        byte[] pngBytes = Convert.FromBase64String(TinyPng);
        var (_, dataUri) = WebThumbCodec.ProcessCapture(pngBytes);
        Assert.NotNull(dataUri);

        var decoded = WebThumbCodec.Decode(dataUri);
        Assert.NotNull(decoded);
        Assert.True(decoded!.IsFrozen);
    }

    [Fact]
    public void Decode_InvalidOrMalformed_ReturnsNull()
    {
        Assert.Null(WebThumbCodec.Decode(null));
        Assert.Null(WebThumbCodec.Decode(""));
        Assert.Null(WebThumbCodec.Decode("not-a-data-uri"));
        Assert.Null(WebThumbCodec.Decode("data:text/plain;base64,SGVsbG8="));
        Assert.Null(WebThumbCodec.Decode("data:image/jpeg;base64,!!!invalid-base64!!!"));
    }

    [Fact]
    public void WebTabSnapshotStore_Roundtrip_PreservesThumbAndFavicon()
    {
        byte[] pngBytes = Convert.FromBase64String(TinyPng);
        var (_, dataUri) = WebThumbCodec.ProcessCapture(pngBytes);

        List<WebTabSnapshot> tabs =
        [
            new WebTabSnapshot(
                url: "https://www.wikipedia.org/",
                title: "Wikipedia",
                host: "www.wikipedia.org",
                isActive: true,
                thumb: dataUri,
                favicon: "data:image/png;base64," + TinyPng
            ),
            new WebTabSnapshot(
                url: "https://en.wikipedia.org/wiki/Dabke",
                title: "Dabke - Wikipedia",
                host: "en.wikipedia.org",
                isActive: false,
                thumb: dataUri,
                favicon: null
            )
        ];

        string json = WebTabSnapshotStore.SerializeTabsSnapshot(tabs);
        var restored = WebTabSnapshotStore.ParseTabsSnapshot(json);

        Assert.Equal(2, restored.Count);

        Assert.Equal("https://www.wikipedia.org/", restored[0].Url);
        Assert.Equal("Wikipedia", restored[0].Title);
        Assert.Equal("www.wikipedia.org", restored[0].Host);
        Assert.True(restored[0].IsActive);
        Assert.Equal(dataUri, restored[0].Thumb);
        Assert.Equal("data:image/png;base64," + TinyPng, restored[0].Favicon);

        Assert.Equal("https://en.wikipedia.org/wiki/Dabke", restored[1].Url);
        Assert.Equal("Dabke - Wikipedia", restored[1].Title);
        Assert.False(restored[1].IsActive);
        Assert.Equal(dataUri, restored[1].Thumb);
        Assert.Null(restored[1].Favicon);
    }

    [Fact]
    public void WebTabSnapshotStore_LegacyJsonWithoutThumb_ParsesSuccessfully()
    {
        string legacyJson = """
        [
          {
            "Url": "https://notebook.google.com/",
            "Title": "Gemini Notebook",
            "Host": "notebook.google.com",
            "IsActive": false
          },
          {
            "Url": "https://www.wikipedia.org/",
            "Title": "Wikipedia",
            "Host": "www.wikipedia.org",
            "IsActive": true
          }
        ]
        """;

        var tabs = WebTabSnapshotStore.ParseTabsSnapshot(legacyJson);

        Assert.Equal(2, tabs.Count);
        Assert.Equal("Gemini Notebook", tabs[0].Title);
        Assert.Null(tabs[0].Thumb);
        Assert.Null(tabs[0].Favicon);

        Assert.Equal("Wikipedia", tabs[1].Title);
        Assert.True(tabs[1].IsActive);
        Assert.Null(tabs[1].Thumb);
        Assert.Null(tabs[1].Favicon);
    }

    [Fact]
    public void WebTabSnapshotStore_FiltersInvalidOrNonHttpUrls()
    {
        string mixedJson = """
        [
          {
            "Url": "javascript:alert(1)",
            "Title": "Bad",
            "Host": "",
            "IsActive": false
          },
          {
            "Url": "file:///C:/test.pdf",
            "Title": "Local file",
            "Host": "",
            "IsActive": false
          },
          {
            "Url": "",
            "Title": "Empty",
            "Host": "",
            "IsActive": false
          },
          {
            "Url": "https://valid.example.com/",
            "Title": "Valid",
            "Host": "valid.example.com",
            "IsActive": true
          }
        ]
        """;

        var tabs = WebTabSnapshotStore.ParseTabsSnapshot(mixedJson);

        var single = Assert.Single(tabs);
        Assert.Equal("https://valid.example.com/", single.Url);
        Assert.Equal("Valid", single.Title);
    }
}

