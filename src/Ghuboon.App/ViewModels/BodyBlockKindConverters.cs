using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Ghuboon.App.ViewModels;

/// <summary>
/// Toggles between the plain-Markdown view and the Expander-wrapped view in
/// the detail pane based on <see cref="BodyBlockKind"/>. Used as
/// <c>{x:Static vm:BodyBlockKindConverters.IsMarkdown}</c> in MainWindow.axaml.
/// </summary>
public static class BodyBlockKindConverters
{
    public static readonly IValueConverter IsMarkdown = new EqualsConverter(BodyBlockKind.Markdown);
    public static readonly IValueConverter IsDetails = new EqualsConverter(BodyBlockKind.Details);

    private sealed class EqualsConverter : IValueConverter
    {
        private readonly BodyBlockKind _kind;
        public EqualsConverter(BodyBlockKind kind) => _kind = kind;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is BodyBlockKind k && k == _kind;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
