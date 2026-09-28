using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Avalanche.Features.AI;

namespace Avalanche
{
    /// <summary>
    /// Converts ChatMessage.Role to a brush for the avatar background.
    /// Colors are derived from the live theme so avatars stay visible in
    /// light, dark and specialty themes (hardcoded colors vanished on some).
    /// </summary>
    internal sealed class AiRoleToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush UserFallback = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        private static readonly SolidColorBrush AssistantFallback = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        private static readonly SolidColorBrush SystemFallback = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                ChatMessage.Role.User => AiBrushHelpers.ThemedTint("TextBrush", 0.12, UserFallback),
                ChatMessage.Role.Assistant => AiBrushHelpers.ThemedTint("PrimaryBrush", 0.25, AssistantFallback),
                ChatMessage.Role.System => AiBrushHelpers.ThemedTint("MutedTextBrush", 0.20, SystemFallback),
                _ => AiBrushHelpers.ThemedTint("TextBrush", 0.12, UserFallback)
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Builds a translucent tint of a live themed brush so bubbles pick up the
    /// current theme while the themed text color stays readable on top of them.
    /// </summary>
    internal static class AiBrushHelpers
    {
        internal static SolidColorBrush ThemedTint(string resourceKey, double alpha, SolidColorBrush fallback)
        {
            var source = Application.Current?.TryFindResource(resourceKey) as SolidColorBrush;
            var c = source?.Color ?? fallback.Color;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// Converts ChatMessage.Role to a glyph for the avatar.
    /// </summary>
    internal sealed class AiRoleToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // NOTE: XML character entities (&#xE77B; style) are only decoded by the
            // XAML parser, not at runtime - returning them from a converter rendered
            // the literal text "&#xE77B;" instead of the glyph. Use real characters.
            return value switch
            {
                ChatMessage.Role.User => "\uE77B",      // Person
                ChatMessage.Role.Assistant => "\uE8A5", // Bot/AI
                ChatMessage.Role.System => "\uE713",    // Info
                _ => "\uE77B"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts ChatMessage.Role to a background brush for the message bubble.
    /// Backgrounds are translucent tints of the live theme brushes so the
    /// themed text color stays readable in light and dark themes (the previous
    /// hardcoded dark bubbles made near-black light-theme text invisible).
    /// </summary>
    internal sealed class AiRoleToBgConverter : IValueConverter
    {
        private static readonly SolidColorBrush UserFallback = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
        private static readonly SolidColorBrush AssistantFallback = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x5C));
        private static readonly SolidColorBrush SystemFallback = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                ChatMessage.Role.User => AiBrushHelpers.ThemedTint("PrimaryBrush", 0.14, UserFallback),
                ChatMessage.Role.Assistant => AiBrushHelpers.ThemedTint("TextBrush", 0.07, AssistantFallback),
                ChatMessage.Role.System => AiBrushHelpers.ThemedTint("TextBrush", 0.07, SystemFallback),
                _ => AiBrushHelpers.ThemedTint("PrimaryBrush", 0.14, UserFallback)
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts an integer count to Visibility (Visible if > 0, Collapsed otherwise).
    /// </summary>
    internal sealed class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
                return count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (value is long lcount)
                return lcount > 0 ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts a boolean to Visibility.
    /// </summary>
    internal sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts a non-null/non-empty string to Visibility.
    /// </summary>
    internal sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}