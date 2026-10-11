using System;
using System.Text.RegularExpressions;

namespace Avalanche.Services;

/// <summary>
/// The store's front door (v1.23.0). A pasted Chrome Web Store link - any
/// shape of it - or a bare extension id becomes the one 32-letter id the
/// crate endpoint understands. Anything else is honestly refused: the
/// downloader only ever knocks on the store's own door with a real id.
/// </summary>
public static class StoreLink
{
    /// <summary>Extension ids live on a 16-letter alphabet a..p, 32 long.
    /// The lookarounds refuse a window carved out of a longer run.</summary>
    private static readonly Regex IdPattern =
        new("(?<![a-p])[a-p]{32}(?![a-p])", RegexOptions.Compiled);

    /// <summary>Accepts a chromewebstore.google.com detail link (current or
    /// legacy chrome.google.com/webstore shape), the crate endpoint's own
    /// query form, or a bare id; yields the id or nothing.</summary>
    public static bool TryParseExtId(string? input, out string extId)
    {
        extId = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim().ToLowerInvariant();
        if (s.Length == 32 && IsIdShaped(s)) { extId = s; return true; }
        Match m = IdPattern.Match(s);
        if (!m.Success) return false;
        extId = m.Value;
        return true;
    }

    private static bool IsIdShaped(string s)
    {
        foreach (char c in s)
            if (c < 'a' || c > 'p') return false;
        return true;
    }
}
