using System.Net;
using System.Net.Http;

namespace MicTap.Core.Tests;

public sealed class RouterAiTranscriberTests
{
    private const string Key = "test-key";

    [Fact]
    public async Task Phrase_goes_as_wav_with_model_language_and_bearer_key_and_the_text_comes_back()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"text":"  привет, мир ","usage":{"seconds":1.6,"cost":0.01,"input_tokens":83,"output_tokens":30}}""");
        using var http = new HttpClient(handler);
        var spent = new List<CloudSpend>();
        var transcriber = new RouterAiTranscriber(http, "openai/whisper-large-v3", () => Key, spent.Add);

        IReadOnlyList<TranscriptSegment> segments = await transcriber.TranscribeAsync(FakeSpeech.Speech(1.5), "ru", CancellationToken.None);

        TranscriptSegment segment = Assert.Single(segments);
        Assert.Equal(("привет, мир", TimeSpan.FromSeconds(1.5)), (segment.Text, segment.End));
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(RouterAiTranscriber.Endpoint, handler.Uri);
        Assert.Equal($"Bearer {Key}", handler.Authorization);
        Assert.Equal("openai/whisper-large-v3", handler.Fields[ "model" ]);
        Assert.Equal("ru", handler.Fields[ "language" ]);
        Assert.Equal("phrase.wav", handler.FileName);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(handler.File!, 0, 4));
        Assert.Equal(WavHeader.Size + 1.5 * 16000 * 2, handler.File!.Length);
        Assert.Equal([ new CloudSpend(1.6, 0.01, 83, 30, 2) ], spent);
    }

    [Fact]
    public void Session_spending_adds_up_in_cloud_json_and_the_project_total_sums_its_sessions()
    {
        using var temp = new TempFolder();
        ProjectInfo project = Projects.Create(temp.Path, new ProjectSettings("Облако", [ "ru" ], AudioRetention.Keep));
        SessionInfo first = SessionFolders.Open(project.Folder, "Пара 1");
        SessionInfo second = SessionFolders.Open(project.Folder, "Пара 2");
        foreach (SessionInfo session in new[] { first, second })
        {
            new SessionMeta(
                [ new SessionStart(DateTimeOffset.Now, 0) ], "Mic", MetaFormat.From(PcmFormat.Mono16k), 60, "test",
                [ new LanguageMark(0, "ru") ]).WriteTo(session);
        }

        CloudUsageLog.Add(first, "x-ai/grok-stt-1.0", new CloudSpend(10, 0.5, Words: 20));
        CloudUsage firstTotal = CloudUsageLog.Add(first, "openai/gpt-4o-mini-transcribe", new CloudSpend(5, 0.2, 40, 9, 8));
        CloudUsageLog.Add(second, "x-ai/grok-stt-1.0", new CloudSpend(20, 1, Words: 30));
        CloudUsage project1 = CloudUsageLog.Total(project.Folder);

        Assert.Equal((2, 15.0, 0.7), (firstTotal.Requests, firstTotal.Seconds, firstTotal.Rubles));
        Assert.Equal(new ModelUsage(1, 5, 0.2, 40, 9, 8), CloudUsageLog.Read(first).Of("openai/gpt-4o-mini-transcribe"));
        Assert.Equal((3, 1.7), (project1.Requests, Math.Round(project1.Rubles, 2)));
        Assert.Equal(new ModelUsage(2, 30, 1.5, 0, 0, 50), project1.Of("x-ai/grok-stt-1.0"));
        Assert.Equal(0, CloudUsageLog.Read(new SessionInfo("нет", Path.Combine(project.Folder, "нет"))).Requests);
    }

    [Fact]
    public void Schema_1_cloud_json_reads_its_totals_as_spent_without_a_model()
    {
        using var temp = new TempFolder();
        var session = new SessionInfo("Пара", temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "cloud.json"), """{ "requests": 5, "seconds": 46.8, "rubles": 0.016, "schemaVersion": 1 }""");

        CloudUsage usage = CloudUsageLog.Add(session, "x-ai/grok-stt-1.0", new CloudSpend(2, 0.01, Words: 3));

        Assert.Equal((6, 0.026), (usage.Requests, Math.Round(usage.Rubles, 3)));
        Assert.Equal(0.016, Math.Round(usage.RublesWithoutModel, 3));
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(Path.Combine(temp.Path, "cloud.json")));
    }

    [Fact]
    public async Task Answers_arriving_at_once_all_count_in_cloud_json()
    {
        using var temp = new TempFolder();
        var session = new SessionInfo("Пара", temp.Path);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => CloudUsageLog.Add(session, $"m/{i % 4}", new CloudSpend(1, 0.5)))));

        CloudUsage usage = CloudUsageLog.Read(session);
        Assert.Equal((40, 40.0, 20.0), (usage.Requests, usage.Seconds, usage.Rubles));
        Assert.All(usage.ByModel.Values, model => Assert.Equal(10, model.Requests));
    }

    [Fact]
    public void Catalog_gives_transcription_models_priced_by_the_second_first_and_cheapest_first()
    {
        const string catalog = """
            {"data":[
              {"id":"openai/gpt-4o-transcribe","name":"GPT-4o Transcribe","architecture":{"output_modalities":["transcription"]},"pricing":{"prompt":0.0002,"completion":0.001}},
              {"id":"openai/whisper-large-v3","name":"Whisper Large v3","architecture":{"output_modalities":["transcription"]},"pricing":{"seconds":0.0008}},
              {"id":"openai/gpt-5","name":"GPT-5","architecture":{"output_modalities":["text"]},"pricing":{"prompt":0.1}},
              {"id":"openai/whisper-large-v3-turbo","name":"Whisper Turbo","architecture":{"output_modalities":["transcription"]},"pricing":{"seconds":0.0003}}
            ]}
            """;

        IReadOnlyList<CloudModelInfo> models = RouterAiModels.Parse(catalog);

        Assert.Equal([ "openai/whisper-large-v3-turbo", "openai/whisper-large-v3", "openai/gpt-4o-transcribe" ], models.Select(m => m.Id));
        Assert.Equal(0.018, models[ 0 ].RublesPerMinute!.Value, 6);
        Assert.Null(models[ 2 ].RublesPerMinute);
    }

    [Fact]
    public async Task Http_error_no_key_and_a_non_transcript_answer_are_cloud_errors_with_the_reason()
    {
        using var http401 = new HttpClient(new FakeHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid api key"}}"""));
        using var httpJunk = new HttpClient(new FakeHandler(HttpStatusCode.OK, "<html>maintenance</html>"));
        using var httpOk = new HttpClient(new FakeHandler(HttpStatusCode.OK, """{"text":"x"}"""));

        var rejected = await Assert.ThrowsAsync<CloudTranscriptionException>(() =>
            new RouterAiTranscriber(http401, "m/x", () => Key).TranscribeAsync(FakeSpeech.Speech(0.5), "ru", CancellationToken.None));
        var junk = await Assert.ThrowsAsync<CloudTranscriptionException>(() =>
            new RouterAiTranscriber(httpJunk, "m/x", () => Key).TranscribeAsync(FakeSpeech.Speech(0.5), "ru", CancellationToken.None));
        var noKey = await Assert.ThrowsAsync<CloudTranscriptionException>(() =>
            new RouterAiTranscriber(httpOk, "m/x", () => null).TranscribeAsync(FakeSpeech.Speech(0.5), "ru", CancellationToken.None));

        Assert.Contains("401", rejected.Message);
        Assert.Contains("invalid api key", rejected.Message);
        Assert.Contains("maintenance", junk.Message);
        Assert.Contains("ключа", noKey.Message);
    }

    [Fact]
    public async Task Empty_text_is_no_phrase()
    {
        using var http = new HttpClient(new FakeHandler(HttpStatusCode.OK, """{"text":"   "}"""));

        IReadOnlyList<TranscriptSegment> segments =
            await new RouterAiTranscriber(http, "m/x", () => Key).TranscribeAsync(FakeSpeech.Speech(0.5), "ja", CancellationToken.None);

        Assert.Empty(segments);
    }

    [Fact]
    public void Wav_of_samples_reads_back_as_the_same_samples()
    {
        using var temp = new TempFolder();
        float[] samples = [ 0f, 0.5f, -0.5f, 1f, -1f, 2f ];
        string path = Path.Combine(temp.Path, "phrase.wav");

        File.WriteAllBytes(path, WavSamples.ToWav(samples));
        float[] back = WavSamples.Read(path);

        Assert.Equal(samples.Length, back.Length);
        Assert.All(samples.Zip(back), pair => Assert.Equal(Math.Clamp(pair.First, -1f, 1f), pair.Second, 0.001f));
    }

    [Fact]
    public void Key_is_saved_read_and_deleted_in_the_credential_manager()
    {
        string target = $"mictap-test/{Guid.NewGuid():N}";
        try
        {
            Assert.Null(CloudKeys.Read(target));

            CloudKeys.Save("sk-test-ключ", target);
            string? saved = CloudKeys.Read(target);
            CloudKeys.Delete(target);

            Assert.Equal("sk-test-ключ", saved);
            Assert.Null(CloudKeys.Read(target));
            CloudKeys.Delete(target); // nothing to delete is fine
        }
        finally
        {
            CloudKeys.Delete(target);
        }
    }

    /// <summary>Answers every request with one response and keeps what the request carried.</summary>
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public Dictionary<string, string> Fields { get; } = [];

        public string? FileName { get; private set; }

        public byte[]? File { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            foreach (HttpContent part in (MultipartFormDataContent)request.Content!)
            {
                string name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "file")
                {
                    FileName = part.Headers.ContentDisposition.FileName!.Trim('"');
                    File = await part.ReadAsByteArrayAsync(cancellationToken);
                }
                else
                {
                    Fields[ name ] = await part.ReadAsStringAsync(cancellationToken);
                }
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
