using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Avalanche.Controls;

/// <summary>One line of the browser's saved tab session (v1.19.24): the
/// address to revive, the title the page wore, its host, whether this
/// was the tab on screen, and its cached preview thumbnail and favicon (v1.36.1). Order in the file is order in the strip.</summary>
public sealed class WebTabSnapshot
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Host { get; set; } = "";
    public bool IsActive { get; set; }
    public string? Thumb { get; set; }
    public string? Favicon { get; set; }

    public WebTabSnapshot() { }

    public WebTabSnapshot(string url, string title, string host, bool isActive, string? thumb = null, string? favicon = null)
    {
        Url = url;
        Title = title;
        Host = host;
        IsActive = isActive;
        Thumb = thumb;
        Favicon = favicon;
    }
}

/// <summary>The tab session's pure core (v1.19.24): what is written and
/// what survives a read. Junk rows - empty, unparseable, non-web - are
/// dropped on the way in, so a corrupted or hand-edited file can never
/// resurrect a tab that makes no sense.</summary>
public static class WebTabSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string SerializeTabsSnapshot(List<WebTabSnapshot> tabs)
        => JsonSerializer.Serialize(tabs, JsonOptions);

    public static List<WebTabSnapshot> ParseTabsSnapshot(string? json)
    {
        List<WebTabSnapshot> parsed = new();
        if (string.IsNullOrWhiteSpace(json)) return parsed;
        try
        {
            List<WebTabSnapshot>? saved = JsonSerializer.Deserialize<List<WebTabSnapshot>>(json, JsonOptions);
            if (saved is null) return parsed;
            foreach (WebTabSnapshot s in saved)
            {
                if (string.IsNullOrWhiteSpace(s.Url)) continue;
                if (!Uri.TryCreate(s.Url, UriKind.Absolute, out Uri? u)) continue;
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) continue;
                parsed.Add(s);
            }
        }
        catch { /* a session file that will not read is not worth a crash */ }
        return parsed;
    }
}

