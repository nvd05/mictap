using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MicTap.App;

/// <summary>The new name typed in the rename dialog.</summary>
public sealed partial class RenameEditor(string name) : ObservableObject
{
    [ObservableProperty]
    private string _name = name;
}

/// <summary>Shows the empty-transcript hint while there are no lines and nothing is being recorded.</summary>
public sealed class EmptyTranscriptConverter : IMultiValueConverter
{
    public static EmptyTranscriptConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [ int count, bool live ] && count == 0 && !live ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
