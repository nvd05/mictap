using System.Net.Http;
using System.Text.Json;

namespace MicTap.Core;

/// <summary>A speech-to-text model of Router AI; <paramref name="RublesPerMinute"/> is null for models priced by tokens.</summary>
public sealed record CloudModelInfo(string Id, string Name, double? RublesPerMinute);

/// <summary>The speech-to-text models of Router AI with their prices, from its public catalog (no key needed).</summary>
public static class RouterAiModels
{
    public static readonly Uri Catalog = new("https://routerai.ru/api/v1/models");

    public static async Task<IReadOnlyList<CloudModelInfo>> LoadAsync(HttpClient http, CancellationToken cancellation) =>
        Parse(await http.GetStringAsync(Catalog, cancellation));

    /// <summary>Models whose output is a transcription, those priced by the second first and cheapest first.</summary>
    public static IReadOnlyList<CloudModelInfo> Parse(string catalog)
    {
        using JsonDocument json = JsonDocument.Parse(catalog);
        JsonElement models = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement : json.RootElement.GetProperty("data");
        var found = new List<CloudModelInfo>();
        foreach (JsonElement model in models.EnumerateArray())
        {
            bool transcribes = model.TryGetProperty("architecture", out JsonElement architecture)
                && architecture.TryGetProperty("output_modalities", out JsonElement outputs)
                && outputs.EnumerateArray().Any(output => output.GetString() == "transcription");
            if (!transcribes)
            {
                continue;
            }

            double? perMinute =
                model.TryGetProperty("pricing", out JsonElement pricing) && pricing.TryGetProperty("seconds", out JsonElement seconds)
                && seconds.TryGetDouble(out double perSecond)
                    ? perSecond * 60
                    : null;
            string id = model.GetProperty("id").GetString()!;
            found.Add(new CloudModelInfo(id, model.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? id : id, perMinute));
        }

        return [ .. found.OrderBy(m => m.RublesPerMinute is null).ThenBy(m => m.RublesPerMinute).ThenBy(m => m.Id, StringComparer.Ordinal) ];
    }
}
