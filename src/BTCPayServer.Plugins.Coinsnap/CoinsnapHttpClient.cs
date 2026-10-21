using System.Net;

namespace BTCPayServer.Plugins.Coinsnap;

internal sealed record CoinsnapHttpResponse(
    HttpStatusCode StatusCode,
    string Body,
    Uri FinalUri,
    TimeSpan? RetryAfter)
{
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
}

internal sealed class CoinsnapHttpClient
{
    private readonly HttpClient _httpClient;

    public CoinsnapHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CoinsnapHttpResponse> GetAsync(Uri initialUri, string fieldName, CancellationToken cancellationToken)
    {
        CoinsnapUrlPolicy.Validate(initialUri, fieldName);
        var current = initialUri;

        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                if (redirects >= CoinsnapConstants.MaxRedirects)
                    throw new HttpRequestException($"Coinsnap {fieldName} exceeded the redirect limit.");
                current = CoinsnapUrlPolicy.ValidateRedirect(current, response.Headers.Location, fieldName);
                continue;
            }

            if (response.Content.Headers.ContentLength is > CoinsnapConstants.MaxResponseBytes)
                throw new HttpRequestException($"Coinsnap {fieldName} response was too large.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
                if (read == 0)
                    break;
                if (buffer.Length + read > CoinsnapConstants.MaxResponseBytes)
                    throw new HttpRequestException($"Coinsnap {fieldName} response was too large.");
                buffer.Write(chunk, 0, read);
            }

            var body = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            var retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate)
            {
                var delta = retryDate - DateTimeOffset.UtcNow;
                retryAfter = delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            return new CoinsnapHttpResponse(response.StatusCode, body, current, retryAfter);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
