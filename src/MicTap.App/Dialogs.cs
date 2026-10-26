using CommunityToolkit.Mvvm.ComponentModel;

namespace MicTap.App;

/// <summary>The name typed in the rename dialog.</summary>
public sealed partial class RenameEditor(string title, string name, string hint, string confirm = "Переименовать") : ObservableObject
{
    [ObservableProperty]
    private string _name = name;

    public string Title { get; } = title;

    public string Hint { get; } = hint;

    public string Confirm { get; } = confirm;
}
