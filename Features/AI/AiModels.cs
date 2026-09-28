using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Represents a chunk of text extracted from a PDF page with rich coordinate information.
    /// </summary>
    public sealed class DocumentChunk
    {
        public string ChunkId { get; set; } = "";
        public string DocumentId { get; set; } = "";
        public int ChunkIndex { get; set; }
        public string Text { get; set; } = "";
        
        // Page-level mapping (chunk can span multiple pages)
        public List<int> PageIndices { get; set; } = new();           // 0-based page indices
        public List<int[]> WordRanges { get; set; } = new();          // [startWord, endWord] per page
        public List<float[]> PdfCoordinates { get; set; } = new();    // [left, bottom, right, top] per page in PDF space
        
        // Character offset in the full document text
        public long CharOffset { get; set; }
        
        // Embedding vector
        public float[]? Embedding { get; set; }
        
        // Lexical search tokens
        public List<string> LexicalTokens { get; set; } = new();
        
        // Section/heading context if available
        public string? SectionHeading { get; set; }
        
        // Primary page (first page this chunk appears on) for backward compatibility
        [JsonIgnore]
        public int PageIndex => PageIndices.Count > 0 ? PageIndices[0] : -1;
        
        [JsonIgnore]
        public int PageNumber => PageIndex + 1;
    }

    /// <summary>
    /// Persistent document index with content hash for change detection.
    /// </summary>
    public sealed class DocumentIndex
    {
        public string DocumentId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public long FileSize { get; set; }
        public long LastWriteTime { get; set; }
        public string ContentHash { get; set; } = "";  // SHA256 of file content
        public int PageCount { get; set; }
        public List<DocumentChunk> Chunks { get; set; } = new();
        public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Computed properties
        public int ChunkCount => Chunks.Count;
    }

    /// <summary>
    /// A retrieved chunk with relevance score.
    /// </summary>
    public sealed class RetrievedChunk
    {
        public DocumentChunk Chunk { get; set; } = new();
        public float Score { get; set; }
        public RetrievalMethod Method { get; set; } = RetrievalMethod.Hybrid;
    }

    public enum RetrievalMethod
    {
        Lexical,
        Semantic,
        Hybrid,
        Reranked
    }

    /// <summary>
    /// Structured source citation from AI response.
    /// </summary>
    public sealed class AiSource
    {
        public string SourceId { get; set; } = "";      // Chunk ID from retrieval
        public int PageNumber { get; set; }
        public int PageIndex { get; set; }
        public string Quote { get; set; } = "";
        public string Reason { get; set; } = "";
        public float[]? PdfCoordinates { get; set; }    // [left, bottom, right, top] in PDF space
        public int[]? WordRange { get; set; }           // [start, end] word indices
    }

    /// <summary>
    /// Structured AI response with answer and sources.
    /// </summary>
    public sealed class AiResponse
    {
        public string Answer { get; set; } = "";
        public List<AiSource> Sources { get; set; } = new();
    }

    /// <summary>
    /// Chat message in the conversation.
    /// </summary>
    public sealed class ChatMessage
    {
        public enum Role { User, Assistant, System }
        public Role MessageRole { get; set; }
        public string Content { get; set; } = "";
        public List<AiSource> Sources { get; set; } = new();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public bool IsLoading { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Configuration for AI provider.
    /// </summary>
    public sealed class AiProviderConfig
    {
        public string ProviderType { get; set; } = "OpenAICompatible";
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "gpt-4o-mini";
        public double Temperature { get; set; } = 0.1;
        public int MaxTokens { get; set; } = 2000;
    }
}