using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// Assigns footnote display numbers in order of first appearance while an
/// answer's document is built. The model's SOURCE_n ids index the reply's
/// evidence list, so citing [SOURCE_2] before [SOURCE_1] made the circles
/// read "2" above "1"; readers expect footnotes to count upward through the
/// text instead. The circle's face shows the display number, its Tag keeps
/// the original SOURCE_n id, and clicks/tooltips resolve through the id -
/// so renumbering changes nothing about where a citation jumps.
/// Numbers are handed out exactly when a circle is created, so markers in
/// contexts that never render citations (code fences, quotes, headings)
/// cannot consume a number.
/// </summary>
internal sealed class AiCitationNumberer
{
    private readonly Dictionary<int, int> _displayBySource = new();
    private int _next = 1;

    /// <summary>Returns the display number for this source id, assigning the next free number on first sight.</summary>
    internal int Register(int sourceNumber)
    {
        if (_displayBySource.TryGetValue(sourceNumber, out int display))
            return display;
        display = _next++;
        _displayBySource[sourceNumber] = display;
        return display;
    }
}

/// <summary>
/// Parsing helpers for inline SOURCE_n citation markers. The model is told to
/// emit "[SOURCE_n]", but models drift: these patterns also accept full-width
/// variants (【SOURCE_3】, ［3］) and bare SOURCE_3 tokens so the
/// footnote circles still render instead of leaking raw markers into the chat.
/// </summary>
internal static class AiCitations
{
    private const string Open = @"[\[\uFF3B\u3010]";
    private const string Close = @"[\]\uFF3D\u3011]";
    private const string Digits = @"[\d\uFF10-\uFF19]{1,3}";

    // [SOURCE_3] / 【SOURCE_3】 / [3] / 【3】 (the bare-number form must
    // not swallow markdown links like [3](https://...) hence the lookahead,
    // and not match "array[1]" hence the non-word left boundary) /
    // bare SOURCE_3 tokens inside prose.
    internal static readonly Regex InlineRx = new(
        Open + @"\s*SOURCE[\s_\-]*(?<s>" + Digits + @")\s*" + Close +
        @"|" + @"(?<!\w)" + Open + @"\s*(?<p>" + Digits + @")\s*" + Close + @"(?!\s*\()" +
        @"|\bSOURCE[\s_\-]*(?<b>" + Digits + @")(?![\w\uFF10-\uFF19\-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Same vocabulary for the ids inside 'sources' ("SOURCE_3", "source 3",
    // "[3]", "【SOURCE_3】", "3") - the bracketed bare-digit form is accepted
    // here even though InlineRx needs a word boundary: inside 'sources' the
    // id is the whole field, not prose.
    private static readonly Regex IdRx = new(
        @"^" + Open + @"?\s*SOURCE[\s_\-]*(?<s>" + Digits + @")\s*" + Close + @"?$" +
        @"|^" + Open + @"?\s*(?<p>" + Digits + @")\s*" + Close + @"?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Extracts the citation number from a matched marker, or -1.
    /// Explicit SOURCE_n markers (bracketed or bare) accept n = 0: some models
    /// number their evidence 0-based even though the prompt asks for 1-based,
    /// and rejecting 0 made every one of their citations unusable. The bare
    /// bracketed-number form still requires 1 - "[0]" is too easily literal
    /// text (array indexes) to promote into a citation.</summary>
    internal static int MatchToNumber(Match m)
    {
        foreach (var name in new[] { "s", "p", "b" })
        {
            var g = m.Groups[name];
            if (g.Success)
            {
                int v = ParseDigits(g.Value);
                if (v >= 1)
                    return v;
                if (v == 0 && name != "p")
                    return 0;
            }
        }
        return -1;
    }

    /// <summary>Parses any SOURCE_n id spelling to its 1-based number, or -1 when unknown.</summary>
    internal static int ParseSourceId(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return -1;
        var m = IdRx.Match(sourceId.Trim());
        return m.Success ? MatchToNumber(m) : -1;
    }

    internal static string FormatId(int number) => "SOURCE_" + number.ToString(CultureInfo.InvariantCulture);

    private static int ParseDigits(string text)
    {
        if (string.IsNullOrEmpty(text))
            return -1;
        int value = 0;
        foreach (var ch in text.Trim())
        {
            int d = ch >= '\uFF10' && ch <= '\uFF19' ? ch - '\uFF10'
                  : ch >= '0' && ch <= '9' ? ch - '0'
                  : -1;
            if (d < 0)
                return -1;
            value = value * 10 + d;
        }
        return value > 999 ? -1 : value;
    }
}
