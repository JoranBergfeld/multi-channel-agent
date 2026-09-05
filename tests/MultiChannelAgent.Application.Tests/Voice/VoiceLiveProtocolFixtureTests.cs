using System.Text.Json;

namespace MultiChannelAgent.Application.Tests.Voice;

// Sources retrieved 2026-09-05:
// https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-webrtc
// https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-api-reference-2026-04-10

public sealed class VoiceLiveProtocolFixtureTests
{
    private const string FixturesPath = "Voice/Fixtures/voice-live-2026-04-10";

    private static async Task<JsonDocument> LoadFixtureAsync(string fileName)
    {
        var path = Path.Combine(FixturesPath, fileName);
        var json = await File.ReadAllTextAsync(path);
        return JsonDocument.Parse(json);
    }

    [Fact]
    public async Task Sdp_create_has_type_sdp_offer_and_session_modalities()
    {
        using var document = await LoadFixtureAsync("sdp-create.json");
        var root = document.RootElement;

        Assert.Equal("rtc.call.sdp.create", root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("sdp_offer").GetString()));
        Assert.True(root.GetProperty("session").TryGetProperty("modalities", out _));
    }

    [Fact]
    public async Task Sdp_created_has_type_and_non_empty_sdp_answer()
    {
        using var document = await LoadFixtureAsync("sdp-created.json");
        var root = document.RootElement;

        Assert.Equal("rtc.call.sdp.created", root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("sdp_answer").GetString()));
    }

    [Fact]
    public async Task Session_update_has_modalities_voice_transcription_turn_detection_and_no_tools()
    {
        using var document = await LoadFixtureAsync("session-update.json");
        var root = document.RootElement;

        Assert.Equal("session.update", root.GetProperty("type").GetString());
        var session = root.GetProperty("session");
        Assert.True(session.TryGetProperty("modalities", out _));
        Assert.True(session.TryGetProperty("voice", out _));
        Assert.True(session.TryGetProperty("input_audio_transcription", out _));
        Assert.True(session.TryGetProperty("turn_detection", out _));
        Assert.False(session.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Response_cancel_has_correct_type()
    {
        using var document = await LoadFixtureAsync("response-cancel.json");

        Assert.Equal("response.cancel", document.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Input_audio_transcription_completed_has_type_item_id_content_index_and_transcript()
    {
        using var document = await LoadFixtureAsync("input-audio-transcription-completed.json");
        var root = document.RootElement;

        Assert.Equal(
            "conversation.item.input_audio_transcription.completed",
            root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("item_id", out _));
        Assert.True(root.TryGetProperty("content_index", out _));
        Assert.True(root.TryGetProperty("transcript", out _));
    }

    [Fact]
    public async Task Conversation_item_truncate_has_item_id_content_index_and_audio_end_ms()
    {
        using var document = await LoadFixtureAsync("conversation-item-truncate.json");
        var root = document.RootElement;

        Assert.Equal("conversation.item.truncate", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("item_id", out _));
        Assert.True(root.TryGetProperty("content_index", out _));
        Assert.True(root.TryGetProperty("audio_end_ms", out _));
    }

    [Fact]
    public async Task Response_create_canonical_has_assistant_pre_generated_message_with_single_text_entry()
    {
        using var document = await LoadFixtureAsync("response-create-canonical.json");
        var root = document.RootElement;

        Assert.Equal("response.create", root.GetProperty("type").GetString());
        var preGenerated = root.GetProperty("response").GetProperty("pre_generated_assistant_message");
        Assert.Equal("assistant", preGenerated.GetProperty("role").GetString());
        var content = preGenerated.GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        var entry = content[0];
        Assert.Equal("text", entry.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Response_audio_transcript_done_has_required_fields()
    {
        using var document = await LoadFixtureAsync("response-audio-transcript-done.json");
        var root = document.RootElement;

        Assert.Equal("response.audio_transcript.done", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("response_id", out _));
        Assert.True(root.TryGetProperty("item_id", out _));
        Assert.True(root.TryGetProperty("output_index", out _));
        Assert.True(root.TryGetProperty("content_index", out _));
        Assert.True(root.TryGetProperty("transcript", out _));
    }

    [Fact]
    public async Task Canonical_response_text_exactly_matches_audio_transcript_done_transcript()
    {
        using var createDoc = await LoadFixtureAsync("response-create-canonical.json");
        using var doneDoc = await LoadFixtureAsync("response-audio-transcript-done.json");

        var canonicalText = createDoc.RootElement
            .GetProperty("response")
            .GetProperty("pre_generated_assistant_message")
            .GetProperty("content")[0]
            .GetProperty("text").GetString();

        var transcript = doneDoc.RootElement.GetProperty("transcript").GetString();

        Assert.Equal(canonicalText, transcript);
    }
}
