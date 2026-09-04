using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Exercises the bound on the one route that accepts an upload, at the two places it lives: the
/// endpoint's own metadata, and the server that has to enforce it.
///
/// The endpoint refuses a *file* longer than <see cref="ImportContract.MaxUploadBytes"/> itself, which
/// <see cref="ImportEndpointsHttpTests"/> covers. It cannot refuse an oversized *body* itself, because
/// by the time it could look, the body would already have been read: that bound is declared as route
/// metadata and applied by the server, and this is where that wiring is proven.
/// </summary>
public sealed class ImportUploadLimitsHttpTests : IAsyncLifetime
{
    private const string Header = "Name,Quantity,Unit,Location,Note";
    private const string ImportRoutePrefix = "/api/inventories/{inventoryId:guid}/import";

    /// <summary>
    /// What the route allows for transport framing beyond the file itself: multipart part headers and
    /// boundaries, and the slack an intermediary needs when it re-frames the body it forwards - a
    /// proxy that re-chunks a maximum-sized upload must not turn a valid import into a 413. It is
    /// deliberately generous, because it buys margin rather than capacity: what may be imported is the
    /// file bound, which this number does not move.
    /// </summary>
    private const int MultipartFramingBytes = 64 * 1024;

    /// <summary>The bound the route declares: the file bound plus that framing allowance.</summary>
    private const long MaxRequestBodyBytes = ImportContract.MaxUploadBytes + MultipartFramingBytes;

    private readonly ServerRequestSizeLimitLog _requestSizeLimits = new();

    private SqliteWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory(
            configureTestServices: services => services.AddServerEnforcedRequestBodySizeLimit(_requestSizeLimits));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    private IReadOnlyList<RouteEndpoint> ImportEndpoints() =>
    [
        .. _factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(ImportRoutePrefix, StringComparison.Ordinal) == true),
    ];

    private async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync(string displayName)
    {
        var jar = new CookieJar();

        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = Guid.NewGuid().ToString(),
                displayName,
                activeTenantMember = true,
            }),
        };
        jar.Apply(signInRequest);
        var signInResponse = await _client.SendAsync(signInRequest);
        jar.Capture(signInResponse);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrapRequest = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(bootstrapRequest);
        var bootstrapResponse = await _client.SendAsync(bootstrapRequest);
        jar.Capture(bootstrapResponse);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (jar, body.GetProperty("csrfToken").GetString()!);
    }

    private async Task<HttpResponseMessage> SendAsync(CookieJar jar, HttpRequestMessage request, string? csrfToken = null)
    {
        jar.Apply(request);
        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        var response = await _client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    private async Task<Guid> CreateInventoryAsync(CookieJar jar, string csrfToken, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventories")
        {
            Content = JsonContent.Create(new { name, clientRequestId = Guid.NewGuid().ToString() }),
        };
        var response = await SendAsync(jar, request, csrfToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    // The bound is the upload route's own, not the server's global one and not every route's: an
    // eligibility read or a confirmation carries a small JSON body and has no business accepting two
    // mebibytes.
    [Fact]
    public void Only_the_upload_route_declares_a_body_of_the_file_bound_plus_multipart_framing()
    {
        var endpoints = ImportEndpoints();
        var validate = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText!.EndsWith("/validate", StringComparison.Ordinal));

        var declared = validate.Metadata.GetMetadata<IRequestSizeLimitMetadata>();

        Assert.NotNull(declared);
        Assert.Equal(MaxRequestBodyBytes, declared.MaxRequestBodySize);
        Assert.Equal(4, endpoints.Count);
        Assert.All(
            endpoints.Where(endpoint => endpoint != validate),
            endpoint => Assert.Null(endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()));
    }

    // Whatever the route accepts, it holds in memory. The framework's default 64 KiB buffering
    // threshold would spool an import file to a temp file instead, so the route states its own: a part
    // is either buffered whole in memory or refused before it grows past what memory was promised.
    // This is what the route declares; that a maximum-sized upload is really never written to disk is
    // proven over HTTP by ImportEndpointsHttpTests.
    [Fact]
    public void The_upload_route_buffers_every_body_it_accepts_in_memory()
    {
        var validate = Assert.Single(
            ImportEndpoints(),
            endpoint => endpoint.RoutePattern.RawText!.EndsWith("/validate", StringComparison.Ordinal));

        var form = validate.Metadata.GetMetadata<IFormOptionsMetadata>();

        Assert.NotNull(form);
        Assert.Equal((int?)MaxRequestBodyBytes, form.MemoryBufferThreshold);
        Assert.Equal((long?)MaxRequestBodyBytes, form.MultipartBodyLengthLimit);
    }

    // A body over the route's bound is the server's refusal, not the endpoint's, and the file inside it
    // is deliberately a good one: were the endpoint reached at all it would preview it and store a
    // proposal. What is proven is that the route's declared bound reached the server feature, that not
    // one byte of the body was delivered to the application, and that nothing was validated or stored.
    //
    // The test server implements no IHttpMaxRequestBodySizeFeature, so ServerRequestSizeLimitExtensions
    // supplies one and enforces it where Kestrel does - at the first read, against the declared length.
    // This is evidence about the endpoint's metadata and the framework's wiring, not about Kestrel.
    [Fact]
    public async Task A_body_over_the_route_bound_is_refused_by_the_server_before_the_endpoint_reads_any_of_it()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"{Header}\nSteel Bolts,4,,,\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var body = new MultipartFormDataContent
        {
            { file, "file", "stock.csv" },

            // The file is well within its own bound; the padding is what puts the body past the route's.
            { new StringContent(new string('p', (int)MaxRequestBodyBytes)), "padding" },
        };
        Assert.True(body.Headers.ContentLength > MaxRequestBodyBytes);

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate") { Content = body },
            csrfToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.DoesNotContain("file_too_large", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var limit = Assert.Single(_requestSizeLimits.Records, record => record.AppliedLimit is not null);
        Assert.Equal($"/api/inventories/{inventoryId}/import/validate", limit.Path);
        Assert.Equal(MaxRequestBodyBytes, limit.AppliedLimit);
        Assert.True(limit.RefusedByTheServer);
        Assert.Equal(0, limit.BytesDelivered);

        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        Assert.False(await database.ImportProposals.AnyAsync());
    }

    // The framing allowance is transport slack, not room for a larger import. A file part past the file
    // bound but inside the body bound is delivered whole - the server has no objection to it - and the
    // endpoint refuses it on its own exact length, with the file bound's own typed answer rather than
    // the body bound's. That is what keeps the two numbers independent: raising the framing allowance
    // for intermediaries can never raise what may be imported.
    [Fact]
    public async Task A_file_part_past_the_file_bound_is_refused_at_the_file_bound_however_much_framing_the_body_is_allowed()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var overTheFileBound = Encoding.UTF8.GetBytes(
            $"{Header}\n" + new string('a', ImportContract.MaxUploadBytes + (MultipartFramingBytes / 2) - Header.Length - 1));
        Assert.Equal(ImportContract.MaxUploadBytes + (MultipartFramingBytes / 2), overTheFileBound.Length);

        var file = new ByteArrayContent(overTheFileBound);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var body = new MultipartFormDataContent { { file, "file", "stock.csv" } };
        var contentLength = body.Headers.ContentLength!.Value;
        Assert.True(contentLength < MaxRequestBodyBytes);

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate") { Content = body },
            csrfToken);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("file_too_large", JsonDocument.Parse(payload).RootElement.GetProperty("code").GetString());

        var limit = Assert.Single(_requestSizeLimits.Records, record => record.AppliedLimit is not null);
        Assert.Equal(MaxRequestBodyBytes, limit.AppliedLimit);
        Assert.False(limit.RefusedByTheServer);
        Assert.InRange(limit.BytesDelivered, overTheFileBound.Length, contentLength);
    }
}
