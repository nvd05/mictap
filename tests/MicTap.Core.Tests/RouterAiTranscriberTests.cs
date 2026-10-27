using System.Net;
using System.Net.Http;

namespace MicTap.Core.Tests;

public sealed class RouterAiTranscriberTests
{
    private const string Key = "test-key";

    [Fact]
    public async Task Phrase_goes_as_wav_with_model_language_and_bearer_key_and_the_text_comes_back()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"text":"  привет, мир ","usage":{"seconds":1.5,"cost":0.01}}""");
        using var http = new HttpClient(handler);
        var transcriber = new RouterAiTranscriber(http, "openai/whisper-large-v3", () => Key);

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
