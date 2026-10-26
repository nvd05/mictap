using System.Net;
using System.Security.Cryptography;

namespace MicTap.Core.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private static readonly byte[] Model = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Download_lands_under_the_model_name_with_progress_up_to_one()
    {
        var store = new ModelStore(Path.Combine(_temp.Path, "models"), SpecFor(Model));
        var progress = new List<double>();

        await store.DownloadAsync(Serving(Model), new SyncProgress(progress), CancellationToken.None);

        Assert.True(store.IsReady);
        Assert.Equal(Model, File.ReadAllBytes(store.ModelPath));
        Assert.Equal(1.0, progress[ ^1 ]);
        Assert.Equal(progress.Order(), progress);
        Assert.False(File.Exists(store.ModelPath + ".part"));
    }

    [Fact]
    public async Task Corrupted_download_is_rejected_and_leaves_no_model()
    {
        byte[] corrupted = Model.ToArray();
        corrupted[ 1234 ] ^= 0xFF;
        var store = new ModelStore(_temp.Path, SpecFor(Model));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.DownloadAsync(Serving(corrupted), new SyncProgress([]), CancellationToken.None));

        Assert.Contains("SHA-256", error.Message);
        Assert.False(store.IsReady);
        Assert.False(File.Exists(store.ModelPath));
        Assert.False(File.Exists(store.ModelPath + ".part"));
    }

    [Fact]
    public async Task Http_error_leaves_no_model()
    {
        var store = new ModelStore(_temp.Path, SpecFor(Model));
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => store.DownloadAsync(http, new SyncProgress([]), CancellationToken.None));

        Assert.False(File.Exists(store.ModelPath));
    }

    private static ModelSpec SpecFor(byte[] content) => new(
        "model.bin", new Uri("https://models.test/model.bin"), content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));

    private static HttpClient Serving(byte[] content) =>
        new(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Unlike <see cref="Progress{T}"/>, reports synchronously, so the test sees every value.</summary>
    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
