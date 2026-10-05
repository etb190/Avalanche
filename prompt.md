# TASK: Fix Summarizer HTTP 500 on NVIDIA NIM API

Repository: `https://github.com/etb190/Avalanche`  
Target Files: `Features/Summary/PageSummarizer.cs`, `Features/Notes/NotesGenerator.cs`  
Version: next patch

---

## The Problem

The **sidebar chat works** with the NVIDIA NIM API but the **summarizer window fails with HTTP 500** after ~5 minutes. The sidebar chat is a bit slower than the old model but functional.

## Root Cause Analysis

### 1. Missing `chat_template_kwargs` in Summarizer Requests
The summarizer has its own HTTP client and `BuildRequest()` method (line 1228 of `PageSummarizer.cs`) that is **completely separate** from `OpenAiCompatibleProvider.BuildRequestBody()`. The provider was updated in v1.19.17 to include:
```csharp
body["chat_template_kwargs"] = new { enable_thinking = true };
```
But the summarizer's `BuildRequest()` was NOT updated. It sends requests without `chat_template_kwargs`, which means the Nemotron API receives a request for a model that expects thinking to be configured but gets no configuration. This could cause the 500 error on the server side.

**Fix**: Add `chat_template_kwargs` to `BuildRequest()` in `PageSummarizer.cs` when the model is Nemotron:
```csharp
private static HttpRequestMessage BuildRequest(AiProviderConfig config, string system, string user, int maxTokens, bool stream, double? temperature = null)
{
    var body = new Dictionary<string, object?>
    {
        ["model"] = config.Model,
        ["messages"] = new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user }
        },
        ["temperature"] = temperature ?? config.Temperature,
        ["max_tokens"] = maxTokens,
        ["stream"] = stream
    };
    
    // Nemotron reasoning: enable thinking via chat_template_kwargs
    if (config.Model.Contains("nemotron", StringComparison.OrdinalIgnoreCase))
    {
        body["chat_template_kwargs"] = new { enable_thinking = true };
    }
    
    // ... rest unchanged
}
```

### 2. Payload Too Large for Free Tier Serverless Endpoint
The summarizer sends **much larger payloads** than the sidebar chat:
- Sidebar chat: ~10-40k chars of context → ~2.5-10k tokens input
- Summarizer: up to **300,000 chars** of extracted PDF text → **~75,000 tokens** input
- Plus `max_tokens` output budget of **10,000–21,000** tokens
- Total: ~75k-96k tokens per request

The NVIDIA NIM free tier is a **serverless, rate-limited endpoint meant for prototyping**. A 75k+ token request takes significant GPU time and may:
- Hit server-side timeouts (the server kills requests that run too long → HTTP 500)
- Exceed undocumented per-request token limits on the free tier

**Fix options (implement ALL):**

**A. Reduce `SinglePassCharBudget` for cloud endpoints.**
The 300k char budget was set for the 1M-token context window, but the free tier may not handle requests that large. Reduce to ~100k chars (~25k tokens) for cloud endpoints:
```csharp
// Use a smaller budget for cloud endpoints to avoid server-side timeouts
int effectiveBudget = AiEndpoints.IsLocal(config.BaseUrl) 
    ? SinglePassCharBudget    // 300k for local (no timeout risk)
    : 100000;                 // 100k for cloud (free tier limit)
```

**B. Lower `max_tokens` for summarizer requests.**
The budget calculation at line 1280 can push `max_tokens` to 21,000+. For a summarizer that produces ~500-4500 words, 8192 tokens is more than enough:
```csharp
int budget = Math.Min(8192, Math.Max(config.MaxTokens, Math.Max(10000, 3000 + (4 * targetWords))));
```
Or cap it:
```csharp
int budget = Math.Max(config.MaxTokens, Math.Max(10000, 3000 + (4 * targetWords)));
if (!AiEndpoints.IsLocal(config.BaseUrl)) budget = Math.Min(budget, 8192);
```

**C. Disable thinking/reasoning for summarizer requests.**
Thinking burns tokens from `max_tokens` AND dramatically increases processing time. The summarizer doesn't need reasoning traces — it just needs a prose digest. Don't send `chat_template_kwargs` for summarizer requests, or explicitly disable thinking:
```csharp
// Summarizer: disable thinking to save time and tokens
if (config.Model.Contains("nemotron", StringComparison.OrdinalIgnoreCase))
{
    body["chat_template_kwargs"] = new { enable_thinking = false };
}
```
This is probably the **single most impactful fix** — with thinking disabled:
- The model generates directly instead of thinking first → much faster
- All of `max_tokens` goes to the actual answer → no wasted budget
- Server processes the request faster → less likely to timeout

### 3. Same Fix Needed in `NotesGenerator.cs`
The notes generator (`Features/Notes/NotesGenerator.cs`) likely has its own HTTP request builder too. Check if it also needs the same fixes.

---

## Summary of Fixes

| Fix | Impact | Risk |
|-----|--------|------|
| **Disable thinking** for summarizer (`enable_thinking = false`) | Huge — eliminates the main cause of 5-min waits and timeouts | Zero — summarizer never uses reasoning traces |
| **Add `chat_template_kwargs`** to `BuildRequest()` | Fixes potential 500 from missing required field | Zero |
| **Cap `max_tokens` at 8192** for cloud endpoints | Prevents server-side timeout on large requests | Minimal — 8192 tokens ≈ 6000+ words, way more than any summary needs |
| **Reduce char budget** to ~100k for cloud | Prevents oversized requests | Minimal — 100k chars ≈ 40 pages, enough for most papers |

---

## Verification

1. Open a PDF → click Recap/Summary → should complete without HTTP 500
2. Summary quality should be at least as good as before (Nemotron is a better model)
3. Summary should complete in ~30-60 seconds, not 5 minutes
4. Sidebar chat should continue working as before
5. Notes generation should also work
