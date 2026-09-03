using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests;

public class OutcomeTests
{
    [Fact]
    public void Completed_outcome_carries_turn_id_code_and_summary()
    {
        var turnId = TurnId.NewId();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var outcome = Outcome.Completed(turnId, code: "echoed", summary: "Echoed: hello", createdAt);

        Assert.Equal(turnId, outcome.TurnId);
        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(OutcomeCategory.Completed, outcome.Category);
        Assert.Equal("echoed", outcome.Code);
        Assert.Equal("Echoed: hello", outcome.Summary);
        Assert.Equal(createdAt, outcome.CreatedAt);
    }

    // A Turn whose request was understood, authorized, and answered has been processed successfully,
    // even when the semantic answer is "nothing matched" or "you may not see that". Recording those
    // as Failed would conflate a deterministic domain answer with a broken system, hiding real
    // outages behind ordinary conversational results.
    [Theory]
    [InlineData(OutcomeCategory.NotFound)]
    [InlineData(OutcomeCategory.Ambiguous)]
    [InlineData(OutcomeCategory.Forbidden)]
    [InlineData(OutcomeCategory.Invalid)]
    [InlineData(OutcomeCategory.Conflict)]
    [InlineData(OutcomeCategory.ConfirmationRequired)]
    public void Semantic_categories_are_recorded_as_completed_processing(OutcomeCategory category)
    {
        var outcome = Outcome.Record(TurnId.NewId(), category, "some_code", "A semantic answer.", DateTimeOffset.UtcNow);

        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal(category, outcome.Category);
    }

    // Failed is reserved for the system itself failing to produce an answer.
    [Fact]
    public void Only_a_system_or_model_failure_is_recorded_as_failed()
    {
        var outcome = Outcome.SystemFailure(TurnId.NewId(), "model_error", "The model could not answer.", DateTimeOffset.UtcNow);

        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Equal(OutcomeCategory.TransientFailure, outcome.Category);
    }

    [Fact]
    public void Every_category_except_transient_failure_reports_completed_processing()
    {
        foreach (var category in Enum.GetValues<OutcomeCategory>())
        {
            var outcome = Outcome.Record(TurnId.NewId(), category, "some_code", "summary", DateTimeOffset.UtcNow);
            var expected = category == OutcomeCategory.TransientFailure ? OutcomeStatus.Failed : OutcomeStatus.Completed;

            Assert.Equal(expected, outcome.Status);
        }
    }

    [Theory]
    [InlineData(OutcomeCategory.Completed, "completed")]
    [InlineData(OutcomeCategory.ConfirmationRequired, "confirmation_required")]
    [InlineData(OutcomeCategory.Ambiguous, "ambiguous")]
    [InlineData(OutcomeCategory.NotFound, "not_found")]
    [InlineData(OutcomeCategory.Forbidden, "forbidden")]
    [InlineData(OutcomeCategory.Conflict, "conflict")]
    [InlineData(OutcomeCategory.Invalid, "invalid")]
    [InlineData(OutcomeCategory.TransientFailure, "transient_failure")]
    public void Categories_expose_a_stable_machine_text(OutcomeCategory category, string expected) =>
        Assert.Equal(expected, category.ToMachineText());

    [Fact]
    public void Completed_outcome_defaults_to_no_payload()
    {
        var outcome = Outcome.Completed(TurnId.NewId(), "echoed", "Echoed: hello", DateTimeOffset.UtcNow);

        Assert.Null(outcome.Payload);
    }

    [Fact]
    public void Completed_outcome_may_carry_a_versioned_typed_payload()
    {
        const string payload = """{"version":1,"kind":"stock_list","rows":[]}""";

        var outcome = Outcome.Completed(TurnId.NewId(), "completed", "1 Stock Entry found.", DateTimeOffset.UtcNow, payload);

        Assert.Equal(payload, outcome.Payload);
    }

    [Fact]
    public void Outcome_is_not_terminal_state_holder_for_pending_processing()
    {
        // Only Completed/Failed statuses are constructible via the public factories; there is no
        // "Pending" Outcome because Outcome represents the terminal semantic result of processing.
        var values = Enum.GetValues<OutcomeStatus>();
        Assert.Equal(new[] { OutcomeStatus.Completed, OutcomeStatus.Failed }, values);
    }
    // A retained payload is an ephemeral projection, not part of the permanent answer, so it carries
    // an explicit expiry from the moment it is recorded rather than accumulating forever.
    [Fact]
    public void A_recorded_payload_carries_an_explicit_expiry()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var outcome = Outcome.Completed(TurnId.NewId(), "completed", "1 Stock Entry found.", createdAt, """{"kind":"stock_list"}""");

        Assert.Equal(createdAt + Outcome.PayloadRetention, outcome.PayloadExpiresAt);
    }

    [Fact]
    public void An_outcome_with_no_payload_has_nothing_to_expire()
    {
        var outcome = Outcome.Completed(TurnId.NewId(), "echoed", "Echoed: hello", DateTimeOffset.UtcNow);

        Assert.Null(outcome.PayloadExpiresAt);
    }

    // Cleanup drops only the projection; the semantic answer itself is permanent.
    [Fact]
    public void Discarding_an_expired_payload_keeps_the_semantic_answer()
    {
        var outcome = Outcome.Record(
            TurnId.NewId(), OutcomeCategory.Ambiguous, "ambiguous", "2 Stock Entries match.", DateTimeOffset.UtcNow, """{"kind":"stock_find"}""");

        var cleaned = outcome.WithoutRetainedPayload();

        Assert.Null(cleaned.Payload);
        Assert.Null(cleaned.PayloadExpiresAt);
        Assert.Equal(OutcomeCategory.Ambiguous, cleaned.Category);
        Assert.Equal("ambiguous", cleaned.Code);
        Assert.Equal("2 Stock Entries match.", cleaned.Summary);
    }
}
