using System.Security.Cryptography;

namespace MicTap.Core;

/// <summary>A model file and the published size and SHA-256 that a download must match.</summary>
public sealed record ModelSpec(string FileName, Uri Source, long Size, string Sha256)
{
    public static ModelSpec LargeV3Turbo { get; } = new(
        "ggml-large-v3-turbo.bin",
        new Uri("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin"),
        1_624_555_275,
        "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69");
}

/// <summary>Keeps a model in a folder; downloads it once, through a .part file renamed only after the hash matches.</summary>
public sealed class ModelStore(string folder, ModelSpec spec)
{
    public ModelSpec Spec { get; } = spec;

    public string ModelPath { get; } = Path.Combine(folder, spec.FileName);

    /// <summary>The model is on disk with the expected size; the hash was checked when it was downloaded.</summary>
    public bool IsReady => new FileInfo(ModelPath) is { Exists: true } file && file.Length == Spec.Size;

    /// <summary>Downloads the model; <paramref name="progress"/> gets the downloaded fraction, 0..1, in steps of 1 %.</summary>
    public async Task DownloadAsync(HttpClient http, IProgress<double> progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        string part = ModelPath + ".part";
        using HttpResponseMessage response =
            await http.GetAsync(Spec.Source, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        await using (Stream input = await response.Content.ReadAsStreamAsync(cancellation))
        await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            byte[] buffer = new byte[1 << 20];
            int percent = -1;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                hash.AppendData(buffer, 0, read);
                received += read;
                int now = (int)(Math.Min(received, Spec.Size) * 100 / Spec.Size);
                if (now != percent)
                {
                    percent = now;
                    progress.Report(now / 100.0);
                }
            }

            output.Flush(flushToDisk: true);
        }

        string sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (received != Spec.Size || sha256 != Spec.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException(
                $"Downloaded {Spec.FileName} does not match: {received} bytes, SHA-256 {sha256}; "
                + $"expected {Spec.Size} bytes, SHA-256 {Spec.Sha256}.");
        }

        File.Move(part, ModelPath, overwrite: true);
    }
}
