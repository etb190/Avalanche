using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Avalanche.Services;

/// <summary>
/// Encoder and decoder for browser tab preview thumbnails (v1.36.1).
/// Scales captured page previews to crisp sidebar thumbnail size (150px height),
/// encodes them into compact JPEG data URIs for persistence across app restarts,
/// and decodes them into frozen BitmapImages.
/// </summary>
public static class WebThumbCodec
{
    private const int TargetThumbHeight = 150;
    private const int JpegQuality = 75;
    private const int MaxDataUriLength = 1048576; // 1 MB cap

    /// <summary>
    /// Processes a raw capture (PNG or JPEG bytes from CoreWebView2.CapturePreviewAsync),
    /// producing a downscaled frozen BitmapImage for UI display and a compact JPEG data URI
    /// for persistence.
    /// </summary>
    public static (BitmapImage? Image, string? DataUri) ProcessCapture(byte[]? captureBytes)
    {
        if (captureBytes is null || captureBytes.Length == 0) return (null, null);
        try
        {
            BitmapImage scaled = new();
            scaled.BeginInit();
            scaled.CacheOption = BitmapCacheOption.OnLoad;
            scaled.DecodePixelHeight = TargetThumbHeight;
            scaled.StreamSource = new MemoryStream(captureBytes);
            scaled.EndInit();
            scaled.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(scaled));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            byte[] jpgBytes = ms.ToArray();
            string dataUri = "data:image/jpeg;base64," + Convert.ToBase64String(jpgBytes);

            return (scaled, dataUri);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Decodes a persisted data URI into a frozen BitmapImage.
    /// Accepts data:image/jpeg;base64,... and data:image/png;base64,...
    /// </summary>
    public static BitmapImage? Decode(string? dataUri)
    {
        try
        {
            if (string.IsNullOrEmpty(dataUri) || dataUri.Length > MaxDataUriLength) return null;
            if (!dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
            int comma = dataUri.IndexOf(',');
            if (comma < 11) return null;
            string meta = dataUri.Substring(11, comma - 11);
            if (!meta.EndsWith("base64", StringComparison.OrdinalIgnoreCase)) return null;
            byte[] bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
            if (bytes.Length == 0) return null;
            BitmapImage bmp = new();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = TargetThumbHeight;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

