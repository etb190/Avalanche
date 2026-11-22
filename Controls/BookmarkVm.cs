using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Avalanche.Services;

namespace Avalanche.Controls;

/// <summary>
/// The bookmarks.json shape (v1.19.28): the folders' names and
/// the bookmarks together. An older file is a bare list of bookmarks;
/// the loader reads both.
/// </summary>
public sealed class BookmarkStore
{
    public List<string>? Folders { get; set; }
    public List<BookmarkVm>? Bookmarks { get; set; }
}

/// <summary>
/// Serialization helper for bookmarks storage (v1.36.1): handles JSON roundtripping,
/// legacy format fallback, case-insensitivity, and ensures transient WPF image handles
/// are never serialized.
/// </summary>
public static class BookmarkStoreSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(List<string> folders, List<BookmarkVm> bookmarks)
    {
        var store = new BookmarkStore
        {
            Folders = folders,
            Bookmarks = bookmarks
        };
        return JsonSerializer.Serialize(store, JsonOptions);
    }

    public static (List<string> Folders, List<BookmarkVm> Bookmarks)? Deserialize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        List<BookmarkVm>? saved = null;
        List<string> folders = [];
        try
        {
            BookmarkStore? store = JsonSerializer.Deserialize<BookmarkStore>(text, JsonOptions);
            if (store is not null)
            {
                foreach (string f in store.Folders ?? [])
                    if (f.Length > 0) folders.Add(f);
                saved = store.Bookmarks ?? [];
            }
        }
        catch (JsonException) { /* a bare list, then */ }

        if (saved is null)
        {
            try { saved = JsonSerializer.Deserialize<List<BookmarkVm>>(text, JsonOptions); }
            catch (JsonException) { /* not a bookmarks file at all */ }
        }

        if (saved is null) return null;
        return (folders, new List<BookmarkVm>(saved.Where(b => !string.IsNullOrEmpty(b.Url))));
    }
}

/// <summary>One saved page in the browser's bookmarks (v1.19.11): the name the
/// reader gave it, the address it opens, and the favicon slot the globe
/// stands in for. Rows live newest first and persist to bookmarks.json in
/// the app's data root.</summary>
public sealed class BookmarkVm : INotifyPropertyChanged
{
    private string _name = "";
    /// <summary>The chip's label - the page's own title until the reader renames it.</summary>
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }

    /// <summary>The address the chip opens - the bookmark's identity.</summary>
    public string Url { get; set; } = "";

    private string _favicon = "";
    /// <summary>The site's icon as a data URI; empty keeps the globe glyph.
    /// v1.22.0: the page itself hands the bytes over, and the setter decodes
    /// them once so every chip of the site paints without rework.</summary>
    public string Favicon
    {
        get => _favicon;
        set
        {
            _favicon = value ?? "";
            _faviconImage = FaviconCodec.Decode(_favicon);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FaviconImage));
        }
    }

    private ImageSource? _faviconImage;
    /// <summary>The decoded icon; null while Favicon is empty (the globe's turn).
    /// Ignored during JSON serialization so WPF image handles never crash the persistence layer.</summary>
    [JsonIgnore]
    public ImageSource? FaviconImage => _faviconImage;

    /// <summary>The folder this bookmark lives in (v1.19.28); empty means
    /// the loose strip. Folders are names, persisted beside the bookmarks.</summary>
    public string Folder { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}

