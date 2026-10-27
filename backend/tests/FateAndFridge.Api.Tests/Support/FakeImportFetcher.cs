using System.Text;
using FateAndFridge.Api.Imports;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Deterministic <see cref="IImportFetcher"/> for import tests: no network
/// access ever occurs, and each handler can be scripted per test. Failed
/// fetches use the structured <see cref="ImportFetchResult.Failure"/> shape so
/// the endpoint mapping can be exercised without a real transport.
/// </summary>
internal sealed class FakeImportFetcher : IImportFetcher
{
    private Func<string, ImportFetchResult> _pageHandler =
        _ => ImportFetchResult.Failure(ImportFetchFailureReason.HttpError, "no page handler configured");

    private Func<string, ImportFetchResult> _imageHandler =
        _ => ImportFetchResult.Failure(ImportFetchFailureReason.HttpError, "no image handler configured");

    public int PageCallCount { get; private set; }

    public int ImageCallCount { get; private set; }

    public List<string> RequestedPageUrls { get; } = [];

    public FakeImportFetcher OnPage(Func<string, ImportFetchResult> handler)
    {
        _pageHandler = handler;
        return this;
    }

    public FakeImportFetcher OnPageFailure(ImportFetchFailureReason reason, string? detail = null) =>
        OnPage(_ => ImportFetchResult.Failure(reason, detail));

    public FakeImportFetcher OnPageHtml(string html, Uri finalUrl, string contentType = "text/html; charset=utf-8") =>
        OnPage(_ => ImportFetchResult.Success(finalUrl, 200, contentType, Encoding.UTF8.GetBytes(html)));

    public FakeImportFetcher OnImage(Func<string, ImportFetchResult> handler)
    {
        _imageHandler = handler;
        return this;
    }

    public Task<ImportFetchResult> FetchPageAsync(string url, CancellationToken cancellationToken = default)
    {
        PageCallCount++;
        RequestedPageUrls.Add(url);
        return Task.FromResult(_pageHandler(url));
    }

    public Task<ImportFetchResult> FetchImageAsync(string url, CancellationToken cancellationToken = default)
    {
        ImageCallCount++;
        return Task.FromResult(_imageHandler(url));
    }
}
