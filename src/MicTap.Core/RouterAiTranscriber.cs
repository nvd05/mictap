using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MicTap.Core;

/// <summary>A cloud call failed in a way worth retrying: no key yet, an HTTP error, an answer that is not a transcript.</summary>
public sealed class CloudTranscriptionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Router AI (routerai.ru), an OpenAI-compatible cloud: a phrase goes as WAV to POST /audio/transcriptions with the
/// project's model and language, the answer's text becomes the phrase. The key is the user's, read on every call, so a
/// key saved while phrases wait is used by their next attempt.
/// </summary>
public sealed class RouterAiTranscriber(HttpClient http, string model, Func<string?> key) : ITranscriber
{
    public static readonly Uri Endpoint = new("https://routerai.ru/api/v1/audio/transcriptions");

    public const string DefaultModel = "openai/whisper-large-v3";

    private const int MaxErrorLength = 300;

    public string Runtime => $"Router AI · {model}";

    public bool OnGpu => false;

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(float[] samples, string language, CancellationToken cancellation)
    {
        string apiKey = key() is { Length: > 0 } saved
            ? saved
            : throw new CloudTranscriptionException("нет ключа Router AI — вставьте его в «Настройках»");
        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(WavSamples.ToWav(samples));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", "phrase.wav");
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(language), "language");
        content.Add(new StringContent("0"), "temperature");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using HttpResponseMessage response = await http.SendAsync(request, cancellation);
        string body = await response.Content.ReadAsStringAsync(cancellation);
        if (!response.IsSuccessStatusCode)
        {
            throw new CloudTranscriptionException($"Router AI ответил {(int)response.StatusCode}: {Shorten(body)}");
        }

        string text;
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            text = json.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CloudTranscriptionException($"Router AI прислал не текст: {Shorten(body)}", ex);
        }

        return text.Length == 0 ? [] : [ new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(samples.Length / 16000.0), text) ];
    }

    /// <summary>Nothing to free: the HttpClient belongs to the caller.</summary>
    public void Dispose()
    {
    }

    private static string Shorten(string body)
    {
        string line = body.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxErrorLength ? line : line[ ..MaxErrorLength ] + "…";
    }
}
