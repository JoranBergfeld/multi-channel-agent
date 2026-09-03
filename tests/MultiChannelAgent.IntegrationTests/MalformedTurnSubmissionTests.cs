using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Docker-free HTTP-boundary coverage (see <see cref="SqliteWebApplicationFactory"/>) for malformed
/// <c>POST /api/turns</c> requests: a missing, null, or blank required field (<c>nativeMessageId</c>,
/// <c>channelConversationId</c>, <c>contentText</c>) must be rejected with a controlled <c>400</c> and
/// useful validation details, never surface as an unhandled <c>500</c> from
/// <see cref="MultiChannelAgent.Domain.Turns.InboundTurn.Create"/> throwing on a null/blank value.
/// Valid requests must still receive <c>202 Accepted</c> - the fix must not regress the happy path.
/// </summary>
public sealed class MalformedTurnSubmissionTests : IClassFixture<SqliteWebApplicationFactory>
{
    private readonly SqliteWebApplicationFactory _factory;

    public MalformedTurnSubmissionTests(SqliteWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("nativeMessageId")]
    [InlineData("channelConversationId")]
    [InlineData("contentText")]
    public async Task Missing_required_field_is_rejected_with_400_instead_of_500(string missingField)
    {
        var client = _factory.CreateClient();

        var body = new Dictionary<string, string?>
        {
            ["nativeMessageId"] = "native-missing-1",
            ["channelConversationId"] = "conversation-missing-1",
            ["contentText"] = "hello",
        };
        body.Remove(missingField);

        var response = await client.PostAsync("/api/turns", JsonContent(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(missingField, out _));
    }

    [Theory]
    [InlineData("nativeMessageId")]
    [InlineData("channelConversationId")]
    [InlineData("contentText")]
    public async Task Null_required_field_is_rejected_with_400_instead_of_500(string nullField)
    {
        var client = _factory.CreateClient();

        var body = new Dictionary<string, string?>
        {
            ["nativeMessageId"] = "native-null-1",
            ["channelConversationId"] = "conversation-null-1",
            ["contentText"] = "hello",
        };
        body[nullField] = null;

        var response = await client.PostAsync("/api/turns", JsonContent(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(nullField, out _));
    }

    [Theory]
    [InlineData("nativeMessageId")]
    [InlineData("channelConversationId")]
    [InlineData("contentText")]
    public async Task Blank_required_field_is_rejected_with_400_instead_of_500(string blankField)
    {
        var client = _factory.CreateClient();

        var body = new Dictionary<string, string?>
        {
            ["nativeMessageId"] = "native-blank-1",
            ["channelConversationId"] = "conversation-blank-1",
            ["contentText"] = "hello",
        };
        body[blankField] = "   ";

        var response = await client.PostAsync("/api/turns", JsonContent(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(blankField, out _));
    }

    [Fact]
    public async Task Valid_request_still_receives_202_accepted()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/turns", new
        {
            nativeMessageId = "native-valid-1",
            channelConversationId = "conversation-valid-1",
            contentText = "hello valid",
            locale = "en-US",
            traceId = "trace-valid-1",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, accepted.GetProperty("turnId").GetGuid());
        Assert.False(accepted.GetProperty("alreadyAccepted").GetBoolean());
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
