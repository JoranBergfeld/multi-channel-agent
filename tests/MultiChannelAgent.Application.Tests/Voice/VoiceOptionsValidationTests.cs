using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Application.Tests.Voice;

public sealed class VoiceOptionsValidationTests
{
    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_match_issue_requirements()
    {
        var o = new VoiceOptions();

        Assert.Equal(5, o.GlobalActiveCap);
        Assert.Equal(TimeSpan.FromMinutes(30), o.MaxSessionDuration);
        Assert.Equal(TimeSpan.FromMinutes(25), o.SessionWarningThreshold);
        Assert.Equal(TimeSpan.FromSeconds(60), o.IdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), o.HeartbeatInterval);
        Assert.False(o.Enabled);
        Assert.Equal("en-US-Ava:DragonHDLatestNeural", o.VoiceName);
    }

    // ── Disabled ──────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_is_empty_when_disabled() =>
        Assert.Empty(new VoiceOptions { Enabled = false }.Validate());

    [Fact]
    public void Validate_is_empty_when_disabled_even_with_no_endpoint_or_model() =>
        Assert.Empty(new VoiceOptions { Enabled = false, Endpoint = null, Model = null }.Validate());

    // ── Enabled — happy path ──────────────────────────────────────────────────

    [Fact]
    public void Validate_is_empty_when_enabled_with_all_fields_valid() =>
        Assert.Empty(ValidEnabled().Validate());

    [Fact]
    public void Validate_is_empty_for_legacy_cognitiveservices_endpoint() =>
        Assert.Empty(new VoiceOptions
        {
            Enabled = true,
            Endpoint = "wss://eastus.cognitiveservices.azure.com/voice-live/realtime",
            Model = "gpt-4.1",
        }.Validate());

    // ── Endpoint presence ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_fails_when_endpoint_is_null() =>
        Assert.Contains(
            new VoiceOptions { Enabled = true, Endpoint = null, Model = "gpt-4.1" }.Validate(),
            e => e.Contains("Endpoint"));

    [Fact]
    public void Validate_fails_when_endpoint_is_empty_string() =>
        Assert.Contains(
            new VoiceOptions { Enabled = true, Endpoint = "", Model = "gpt-4.1" }.Validate(),
            e => e.Contains("Endpoint"));

    [Fact]
    public void Validate_fails_when_endpoint_is_whitespace() =>
        Assert.Contains(
            new VoiceOptions { Enabled = true, Endpoint = "   ", Model = "gpt-4.1" }.Validate(),
            e => e.Contains("Endpoint"));

    // ── Model presence ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_fails_when_model_is_null() =>
        Assert.Contains(
            new VoiceOptions { Enabled = true, Endpoint = "wss://x.services.ai.azure.com/v", Model = null }.Validate(),
            e => e.Contains("Model"));

    [Fact]
    public void Validate_fails_when_model_is_empty_string() =>
        Assert.Contains(
            new VoiceOptions { Enabled = true, Endpoint = "wss://x.services.ai.azure.com/v", Model = "" }.Validate(),
            e => e.Contains("Model"));

    [Fact]
    public void Validate_fails_when_model_is_whitespace() =>
        Assert.Contains(
            new VoiceOptions { Enabled = true, Endpoint = "wss://x.services.ai.azure.com/v", Model = " " }.Validate(),
            e => e.Contains("Model"));

    // ── Endpoint URI validation ───────────────────────────────────────────────

    [Theory]
    [InlineData("https://x.services.ai.azure.com/v")]       // wrong scheme — https
    [InlineData("ws://x.services.ai.azure.com/v")]          // wrong scheme — ws (non-secure)
    [InlineData("wss://x.openai.azure.com/v")]              // unsupported domain
    [InlineData("wss://x.example.com/v")]                   // unrelated domain
    [InlineData("not-a-uri")]                               // not a URI at all
    [InlineData("wss://services.ai.azure.com/v")]           // apex domain, no subdomain
    public void Validate_fails_for_invalid_endpoint_uri(string endpoint) =>
        Assert.Contains(
            new VoiceOptions { Enabled = true, Endpoint = endpoint, Model = "gpt-4.1" }.Validate(),
            e => e.Contains("Endpoint"));

    [Theory]
    [InlineData("wss://x.services.ai.azure.com/voice-live/realtime")]
    [InlineData("wss://eastus.services.ai.azure.com/voice-live/realtime")]
    [InlineData("wss://a.services.ai.azure.com")]
    [InlineData("wss://x.cognitiveservices.azure.com/v")]
    [InlineData("wss://eastus.cognitiveservices.azure.com/voice-live/realtime")]
    public void Validate_accepts_supported_wss_endpoints(string endpoint) =>
        Assert.Empty(
            new VoiceOptions { Enabled = true, Endpoint = endpoint, Model = "gpt-4.1" }.Validate());

    // ── GlobalActiveCap ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_fails_when_global_cap_is_zero()
    {
        var o = ValidEnabled();
        o.GlobalActiveCap = 0;

        Assert.Contains(o.Validate(), e => e.Contains("GlobalActiveCap"));
    }

    [Fact]
    public void Validate_fails_when_global_cap_is_negative()
    {
        var o = ValidEnabled();
        o.GlobalActiveCap = -1;

        Assert.Contains(o.Validate(), e => e.Contains("GlobalActiveCap"));
    }

    [Fact]
    public void Validate_succeeds_when_global_cap_is_one()
    {
        var o = ValidEnabled();
        o.GlobalActiveCap = 1;

        Assert.Empty(o.Validate());
    }

    // ── MaxSessionDuration ────────────────────────────────────────────────────

    [Fact]
    public void Validate_fails_when_max_session_duration_is_zero()
    {
        var o = ValidEnabled();
        o.MaxSessionDuration = TimeSpan.Zero;
        // Warning threshold would also be violated; we only check that MaxSessionDuration is flagged
        Assert.Contains(o.Validate(), e => e.Contains("MaxSessionDuration"));
    }

    [Fact]
    public void Validate_fails_when_max_session_duration_is_negative()
    {
        var o = ValidEnabled();
        o.MaxSessionDuration = TimeSpan.FromSeconds(-1);

        Assert.Contains(o.Validate(), e => e.Contains("MaxSessionDuration"));
    }

    // ── SessionWarningThreshold ───────────────────────────────────────────────

    [Fact]
    public void Validate_fails_when_warning_threshold_is_zero()
    {
        var o = ValidEnabled();
        o.SessionWarningThreshold = TimeSpan.Zero;

        Assert.Contains(o.Validate(), e => e.Contains("SessionWarningThreshold"));
    }

    [Fact]
    public void Validate_fails_when_warning_threshold_is_negative()
    {
        var o = ValidEnabled();
        o.SessionWarningThreshold = TimeSpan.FromSeconds(-1);

        Assert.Contains(o.Validate(), e => e.Contains("SessionWarningThreshold"));
    }

    [Fact]
    public void Validate_fails_when_warning_threshold_equals_max_session_duration()
    {
        var o = ValidEnabled();
        o.MaxSessionDuration = TimeSpan.FromMinutes(10);
        o.SessionWarningThreshold = TimeSpan.FromMinutes(10);

        Assert.Contains(o.Validate(), e => e.Contains("SessionWarningThreshold"));
    }

    [Fact]
    public void Validate_fails_when_warning_threshold_exceeds_max_session_duration()
    {
        var o = ValidEnabled();
        o.MaxSessionDuration = TimeSpan.FromMinutes(10);
        o.SessionWarningThreshold = TimeSpan.FromMinutes(15);

        Assert.Contains(o.Validate(), e => e.Contains("SessionWarningThreshold"));
    }

    [Fact]
    public void Validate_succeeds_when_warning_threshold_is_just_below_max()
    {
        var o = ValidEnabled();
        o.MaxSessionDuration = TimeSpan.FromMinutes(10);
        o.SessionWarningThreshold = TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1);

        Assert.Empty(o.Validate());
    }

    // ── IdleTimeout ───────────────────────────────────────────────────────────

    [Fact]
    public void Validate_fails_when_idle_timeout_is_zero()
    {
        var o = ValidEnabled();
        o.IdleTimeout = TimeSpan.Zero;

        Assert.Contains(o.Validate(), e => e.Contains("IdleTimeout"));
    }

    [Fact]
    public void Validate_fails_when_idle_timeout_is_negative()
    {
        var o = ValidEnabled();
        o.IdleTimeout = TimeSpan.FromSeconds(-1);

        Assert.Contains(o.Validate(), e => e.Contains("IdleTimeout"));
    }

    // ── HeartbeatInterval ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_fails_when_heartbeat_interval_is_zero()
    {
        var o = ValidEnabled();
        o.HeartbeatInterval = TimeSpan.Zero;

        Assert.Contains(o.Validate(), e => e.Contains("HeartbeatInterval"));
    }

    [Fact]
    public void Validate_fails_when_heartbeat_interval_is_negative()
    {
        var o = ValidEnabled();
        o.HeartbeatInterval = TimeSpan.FromSeconds(-1);

        Assert.Contains(o.Validate(), e => e.Contains("HeartbeatInterval"));
    }

    [Fact]
    public void Validate_fails_when_heartbeat_interval_equals_idle_timeout()
    {
        var o = ValidEnabled();
        o.IdleTimeout = TimeSpan.FromSeconds(60);
        o.HeartbeatInterval = TimeSpan.FromSeconds(60);

        Assert.Contains(o.Validate(), e => e.Contains("HeartbeatInterval"));
    }

    [Fact]
    public void Validate_fails_when_heartbeat_interval_exceeds_idle_timeout()
    {
        var o = ValidEnabled();
        o.IdleTimeout = TimeSpan.FromSeconds(30);
        o.HeartbeatInterval = TimeSpan.FromSeconds(60);

        Assert.Contains(o.Validate(), e => e.Contains("HeartbeatInterval"));
    }

    [Fact]
    public void Validate_succeeds_when_heartbeat_interval_is_just_below_idle_timeout()
    {
        var o = ValidEnabled();
        o.IdleTimeout = TimeSpan.FromSeconds(60);
        o.HeartbeatInterval = TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1);

        Assert.Empty(o.Validate());
    }

    // ── ComputeDeadlines ──────────────────────────────────────────────────────

    [Fact]
    public void ComputeDeadlines_expires_at_admitted_plus_max_session_duration()
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var d = ValidEnabled().ComputeDeadlines(now);

        Assert.Equal(now + TimeSpan.FromMinutes(30), d.ExpiresAt);
    }

    [Fact]
    public void ComputeDeadlines_warns_at_admitted_plus_warning_threshold()
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var d = ValidEnabled().ComputeDeadlines(now);

        Assert.Equal(now + TimeSpan.FromMinutes(25), d.WarningAt);
    }

    [Fact]
    public void ComputeDeadlines_idle_expires_at_admitted_plus_idle_timeout()
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var d = ValidEnabled().ComputeDeadlines(now);

        Assert.Equal(now + TimeSpan.FromSeconds(60), d.IdleExpiresAt);
    }

    [Fact]
    public void ComputeDeadlines_uses_non_default_durations_from_options()
    {
        var o = ValidEnabled();
        o.MaxSessionDuration = TimeSpan.FromMinutes(45);
        o.SessionWarningThreshold = TimeSpan.FromMinutes(40);
        o.IdleTimeout = TimeSpan.FromSeconds(90);
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

        var d = o.ComputeDeadlines(now);

        Assert.Equal(now + TimeSpan.FromMinutes(45), d.ExpiresAt);
        Assert.Equal(now + TimeSpan.FromMinutes(40), d.WarningAt);
        Assert.Equal(now + TimeSpan.FromSeconds(90), d.IdleExpiresAt);
    }

    [Fact]
    public void ComputeDeadlines_warning_is_always_before_expiry()
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var d = ValidEnabled().ComputeDeadlines(now);

        Assert.True(d.WarningAt < d.ExpiresAt);
    }

    [Fact]
    public void ComputeDeadlines_uses_the_supplied_admitted_at_offset()
    {
        var now1 = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var now2 = now1.AddHours(1);
        var o = ValidEnabled();

        var d1 = o.ComputeDeadlines(now1);
        var d2 = o.ComputeDeadlines(now2);

        Assert.Equal(d1.ExpiresAt + TimeSpan.FromHours(1), d2.ExpiresAt);
        Assert.Equal(d1.WarningAt + TimeSpan.FromHours(1), d2.WarningAt);
        Assert.Equal(d1.IdleExpiresAt + TimeSpan.FromHours(1), d2.IdleExpiresAt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VoiceOptions ValidEnabled() => new()
    {
        Enabled = true,
        Endpoint = "wss://x.services.ai.azure.com/voice-live/realtime",
        Model = "gpt-4.1",
    };
}
