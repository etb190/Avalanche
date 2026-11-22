using System;
using System.Collections.Generic;
using Avalanche.Controls;
using Xunit;

namespace Avalanche.Tests;

public sealed class BookmarkStoreTests
{
    private const string TinyPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public void Serialize_WithFavicon_DoesNotThrow_AndIgnoresFaviconImage()
    {
        var vm = new BookmarkVm
        {
            Name = "Example",
            Url = "https://example.com",
            Favicon = "data:image/png;base64," + TinyPng,
            Folder = "Work"
        };

        Assert.NotNull(vm.FaviconImage);

        string json = BookmarkStoreSerializer.Serialize(["Work"], [vm]);

        Assert.Contains("https://example.com", json);
        Assert.Contains("Work", json);
        Assert.Contains("Favicon", json);
        Assert.DoesNotContain("FaviconImage", json);
    }

    [Fact]
    public void Deserialize_WithFavicon_DecodesFaviconImage()
    {
        string json = "{\n  \"Folders\": [\"Reading\"],\n  \"Bookmarks\": [\n    {\n      \"Name\": \"Example\",\n      \"Url\": \"https://example.com\",\n      \"Favicon\": \"data:image/png;base64," + TinyPng + "\",\n      \"Folder\": \"Reading\"\n    }\n  ]\n}";

        var result = BookmarkStoreSerializer.Deserialize(json);

        Assert.NotNull(result);
        var (folders, bookmarks) = result.Value;
        Assert.Single(folders);
        Assert.Equal("Reading", folders[0]);

        var bm = Assert.Single(bookmarks);
        Assert.Equal("Example", bm.Name);
        Assert.Equal("https://example.com", bm.Url);
        Assert.Equal("Reading", bm.Folder);
        Assert.NotNull(bm.FaviconImage);
        Assert.True(bm.FaviconImage.IsFrozen);
    }

    [Fact]
    public void Deserialize_LegacyBareList_ReturnsBookmarks()
    {
        string legacyJson = "[\n  {\n    \"Name\": \"Legacy Page\",\n    \"Url\": \"https://legacy.example.org\",\n    \"Favicon\": \"\",\n    \"Folder\": \"\"\n  }\n]";

        var result = BookmarkStoreSerializer.Deserialize(legacyJson);

        Assert.NotNull(result);
        var (folders, bookmarks) = result.Value;
        Assert.Empty(folders);

        var bm = Assert.Single(bookmarks);
        Assert.Equal("Legacy Page", bm.Name);
        Assert.Equal("https://legacy.example.org", bm.Url);
    }

    [Fact]
    public void Deserialize_CaseInsensitive_ParsesCorrectly()
    {
        string lowerJson = "{\n  \"folders\": [\"research\"],\n  \"bookmarks\": [\n    {\n      \"name\": \"arXiv\",\n      \"url\": \"https://arxiv.org\",\n      \"folder\": \"research\"\n    }\n  ]\n}";

        var result = BookmarkStoreSerializer.Deserialize(lowerJson);

        Assert.NotNull(result);
        var (folders, bookmarks) = result.Value;
        Assert.Equal(["research"], folders);

        var bm = Assert.Single(bookmarks);
        Assert.Equal("arXiv", bm.Name);
        Assert.Equal("https://arxiv.org", bm.Url);
        Assert.Equal("research", bm.Folder);
    }

    [Fact]
    public void Deserialize_StoreWithOnlyFolders_PreservesFolders()
    {
        string json = "{\n  \"Folders\": [\"Archive\", \"Projects\"]\n}";

        var result = BookmarkStoreSerializer.Deserialize(json);

        Assert.NotNull(result);
        var (folders, bookmarks) = result.Value;
        Assert.Equal(2, folders.Count);
        Assert.Contains("Archive", folders);
        Assert.Contains("Projects", folders);
        Assert.Empty(bookmarks);
    }

    [Fact]
    public void IconOnlyBookmark_EmptyName_PreservesAndSerializes()
    {
        var vm = new BookmarkVm
        {
            Name = "",
            Url = "https://pmc.ncbi.nlm.nih.gov/articles/PMC5544389/",
            Favicon = "data:image/png;base64," + TinyPng,
            Folder = ""
        };

        string json = BookmarkStoreSerializer.Serialize([], [vm]);
        var result = BookmarkStoreSerializer.Deserialize(json);

        Assert.NotNull(result);
        var (folders, bookmarks) = result.Value;
        var bm = Assert.Single(bookmarks);
        Assert.Equal("", bm.Name);
        Assert.Equal("https://pmc.ncbi.nlm.nih.gov/articles/PMC5544389/", bm.Url);
        Assert.NotNull(bm.FaviconImage);
    }

    [Fact]
    public void Roundtrip_MaintainsAllData()
    {
        List<string> originalFolders = ["Docs", "Tools"];
        List<BookmarkVm> originalBookmarks =
        [
            new BookmarkVm
            {
                Name = "Doc 1",
                Url = "https://docs.microsoft.com",
                Folder = "Docs",
                Favicon = "data:image/png;base64," + TinyPng
            },
            new BookmarkVm
            {
                Name = "Tool 1",
                Url = "https://github.com",
                Folder = "Tools"
            }
        ];

        string json = BookmarkStoreSerializer.Serialize(originalFolders, originalBookmarks);
        var result = BookmarkStoreSerializer.Deserialize(json);

        Assert.NotNull(result);
        var (folders, bookmarks) = result.Value;

        Assert.Equal(originalFolders, folders);
        Assert.Equal(2, bookmarks.Count);

        Assert.Equal("Doc 1", bookmarks[0].Name);
        Assert.Equal("https://docs.microsoft.com", bookmarks[0].Url);
        Assert.Equal("Docs", bookmarks[0].Folder);
        Assert.NotNull(bookmarks[0].FaviconImage);

        Assert.Equal("Tool 1", bookmarks[1].Name);
        Assert.Equal("https://github.com", bookmarks[1].Url);
        Assert.Equal("Tools", bookmarks[1].Folder);
        Assert.Null(bookmarks[1].FaviconImage);
    }
}
