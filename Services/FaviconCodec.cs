using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Avalanche.Services;

/// <summary>
/// The favicon's decoder (v1.22.0). The browser's pages hand their own icon
/// over as a data URI - read from the page's own cache, carried through the
/// webview's message pipe - and this is where the bytes become a frozen
/// ImageSource. Nothing here touches the network or the disk: the URI either
/// decodes, in memory, or it does not and the globe keeps the seat.
/// </summary>
public static class FaviconCodec
{
    /// <summary>Anything larger than this is not an icon, it is a payload -
    /// the probe's own canvas cap is smaller, so this is only a second gate.</summary>
    private const int MaxDataUriLength = 131072;

    /// <summary>Decodes a data:image/...;base64 URI into a frozen bitmap, or
    /// returns null for anything that is not honestly a small image.</summary>
    public static BitmapImage? Decode(string? dataUri)
    {
        try
        {
            if (string.IsNullOrEmpty(dataUri) || dataUri.Length > MaxDataUriLength) return null;
            if (!dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
            int comma = dataUri.IndexOf(',');
            if (comma < 11) return null;   // inside the fixed "data:image/" prefix there is no comma
            string meta = dataUri.Substring(11, comma - 11);   // between the prefix and the comma
            if (!meta.EndsWith("base64", StringComparison.OrdinalIgnoreCase)) return null;
            byte[] bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
            if (bytes.Length == 0) return null;
            BitmapImage bmp = new();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;   // an icon that will not read is not worth a crash
        }
    }
}
