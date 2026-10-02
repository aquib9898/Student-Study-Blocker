namespace StudyBlocker.Service;

// Reads the time from the Date header of HTTPS responses. HTTPS is what
// makes it trustworthy: faking a response needs a root certificate trusted
// by the SYSTEM account, which only an admin can install.
public sealed class NetworkTimeSource
{
    private static readonly Uri[] Endpoints =
    [
        new("https://www.microsoft.com/"),
        new("https://www.cloudflare.com/"),
    ];

    private readonly HttpClient _http;

    public NetworkTimeSource()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SmartStudyBlocker/0.1");
    }

    public async Task<(DateTimeOffset Utc, long MeasuredAtMs)?> TryGetTimeAsync(
        Func<long> monotonicMs, CancellationToken cancellationToken)
    {
        foreach (var endpoint in Endpoints)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
                var sentMs = monotonicMs();
                using var response = await _http.SendAsync(request, cancellationToken);
                var receivedMs = monotonicMs();
                if (response.Headers.Date is { } date)
                    return (date, sentMs + (receivedMs - sentMs) / 2);
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // request timed out; try the next endpoint
            }
        }
        return null;
    }
}
