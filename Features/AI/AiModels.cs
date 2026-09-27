using System.Collections.Generic;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Represents a chunk of text extracted from a PDF page with coordinate information.
    /// </summary>
    internal sealed class DocumentChunk
    {
        public string DocumentId { get; init; } = "";
        public int PageIndex { get; init; }          // 0-based
        public int PageNumber { get; init; }         // 1-based (displayed)
        public string ChunkId { get; init; } = "";
        public string Text { get; init; } = "";
        public double Left { get; init; }
        public double Bottom { get; init; }
        public double Right { get; init; }
        public double Top { get; init; }
        public int StartWordIndex { get; init; }
        public int EndWordIndex { get; init; }
    }

    /// <summary>
    /// Index of a document for AI retrieval.
    /// </summary>
    internal sealed class DocumentIndex
    {
        public string DocumentId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public long FileSize { get; set; }
        public long LastWriteTime { get; set; }
        public int PageCount { get; set; }
        public List<DocumentChunk> Chunks { get; set; } = [];
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A retrieved chunk with relevance score.
    /// </summary>
    internal sealed class RetrievedChunk
    {
        public DocumentChunk Chunk { get; set; } = new();
        public double Score { get; set; }
    }

    /// <summary>
    /// Structured source citation from AI response.
    /// </summary>
    internal sealed class AiSource
    {
        public int PageNumber { get; set; }
        public string Quote { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Structured AI response with answer and sources.
    /// </summary>
    internal sealed class AiResponse
    {
        public string Answer { get; set; } = "";
        public List<AiSource> Sources { get; set; } = [];
    }

    /// <summary>
    /// Chat message in the conversation.
    /// </summary>
    internal sealed class ChatMessage
    {
        public enum Role { User, Assistant, System }
        public Role MessageRole { get; set; }
        public string Content { get; set; } = "";
        public List<AiSource> Sources { get; set; } = [];
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public bool IsLoading { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Configuration for AI provider.
    /// </summary>
    internal sealed class AiProviderConfig
    {
        public string ProviderType { get; init; } = "OpenAICompatible";
        public string BaseUrl { get; init; } = "https://api.openai.com/v1";
        public string ApiKey { get; init; } = "";
        public string Model { get; init; } = "gpt-4o-mini";
        public double Temperature { get; init; } = 0.1;
        public int MaxTokens { get; init; } = 2000;
    }
}