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
    /// </summary>
    internal sealed class AiRoleToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush UserBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        private static readonly SolidColorBrush AssistantBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
        private static readonly SolidColorBrush SystemBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                ChatMessage.Role.User => UserBrush,
                ChatMessage.Role.Assistant => AssistantBrush,
                ChatMessage.Role.System => SystemBrush,
                _ => UserBrush
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts ChatMessage.Role to a glyph for the avatar.
    /// </summary>
    internal sealed class AiRoleToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                ChatMessage.Role.User => "&#xE77B;",      // Person
                ChatMessage.Role.Assistant => "&#xE8A5;", // Bot/AI
                ChatMessage.Role.System => "&#xE713;",    // Info
                _ => "&#xE77B;"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts ChatMessage.Role to a background brush for the message bubble.
    /// </summary>
    internal sealed class AiRoleToBgConverter : IValueConverter
    {
        private static readonly SolidColorBrush UserBg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));
        private static readonly SolidColorBrush AssistantBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x5C));
        private static readonly SolidColorBrush SystemBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                ChatMessage.Role.User => UserBg,
                ChatMessage.Role.Assistant => AssistantBg,
                ChatMessage.Role.System => SystemBg,
                _ => UserBg
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