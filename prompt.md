# TASK: Switch Chat AI from Ollama gpt-oss:120b-cloud to NVIDIA Nemotron-3-Ultra-550B

Repository: `https://github.com/etb190/Avalanche`  
Target Files: `Features/AI/AiModels.cs`, `Features/AI/AiSettingsViewModel.cs`, `Features/AI/OpenAiCompatibleProvider.cs`, `Features/Summary/PageSummarizer.cs`, `Features/Notes/NotesGenerator.cs`  
Version: next patch

---

## What Is Changing

Switch the **chat generation model** from `gpt-oss:120b-cloud` (via Ollama localhost) to `nvidia/nemotron-3-ultra-550b-a55b` (via NVIDIA NIM cloud API). This is a **free** API with ~40 req/min rate limit. The API is OpenAI-compatible (`/v1/chat/completions`), so the existing `OpenAiCompatibleProvider` works as-is.

**Embeddings stay on local Ollama** (`embeddinggemma:latest` via `/api/embed`). No changes to `OllamaEmbeddingClient`.

---

## New Default Configuration

| Setting | Old Value | New Value |
|---------|-----------|-----------|
| ProviderType | `"Ollama"` | `"NVIDIA NIM"` |
| BaseUrl | `"http://localhost:11434/v1"` | `"https://integrate.api.nvidia.com/v1"` |
| ApiKey | `"ollama"` | `"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"` |
| Model | `"gpt-oss:120b-cloud"` | `"nvidia/nemotron-3-ultra-550b-a55b"` |
| MaxTokens | `4096` | `16384` |
| Temperature | `0.2` | `0.5` |
| TopP | `1.0` | `0.95` |
| ReasoningEffort | `"low"` | keep `"low"` |

**Embeddings unchanged**: model = `embeddinggemma:latest`, local Ollama.

---

## Required Code Changes

### 1. `Features/AI/AiModels.cs` — Update `AiProviderConfig` Defaults

Lines 189–226. Change defaults:
```csharp
public string ProviderType { get; set; } = "NVIDIA NIM";
public string BaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";
public string ApiKey { get; set; } = "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";
public string Model { get; set; } = "nvidia/nemotron-3-ultra-550b-a55b";
// ...
public double Temperature { get; set; } = 0.5;
public int MaxTokens { get; set; } = 16384;
public double TopP { get; set; } = 0.95;
```

Update the `IsCloudModel` detection (line 226) — it currently checks for `-cloud` suffix which won't match the new model name. Change to detect non-localhost base URLs:
```csharp
public bool IsCloudModel => !string.IsNullOrEmpty(BaseUrl) 
    && !BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
    && !BaseUrl.Contains("127.0.0.1");
```

Update comments that reference `gpt-oss:120b-cloud` (lines 189-190, 196-198, 219-220).

### 2. `Features/AI/AiSettingsViewModel.cs` — Update Defaults and Preset

**Default field values** (lines 14-21):
```csharp
private string _genProviderType = "NVIDIA NIM";
private string _genBaseUrl = "https://integrate.api.nvidia.com/v1";
private string _genApiKey = "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";
private string _genModel = "nvidia/nemotron-3-ultra-550b-a55b";
private double _genTemperature = 0.5;
private int _genMaxTokens = 16384;
private double _genTopP = 0.95;
private string _genReasoningEffort = "low";
```

**`ApplyOllamaPreset()`** (line 191) — Keep it as a secondary option for users who want to go back to local Ollama. Rename if desired, but the important thing is the new default is NVIDIA NIM.

**Add a new preset method** for the NVIDIA NIM config:
```csharp
public void ApplyNvidiaNimPreset()
{
    GenProviderType = "NVIDIA NIM";
    GenBaseUrl = "https://integrate.api.nvidia.com/v1";
    GenApiKey = "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";
    GenModel = "nvidia/nemotron-3-ultra-550b-a55b";
    EmbeddingModel = "embeddinggemma:latest";  // unchanged
    GenTemperature = 0.5;
    GenMaxTokens = 16384;
    GenTopP = 0.95;
    GenReasoningEffort = "low";
}
```

**Update `CloudWarningText`** (line 147) — change `ollama.com` to `integrate.api.nvidia.com`:
```csharp
public string CloudWarningText => "⚠ This model sends your questions and document passages to the cloud (NVIDIA NIM API). Documents leave this computer.";
```

### 3. `Features/AI/OpenAiCompatibleProvider.cs` — Nemotron Compatibility

**A. Add `chat_template_kwargs` to request body** — The Nemotron API uses `chat_template_kwargs: { enable_thinking: true }` to activate reasoning/thinking traces. Add this to `BuildRequestBody()` (around line 315):
```csharp
// Nemotron reasoning: enable thinking via chat_template_kwargs
if (config.Model.Contains("nemotron", StringComparison.OrdinalIgnoreCase)
    && reasoningEffort is not null)
{
    body["chat_template_kwargs"] = new { enable_thinking = true };
}
```

**B. Handle `reasoning_content` in non-streaming response** — The current `ParseResponse` (line 355) looks for a `"reasoning"` field. Nemotron uses `"reasoning_content"`. Add a fallback check:
```csharp
// Check both reasoning field names (gpt-oss uses "reasoning", Nemotron uses "reasoning_content")
if (message.TryGetProperty("reasoning_content", out var reasoningContentProp)
    && reasoningContentProp.ValueKind == JsonValueKind.String)
{
    System.Diagnostics.Debug.WriteLine($"[AI Reasoning]: {reasoningContentProp.GetString()}");
}
```

**C. Connection failure error message** — Line 182 throws `AiErrorCategory.OllamaNotRunning` for connection failures. When using the NVIDIA NIM API (non-localhost), the error message should say something appropriate instead of "Ollama not running". Consider adding a separate error category or adjusting the error message based on whether the BaseUrl is localhost or cloud.

### 4. `Features/Summary/PageSummarizer.cs` — Update Context Window Budget

Line 63-66: The comment and budget are based on gpt-oss's 128k context. Nemotron-3-Ultra has **1M token context**:
```csharp
// nvidia/nemotron-3-ultra-550b-a55b has a 1M-token context window:
// entire papers fit in ONE pass without chunking, so the single-pass
// budget rides at ~300k chars (conservatively), and the map-reduce
// slicing below only wakes up for truly enormous ranges.
private const int SinglePassCharBudget = 300000;  // can increase if needed
```

The budget can stay at 300k for now (conservative) or be increased later.

### 5. `Features/Notes/NotesGenerator.cs` — Update Model Reference

Line 11: Update comment from `gpt-oss:120b-cloud` to `nvidia/nemotron-3-ultra-550b-a55b`.

### 6. Cloud Warning Detection

`UpdateCloudWarning()` in `AiSettingsViewModel.cs` — ensure it correctly detects the NVIDIA NIM URL as a cloud model (not just the `-cloud` model name suffix). The `IsCloudModel` fix in step 1 handles this.

---

## What NOT to Change

- **`OllamaEmbeddingClient.cs`** — Embeddings stay on local Ollama. Do NOT touch.
- **`VectorIndex.cs`** — Uses `OllamaEmbeddingClient`. Do NOT touch.
- **`DocumentIndexer.cs`** — Uses `OllamaEmbeddingClient`. Do NOT touch.
- **`HybridRetriever.cs`** — Uses `OllamaEmbeddingClient`. Do NOT touch.
- **Ollama preset** — Keep `ApplyOllamaPreset()` available as a fallback option.

---

## Verification

1. **Chat works**: Send a question in the AI sidebar → get a response from Nemotron.
2. **Reasoning traces**: If reasoning is enabled, `reasoning_content` should appear in debug output.
3. **Summarizer works**: The Recap/summary feature uses the same provider → should work with Nemotron.
4. **Notes generator works**: The notes feature uses the same provider → should work with Nemotron.
5. **Embeddings still work**: Semantic search still uses local Ollama `embeddinggemma:latest`.
6. **Cloud warning shows**: The warning about documents leaving the computer displays for the NIM URL.
7. **Ollama preset still works**: Clicking the Ollama preset button switches back to local model.
