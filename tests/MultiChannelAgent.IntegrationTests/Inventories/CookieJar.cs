namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// A minimal cookie jar for HTTP integration tests: <see cref="HttpClient"/> instances created via
/// <c>WebApplicationFactory.CreateClient()</c> do not automatically store or resend cookies (unlike a
/// real browser), so tests that must exercise the real Cookie authentication/CSRF/web-conversation
/// cookies capture <c>Set-Cookie</c> response headers here and replay them as a <c>Cookie</c> request
/// header on subsequent calls.
/// </summary>
public sealed class CookieJar
{
    private readonly Dictionary<string, string> _cookies = [];

    public IReadOnlyDictionary<string, string> Cookies => _cookies;

    public void Capture(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return;
        }

        foreach (var header in setCookieHeaders)
        {
            var nameValuePart = header.Split(';', 2)[0];
            var separatorIndex = nameValuePart.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var name = nameValuePart[..separatorIndex];
            var value = nameValuePart[(separatorIndex + 1)..];
            _cookies[name] = value;
        }
    }

    public void Apply(HttpRequestMessage request)
    {
        if (_cookies.Count == 0)
        {
            return;
        }

        request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}")));
    }
}
