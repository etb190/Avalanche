using System.Text.Json;

namespace Avalanche.Services;

/// <summary>
/// Where an extension's options page lives, read the way Chrome reads
/// it: MV3 spells it options_ui.page - the key the toolbar's reader
/// once missed, leaving every standard options declaration with no
/// Options row at all - the MV2 years answered to a root-level
/// options_page, and options_ui.options_page is tolerated behind them
/// though no manifest ever wore it.
/// </summary>
public static class ExtensionOptionsPath
{
    public static string FromJson(string manifestJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(manifestJson);
            return Resolve(doc.RootElement);
        }
        catch { return ""; }
    }

    public static string Resolve(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "";
        if (root.TryGetProperty("options_ui", out JsonElement oui)
            && oui.ValueKind == JsonValueKind.Object)
        {
            string path = Str(oui, "page");
            if (path.Length == 0) path = Str(oui, "options_page");
            if (path.Length > 0) return path;
        }
        return Str(root, "options_page");
    }

    private static string Str(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out JsonElement v)) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}
