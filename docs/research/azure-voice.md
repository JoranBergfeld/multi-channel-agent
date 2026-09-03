# Azure Voice & Speech Options for a Tool-Calling Agent

**Research date:** 2026-08-28  
**Issue:** [#6 — Survey Azure options for speech and real-time voice](https://github.com/JoranBergfeld/multi-channel-agent/issues/6)  
**Scope:** Voice interaction for a multi-channel inventory agent reachable over a website and Microsoft Teams.

---

## 1. Landscape Overview

Azure offers five distinct voice-interaction paths, from simple STT+TTS chaining to fully managed speech-to-speech APIs:

| # | Option | GA / Preview | Best fit |
|---|--------|-------------|----------|
| 1 | **Azure AI Speech — STT + TTS (classic chain)** | GA | Max control, lowest cost at low traffic |
| 2 | **Azure OpenAI Realtime API** | GA (as of 2025) | Lowest-latency native speech-to-speech, browser WebRTC |
| 3 | **Azure AI Voice Live API** | GA (pricing effective July 2025) | Managed speech-to-speech with 600+ voices, noise suppression, echo cancellation |
| 4 | **Azure Communication Services (ACS) Call Automation** | GA | PSTN/VoIP telephony with AI audio streaming |
| 5 | **Teams Calling Bots (Graph API + Real-time Media Platform)** | GA | Low-level raw media access inside Teams calls |

---

## 2. Option 1 — Azure AI Speech: STT + TTS (Classic Chain)

### What it is
Separate speech-to-text and text-to-speech services that a developer chains manually around an LLM.

- **Speech-to-text:** Real-time transcription, fast transcription (pre-recorded), batch transcription. Custom speech models for domain vocabulary.
- **Text-to-speech:** 600+ neural voices across 150+ locales. Standard (24 kHz / 48 kHz HD) and custom neural voice (limited access, requires application).
- **Custom Neural Voice:** Brand-specific voices. Requires approval. Training and model hosting billed separately.

**Source:** [Azure AI Speech overview](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/overview) (updated 2026-06-05)

### Latency
End-to-end perceived latency = STT recognition time (~100–500 ms) + LLM first-token latency (~300 ms–2 s) + TTS streaming start (~100 ms). Total typically **1–3 s** depending on model size and network. No built-in pipeline optimisation.

### Barge-in / Interruption Support
None out of the box. The developer must implement voice activity detection (VAD) and cancel the TTS stream manually when the user starts speaking. This is complex to do correctly.

### Browser SDK Availability
- **Speech SDK:** Available for JavaScript/TypeScript (npm), .NET, Java, Python, C++, Swift, Go.  
- Works in the browser for STT and TTS.  
- Source: [Speech SDK](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-sdk)

### Cost Model
- **STT (real-time):** Billed per audio hour. Standard model is lower cost; custom model higher.
- **TTS (neural voice):** Billed per 1 million characters.
- **Custom voice training:** Per compute-second; model hosting billed monthly.
- Source: [Azure AI Speech pricing](https://azure.microsoft.com/en-us/pricing/details/speech/)

### Language Support
- **STT:** 140+ locales (BCP-47). Major world languages fully supported. See [language support table](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/language-support).
- **TTS:** 600+ voices across 150+ locales.

### Tool-Calling Composition
The developer is fully responsible for orchestration:
1. Stream audio → STT → text transcript
2. Call LLM (e.g. GPT-4.1) with transcript; receive tool call
3. Execute tool; inject result; get LLM text response
4. Convert text → TTS; stream audio back

**Complexity:** High. Interruption, VAD, low-latency streaming, and the STT/LLM/TTS handoffs all require custom plumbing. However, this gives the most control and decouples services cleanly.

---

## 3. Option 2 — Azure OpenAI Realtime API

### What it is
A native speech-to-speech API from Azure OpenAI (Microsoft Foundry). Audio goes in; audio comes back out. The model handles transcription, reasoning, and synthesis internally. Part of the GPT-4o family; also available as `gpt-realtime-2` (2026-05-07) and `gpt-realtime-1.5` (2026-02-23).

**Source:** [Azure OpenAI Realtime API how-to](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio) (updated 2026-07-31)

### Connection Methods

| Method | Latency | Use case |
|--------|---------|----------|
| **WebRTC** | ~100 ms | Browser/client apps — **recommended** |
| **WebSocket** | ~200 ms | Server-to-server |
| **SIP** | Varies | Telephony integration |

Source: [Realtime API connection methods](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio#connection-methods)

### Barge-in / Interruption Support
Fully supported natively. When the user starts speaking, the server detects the interruption and stops the current response audio. This is part of the Realtime API's built-in voice activity detection.

### Browser SDK Availability
**Yes.** Via WebRTC using standard browser Web APIs. No proprietary SDK needed for the browser-side. The developer fetches an ephemeral token from a backend service (using the REST endpoint `POST /openai/v1/realtime/client_secrets`) and then uses that token to establish a WebRTC peer connection directly in the browser.

Source: [Realtime API via WebRTC](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-webrtc) (updated 2026-07-31)

### Supported Models (as of 2026-08-28)
- `gpt-realtime-2` (2026-05-07) — latest
- `gpt-realtime-1.5` (2026-02-23)
- `gpt-realtime`, `gpt-realtime-mini`
- `gpt-4o-realtime-preview`, `gpt-4o-mini-realtime-preview`
- `gpt-realtime-translate` — real-time translation
- `gpt-realtime-whisper`, `gpt-live-transcribe` — transcription-focused

### Language Support
Primary model (`gpt-realtime-*`) is multilingual but English-first in quality. `gpt-realtime-translate` and `gpt-realtime-whisper` models explicitly target multilingual audio. Docs recommend passing an ISO-639-1 language hint to improve accuracy.

### Cost Model
Token-based. Audio input ≈ 10 tokens/second; audio output ≈ 20 tokens/second. Text tokens also charged for system prompt and function call payloads. Translation/transcription models billed per **duration** (see Audio Models section on [Azure OpenAI pricing](https://azure.microsoft.com/en-us/pricing/details/azure-openai/)). Specific per-token prices not published statically (dynamic pricing page); check the Azure portal.

### Tool-Calling Composition
**First-class support.** The Realtime API supports the standard OpenAI function-calling protocol:
1. Session is configured with tool definitions in `session.update`.
2. When the model decides to call a tool, it emits a `response.function_call_arguments.done` event.
3. The developer executes the tool and sends a `conversation.item.create` event with the tool result.
4. The model resumes speaking.

This happens mid-conversation with minimal interruption to the audio stream. The model is paused only for the duration of the tool execution. For fast tools (inventory lookups < 500 ms), this is nearly imperceptible.

Source: [Realtime API reference](https://learn.microsoft.com/en-us/azure/foundry/openai/realtime-audio-reference)

---

## 4. Option 3 — Azure AI Voice Live API

### What it is
A fully managed, end-to-end speech-to-speech API from **Azure AI Speech** (not Azure OpenAI). It wraps STT + any of a broad model menu (GPT-5, GPT-4.1, GPT-4o, Phi, azure-realtime, and more) + Azure TTS into a single WebSocket endpoint. It exposes the same event protocol as the Azure OpenAI Realtime API, so existing code is largely compatible.

> "The Voice Live API is designed for compatibility with the Azure OpenAI Realtime API. Features that are unique to the Voice Live API are optional and additive."

**Source:** [Voice Live API overview](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live) (updated 2026-08-26 — very current)

### Latency
"Low latency" — the docs position it as *lower perceived latency than the DIY STT+LLM+TTS chain* because STT and TTS run in the same backend pipeline, eliminating inter-service round trips. Numeric SLA is not published.

### Barge-in / Interruption Support
**Explicitly listed as a feature:** "Robust interruption detection: Ensures accurate recognition of interruptions during conversations." Also includes "Advanced end-of-turn detection: Allows natural pauses without prematurely concluding interactions."

Additional input processing: noise suppression (`azure_deep_noise_suppression`) and echo cancellation (`server_echo_cancellation`, including a Live-Reference AEC mode for client-side playback reference). These are unique to Voice Live and not available in the base Azure OpenAI Realtime API.

### Browser SDK Availability
Voice Live API is a **WebSocket endpoint** (server-to-server primary path). WebRTC integration for browser-facing apps is also documented (`voice-live-webrtc`). In practice: a lightweight backend WebSocket proxy handles the Voice Live session; the browser connects via WebRTC to the proxy. No proprietary browser SDK is needed.

Source: [Voice Live how-to](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to) (updated 2026-08-18)

### Supported Models
A wide menu (all fully managed, no deployment needed):

| Model | Notes |
|-------|-------|
| `gpt-realtime-1.5` | Native audio I/O or Azure STT/TTS voices |
| `gpt-realtime-mini` | Smaller/faster/cheaper realtime |
| `gpt-4.1`, `gpt-4.1-mini`, `gpt-4.1-nano` | Azure STT input + Azure TTS output |
| `gpt-5`, `gpt-5-mini`, `gpt-5-nano` | Azure STT input + Azure TTS output |
| `gpt-5.2`, `gpt-5.3-chat`, `gpt-5.4` | Same pattern |
| `phi4-mm-realtime` (**preview**) | Phi 4 multimodal |
| `phi4-mini` (**preview**) | Small, low-cost |
| `azure-realtime` | Azure's own realtime model with `azure-realtime-native` voices |

### Language Support
**140+ STT locales, 600+ TTS voices across 150+ locales.** Broader than Azure OpenAI Realtime API alone because Azure Speech STT supports more locales. Custom speech models and custom voices can be plugged in.

### Cost Model
**Tiered by model:**
- **Voice Live Pro:** `gpt-realtime*`, `gpt-4o`, `gpt-4.1`, `gpt-5`, `gpt-5-chat`
- **Voice Live Basic:** `gpt-realtime-mini`, `gpt-4o-mini`, `gpt-4.1-mini`, `gpt-5-mini`
- **Voice Live Lite:** `gpt-5-nano`, `phi4-mm-realtime`, `phi4-mini`

Token usage: ~10 input tokens/second audio, ~20 output tokens/second audio (Azure OpenAI models); ~12.5/~20 for Phi models. You are also charged for text tokens (system prompt, tool call payloads, conversation context).

Custom voice model hosting and custom speech training billed separately.

Source: [Voice Live pricing section](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live#pricing)

### Tool-Calling Composition
**First-class support.** The docs explicitly state: "Function calling: Enables external actions, use of tools, and grounded responses using the VoiceRAG pattern." The event protocol mirrors Azure OpenAI Realtime API. Tool execution follows the same pattern: model emits `function_call_arguments.done`, developer executes tool, sends result, model resumes.

---

## 5. Option 4 — Azure Communication Services (ACS) Call Automation

### What it is
A server-side telephony and VoIP orchestration API. It answers or places PSTN and VoIP calls, plays audio, recognises DTMF or voice, and exposes bidirectional audio streaming over WebSocket so your server can pipe the audio to any AI service.

**Source:** [Call Automation overview](https://learn.microsoft.com/en-us/azure/communication-services/concepts/call-automation/call-automation) (updated 2026-06-02)

### Typical Architecture for AI Voice Agent
```
PSTN / VoIP caller
       ↓
  ACS Call Automation  ←→  WebSocket audio stream
       ↓
  Your middleware server
       ↓
  Voice Live API or Azure OpenAI Realtime API
       ↓
  Tool calls → Inventory agent
```

Bidirectional audio: 16-bit PCM mono at 16,000 Hz or 24,000 Hz, 50 frames/second, 20 ms per frame.

Source: [Audio streaming concept](https://learn.microsoft.com/en-us/azure/communication-services/concepts/call-automation/audio-streaming-concept) (updated 2026-06-12)

### Latency
ACS introduces an extra WebSocket hop to your server and back. End-to-end latency depends on the AI service you connect. With Voice Live API on the other side, expect +100–200 ms over direct WebRTC. The audio format overhead is minimal.

### Barge-in / Interruption Support
ACS's `Play` action can be stopped (`CancelMediaOperations`) when your server detects the user speaking (via the audio stream). Interruption detection at the AI level (Voice Live API or Realtime API) will signal the tool layer; your server must then call `CancelMediaOperations` on the ACS side. **Not seamless** — requires custom coordination logic.

### Browser SDK Availability
**Yes.** `@azure/communication-calling` on npm for JavaScript. Also .NET, Android, iOS.  
Source: [Calling SDK overview](https://learn.microsoft.com/en-us/azure/communication-services/concepts/voice-video-calling/calling-sdk-features) (updated 2026-03-25)

### PSTN Support
Full PSTN calling with Azure-acquired numbers or bring-your-own via SBCs (Direct Routing). Supports inbound and outbound calls.

### Language Support
STT/TTS language support is inherited from whichever AI service is connected (Voice Live API gives 140+ STT locales). ACS itself is infrastructure and is language-agnostic.

### Cost Model
Per-minute calling charges + phone number lease fees + audio streaming fees (billed in calling category). Specific rates must be checked at the [ACS pricing page](https://azure.microsoft.com/en-us/pricing/details/communication-services/) as they vary by region and call type.

### Tool-Calling Composition
Tool calls happen in your middleware server as part of the WebSocket/AI pipeline. ACS itself has no awareness of LLM tool calls — it is a pure telephony layer. Your server handles the full orchestration.

---

## 6. Option 5 — Teams Calling Bots (Graph API + Real-time Media Platform)

### What it is
Bots registered in Teams that can participate in Teams calls and meetings. Two sub-types:

- **Service-hosted media bots:** Microsoft handles audio encoding/decoding. Bot sends play commands (WAV, TTS, SSML) and receives DTMF/voice recognition events. Simpler, no Windows Server required.
- **Application-hosted media bots:** Bot receives raw 16-bit PCM audio frames (50 fps). Requires `Microsoft.Graph.Communications.Calls.Media` .NET library and **must be deployed on Windows Server** (VM or Azure Windows guest OS).

**Source:** [Calls and meetings bots overview](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/calls-meetings-bots-overview) (updated 2026-05-29)

### ⚠️ Microsoft's Own Guidance

> **"Building AI agents for meetings? Real-time Media bots are not recommended for AI agent scenarios. Instead, use: Microsoft Copilot Studio agents for building agents that participate in Teams meetings."**

Source: [Real-time media concepts](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/real-time-media-concepts) (updated 2026-05-29)

Real-time Media bots are now explicitly scoped to specialised scenarios: Cloud Video Interop (CVI), compliance recording, and contact centre integration.

### Latency
Service-hosted: depends on the play/recognize cycle. Application-hosted: raw frame access but Windows Server deployment adds operational overhead. Neither provides the tight latency loop of Voice Live API + WebRTC.

### Barge-in / Interruption Support
**Service-hosted:** None built-in. Must stop current play action and restart recognition.  
**Application-hosted:** Raw audio access allows custom VAD, but the bot must handle all media processing. Very complex.

### Browser SDK Availability
Not applicable to bots. Teams Calling SDK (`@azure/communication-calling`) can be used in browser apps that join Teams meetings as ACS users. A **Teams calling bot** runs as a server-side application registered via Graph API.

### Language Support
Service-hosted media bots use Azure Speech internally (Microsoft manages it) — limited locale exposure. Application-hosted bots can pipe to any speech service and theoretically support all Azure Speech locales.

### Cost Model
Graph API calling is included in the Microsoft 365 licensing model. Application-hosted media bots require dedicated Windows VMs on Azure (additional compute cost). PSTN dial-out from Teams has per-minute rates.

### Tool-Calling Composition
Application-hosted: all orchestration is custom in the .NET bot code. Service-hosted: the bot reacts to DTMF/voice recognition events and plays responses; tool calls happen in between those events. Neither is as seamless as Voice Live API's integrated function-calling protocol.

---

## 7. Comparison Matrix

| Dimension | STT+TTS Chain | AOAI Realtime API | Voice Live API | ACS Call Automation | Teams Graph Bot |
|-----------|--------------|-------------------|----------------|---------------------|-----------------|
| **Latency** | ~1–3 s | ~100 ms (WebRTC) | Lower than chain (no numeric SLA) | Chain + network hop | Variable |
| **Barge-in** | Manual (custom VAD) | ✅ Native | ✅ Native + noise/echo | Manual (cancel media) | ❌ Service-hosted; complex in app-hosted |
| **Browser SDK** | ✅ Speech SDK JS | ✅ WebRTC native | ✅ via WebRTC proxy | ✅ `@azure/communication-calling` | N/A (server bot) |
| **Tool calling** | Custom orchestration | ✅ Native | ✅ Native | Custom in middleware | Custom in bot code |
| **Language support** | 140+ STT locales / 150+ TTS | Multilingual models available | 140+ STT / 150+ TTS | Depends on AI service | Limited (service-hosted) |
| **Custom voice** | ✅ (limited access) | ❌ (uses model's voice) | ✅ (limited access) | Via connected AI service | N/A |
| **PSTN** | Via ACS or 3rd party | No (audio API only) | No (audio API only) | ✅ Native | ✅ (Teams PSTN) |
| **Teams native** | Via ACS interop | No | No | ✅ (Teams interop available) | ✅ Native |
| **Operational complexity** | Medium | Low–Medium | Low (managed) | Medium–High | High (Windows Server) |
| **Status** | GA | GA | GA | GA | GA (not recommended for AI agents) |

---

## 8. How Tool Calling Works Mid-Conversation (Detail)

For Azure OpenAI Realtime API and Voice Live API, the protocol is identical (Voice Live API is compatible with the Realtime API event schema):

1. **`session.update`** — include `tools` array with function definitions (same JSON schema as chat completions).
2. User speaks → model transcribes → model reasons → model emits `response.function_call_arguments.delta` (streaming) and then `response.function_call_arguments.done`.
3. At `done`, your server reads the function name and arguments, executes the tool (e.g. queries the inventory database), and sends a **`conversation.item.create`** event with `type: "function_call_output"` and the result.
4. Optionally send a **`response.create`** event to prompt the model to continue speaking.
5. Model resumes speaking the result to the user.

During step 3 (tool execution), the model is silent. If the user speaks during this pause, the interruption will be picked up on resume. Keeping tool latency below ~500 ms avoids awkward silence.

Source: [Azure OpenAI Realtime API reference](https://learn.microsoft.com/en-us/azure/foundry/openai/realtime-audio-reference)

---

## 9. Recommendations

### (a) Voice on the Website

**Recommended: Azure AI Voice Live API over WebRTC**

**Reasoning:**
1. Voice Live API is the most fully managed option. It eliminates the three-service orchestration (STT + LLM + TTS) that the classic chain requires.
2. It has native barge-in/interruption detection, noise suppression, and echo cancellation — all critical for a pleasant in-browser voice experience.
3. It supports **140+ STT locales and 600+ TTS voices**, future-proofing the inventory agent for multilingual users.
4. Function calling is a first-class, well-documented feature — the inventory agent can call mutation/query tools mid-utterance.
5. The WebRTC path (documented at `voice-live-webrtc`) gives ~100 ms round-trip latency, indistinguishable from a live phone call for most users.
6. The model menu (GPT-5, GPT-4.1, GPT-4o, Phi) decouples intelligence from the voice pipeline — you can upgrade the model without changing the audio architecture.
7. Azure Speech custom neural voice can be added later if brand-voice is a requirement (subject to limited-access approval).

**Architecture:**
```
Browser (WebRTC)
  ↔ Lightweight token-proxy server (Node.js/Python)
  ↔ Voice Live API WebSocket (wss://<resource>.services.ai.azure.com/voice-live/realtime)
        Model: gpt-4.1 or gpt-realtime-1.5
        Tools: inventory read/write functions
```

**Alternative considered: Azure OpenAI Realtime API directly**  
Nearly equivalent. The Realtime API is slightly simpler (no Azure Speech resource needed) and reaches the model more directly. Choose it if you don't need the extra STT locale breadth, custom Azure TTS voices, or the server-side noise suppression / echo cancellation. For a website where users likely have decent microphones, the Realtime API is a valid and slightly simpler alternative.

**What would overturn this recommendation:**
- If the inventory agent must support obscure languages not yet in the Azure OpenAI Realtime API but in Azure Speech STT → Voice Live API is then clearly superior.
- If budget is very tight and usage is low, the classic STT+LLM+TTS chain costs less per minute (no voice pipeline markup) but sacrifices latency and barge-in quality.
- If custom neural voice is a hard requirement on day one, both options support it (limited access), but the classic chain may be faster to approve since it's a separate TTS call.

---

### (b) Voice on Teams

**Recommended: Azure Communication Services Call Automation + Voice Live API, bridged into Teams via ACS–Teams Interop**

**Reasoning:**
1. Microsoft explicitly discourages Graph API / Real-time Media bots for AI agent scenarios: "Real-time Media bots are not recommended for AI agent scenarios. Instead, use Microsoft Copilot Studio agents."  
2. Real-time Media bots require Windows Server VMs and complex .NET media processing — high operational overhead for what is essentially an inventory chatbot.
3. ACS Call Automation provides a clean telephony abstraction. The ACS Calling SDK can join Teams meetings as an ACS identity, giving the bot a presence in a Teams call without the Graph bot complexity.
4. Audio is streamed bidirectionally over WebSocket from ACS to a middleware server, which then pipes to Voice Live API. This reuses the same Voice Live API session architecture as the website path — one AI backend serves both channels.
5. Tool calling is identical regardless of whether the audio path goes through browser WebRTC or ACS WebSocket.

**Architecture:**
```
Teams user (in a Teams meeting or direct call)
  ↔ Teams ↔ ACS interop
  ↔ ACS Call Automation (bidirectional audio WebSocket)
  ↔ Your middleware server
  ↔ Voice Live API (same session config as website)
        Tools: inventory read/write functions
```

For Teams *bots that join meetings proactively* (e.g., a scheduled meeting the agent hosts), the bot registers with ACS, gets a meeting join URL, and calls `ACS.joinTeamsMeeting`. For *responding to Teams users who message the agent*, the standard Bot Framework / Azure Bot Service text channel is the more appropriate path (not voice).

**Alternative considered: Microsoft Copilot Studio**  
Copilot Studio is Microsoft's official recommendation for AI agents in Teams. It provides voice integration, Teams channel deployment, and pre-built tool-calling connectors. It is the right choice if:
- The team wants a low-code/no-code agent management experience.
- The inventory backend can be exposed as a Power Platform connector or an HTTP action.

However, Copilot Studio has less control over the voice pipeline (you cannot choose the LLM model, custom voice, or STT locale granularity easily). For a fully custom agent with specific latency, model, and voice requirements, ACS + Voice Live API gives more control.

**What would overturn this recommendation:**
- If deep Teams meeting integration (roster awareness, meeting transcript, Teams-native UX) matters → investigate Copilot Studio first.
- If PSTN calling (phone number) is a requirement for Teams → ACS PSTN calling is the right path; the architecture above still applies.
- If the team is a small team with no DevOps for a middleware server → Copilot Studio is operationally simpler.

---

## 10. Sources

| Resource | URL | Last updated |
|----------|-----|-------------|
| Azure AI Speech overview | https://learn.microsoft.com/en-us/azure/ai-services/speech-service/overview | 2026-06-05 |
| Azure AI Speech language support | https://learn.microsoft.com/en-us/azure/ai-services/speech-service/language-support | 2026-08-13 |
| Azure AI Speech pricing | https://azure.microsoft.com/en-us/pricing/details/speech/ | Dynamic |
| Text to speech overview | https://learn.microsoft.com/en-us/azure/ai-services/speech-service/text-to-speech | 2026-08-18 |
| Voice Live API overview | https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live | 2026-08-26 |
| Voice Live API how-to | https://learn.microsoft.com/en-us/azure/ai-services/speech-service/voice-live-how-to | 2026-08-18 |
| Azure OpenAI Realtime API how-to | https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio | 2026-07-31 |
| Azure OpenAI Realtime API via WebRTC | https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-webrtc | 2026-07-31 |
| Azure OpenAI Realtime API reference | https://learn.microsoft.com/en-us/azure/foundry/openai/realtime-audio-reference | 2026-06-05 |
| Azure OpenAI pricing | https://azure.microsoft.com/en-us/pricing/details/azure-openai/ | Dynamic |
| ACS Calling SDK overview | https://learn.microsoft.com/en-us/azure/communication-services/concepts/voice-video-calling/calling-sdk-features | 2026-03-25 |
| ACS Call Automation overview | https://learn.microsoft.com/en-us/azure/communication-services/concepts/call-automation/call-automation | 2026-06-02 |
| ACS Audio Streaming concept | https://learn.microsoft.com/en-us/azure/communication-services/concepts/call-automation/audio-streaming-concept | 2026-06-12 |
| ACS Teams user calling capabilities | https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/teams-user-calling | 2026-03-25 |
| ACS pricing | https://azure.microsoft.com/en-us/pricing/details/communication-services/ | Dynamic |
| Teams calls/meetings bots overview | https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/calls-meetings-bots-overview | 2026-05-29 |
| Teams real-time media concepts | https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/real-time-media-concepts | 2026-05-29 |
| Teams calling bot registration | https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/registering-calling-bot | 2026-08-03 |

---

## 11. Caveats and Things to Verify

1. **Voice Live API numeric latency SLA** is not published. The "low latency" claim is qualitative. Run a benchmark before committing.
2. **Azure OpenAI Realtime API token pricing** was not available as a static value on the pricing page (dynamic). Check the Azure portal or contact sales.
3. **Custom Neural Voice** requires a [limited-access application](https://aka.ms/customneural) and approval. Do not assume it is available on-demand.
4. **Phi models in Voice Live API** (`phi4-mm-realtime`, `phi4-mini`) are still in **preview** as of 2026-08-28. Do not use for production.
5. **ACS + Teams interop** supports ACS identities joining Teams meetings, but feature parity with Teams-native users is not complete (see [Teams user calling capabilities](https://learn.microsoft.com/en-us/azure/communication-services/concepts/interop/teams-user-calling)).
6. **Real-time Media bots (application-hosted)** require Windows Server. This is a hard constraint — Linux containers are not supported for the media library.
7. Token pricing numbers for Voice Live API: the token-per-second table is from the docs (10 input / 20 output for Azure OpenAI models). Multiply by session duration to estimate cost before the session billable rate is published per tier.
