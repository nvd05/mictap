namespace MicTap.Core.Tests;

/// <summary>A fresh folder under %TEMP%, deleted with its contents on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mictap-tests", Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
