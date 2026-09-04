using Microsoft.Extensions.Logging;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Pins what the default <see cref="LoggingDeliverySender"/> may write. It stands in for real channel
/// adapters, so it is the one place a Delivery's whole payload could reach a log sink - and a
/// confirmation Delivery's payload carries a live single-use confirmation token, alongside Stock
/// Entry names and Quantities that the non-disclosure rules keep out of diagnostics everywhere else.
/// </summary>
public sealed class LoggingDeliverySenderTests
{
    private sealed class CapturingLogger : ILogger<LoggingDeliverySender>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task Dispatching_a_Delivery_never_writes_its_payload_to_the_log()
    {
        const string Token = "8Fh3kQxWvZ0aBcDeFgHiJkLmNoPqRsTuVwXyZ01aBcD";
        var payload = $"{{\"kind\":\"stock_proposal\",\"token\":\"{Token}\",\"changes\":[{{\"name\":\"Steel Bolts\"}}]}}";
        var logger = new CapturingLogger();
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", payload, DateTimeOffset.UnixEpoch);

        Assert.True(await new LoggingDeliverySender(logger).TrySendAsync(delivery, CancellationToken.None));

        var message = Assert.Single(logger.Messages);
        Assert.DoesNotContain(Token, message, StringComparison.Ordinal);
        Assert.DoesNotContain("Steel Bolts", message, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, message, StringComparison.Ordinal);

        // It still says enough to trace a Delivery: which Turn, which channel, and how big the answer was.
        Assert.Contains(delivery.TurnId.Value.ToString(), message, StringComparison.Ordinal);
        Assert.Contains("conversation", message, StringComparison.Ordinal);
        Assert.Contains(payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
    }
}
