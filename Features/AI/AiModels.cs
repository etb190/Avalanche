using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
        public List<int[]> WordRanges { get; set; } = new();          // [startWord, endWord] per page,
                                                                      // CHUNK-relative (word indices
                                                                      // inside this chunk's Text)
        public List<float[]> PdfCoordinates { get; set; } = new();    // [left, bottom, right, top] per page in PDF space
        
        // Character offset in the full document text
        public long CharOffset { get; set; }
        
        // Lexical search tokens
        public List<string> LexicalTokens { get; set; } = new();
        
        // Section/heading context if available
        public string? SectionHeading { get; set; }
        
        // Page size/rotation for coordinate conversion (cached from PDF)
        public float PageWidth { get; set; }
        public float PageHeight { get; set; }
        public int PageRotation { get; set; }
        public float[]? CropBox { get; set; } // [left, bottom, right, top]

        // Per-page geometry aligned with PageIndices. A chunk's pages can differ
        // in size and rotation; the single-page legacy fields above only described
        // the first page and broke coordinate conversion on every other page.
        public List<float[]> PageSizes { get; set; } = new();         // [width, height] per page
        public List<int> PageRotations { get; set; } = new();         // rotation degrees per page
        public List<float[]> CropBoxes { get; set; } = new();         // [left, bottom, right, top] per page
        
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
        public RetrievalMethod Method { get; set; } = RetrievalMethod.Lexical;
    }

    public enum RetrievalMethod
    {
        Lexical,
        Semantic,
        Hybrid,
        Reranked
    }

    /// <summary>Where the model's quote was located inside the cited chunk.
    /// Drives highlight behavior: Exact/Approximate highlight the located
    /// range, Unlocated shows the "could not locate" status.</summary>
    public enum AiQuoteLocation
    {
        Exact,
        Approximate,
        Unlocated
    }

    /// <summary>
    /// Structured source citation from AI response.
    /// </summary>
    public sealed class AiSource
    {
        public string SourceId { get; set; } = "";      // SOURCE_1, SOURCE_2, etc. (not ChunkId)
        public int PageNumber { get; set; }
        public int PageIndex { get; set; }
        public string Quote { get; set; } = "";
        public string Reason { get; set; } = "";
        public float[]? PdfCoordinates { get; set; }    // [left, bottom, right, top] in PDF space
        public int[]? WordRange { get; set; }           // [start, end] word indices
        
        // Resolved chunk for exact navigation
        public DocumentChunk? ResolvedChunk { get; set; }

        // True when the model's quote was verified against the cited chunk's
        // text (relaxed letters/digits compare). Unverified quotes are never
        // used as highlight needles - they would paint unrelated passages.
        public bool QuoteVerified { get; set; }

        /// <summary>Where the quote was found inside the cited chunk: Exact
        /// (word-for-word), Approximate (in-order repair) or Unlocated (no
        /// highlight, status message). Computed by ResolveSources - never
        /// trusted from the model.</summary>
        public AiQuoteLocation Location { get; set; } = AiQuoteLocation.Unlocated;
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
    public sealed class ChatMessage : INotifyPropertyChanged
    {
        public enum Role { User, Assistant, System }
        public Role MessageRole { get; set; }
        private string _content = "";
        public string Content 
        { 
            get => _content; 
            set { _content = value; OnPropertyChanged(); }
        }
        private List<AiSource> _sources = new();

        /// <summary>
        /// Raises change notifications: the sources chip row under a bubble
        /// binds when the template materializes (long before the reply fills
        /// this list), so it must observe later replacements too.
        /// </summary>
        public List<AiSource> Sources
        {
            get => _sources;
            set { _sources = value ?? new List<AiSource>(); OnPropertyChanged(); }
        }

        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        private bool _isLoading;
        public bool IsLoading 
        { 
            get => _isLoading; 
            set { _isLoading = value; OnPropertyChanged(); }
        }
        private string? _error;
        public string? Error 
        { 
            get => _error; 
            set { _error = value; OnPropertyChanged(); }
        }
        
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Configuration for AI provider.
    /// </summary>
    public sealed class AiProviderConfig
    {
        // NVIDIA's NIM cloud API is the chat backend; nemotron-3-ultra-550b-a55b
        // is the default model (OpenAI-compatible /v1/chat/completions, ~40
        // req/min free tier). The local Ollama bridge stays available through
        // the settings' Ollama preset.
        public string ProviderType { get; set; } = "NVIDIA NIM";
        public string BaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";
        public string ApiKey { get; set; } = "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";
        public string Model { get; set; } = "nvidia/nemotron-3-ultra-550b-a55b";

        // Semantic retrieval model - used ONLY for Ollama /api/embed calls
        // (local vectors for the hybrid retriever). Never used for chat
        // generation; that stays Nemotron on the NIM API.
        public string EmbeddingModel { get; set; } = "embeddinggemma:latest";

        // Retrieval behavior (was hardcoded in the AiChatViewModel ctor).
        public int TopK { get; set; } = 24;
        public int EvidenceCharBudget { get; set; } = 40000;
        public int MaxHistoryMessages { get; set; } = 6;

        // Embedding prompt prefixes (model-specific). The EmbeddingGemma model
        // card prescribes task prompts - documents: "title: none | text: ...",
        // queries: "task: search results | query: ...". nomic-embed-text
        // models use "search_document: " / "search_query: ". Changing a
        // prefix invalidates stored vectors (they are part of the embedding
        // state).
        public string EmbeddingDocumentPrefix { get; set; } = "title: none | text: ";
        public string EmbeddingQueryPrefix { get; set; } = "task: search results | query: ";
        public double Temperature { get; set; } = 0.5;
        public int MaxTokens { get; set; } = 16384;
        public double TopP { get; set; } = 0.95;
        public string? ReasoningEffort { get; set; } = "low";  // low, medium, high

        // Ask the endpoint for JSON output (response_format). The default NIM
        // endpoint accepts it; other OpenAI-compatible servers may reject unknown
        // request fields. The provider also falls back automatically when the server
        // answers 400 naming the field; set false to never send it at all.
        public bool RequestJsonOutput { get; set; } = true;

        // Cloud model detection: any endpoint off this machine (NIM and friends)
        // counts as cloud - the -cloud model-name suffix died with gpt-oss.
        public bool IsCloudModel => !string.IsNullOrEmpty(BaseUrl)
            && !AiEndpoints.IsLocal(BaseUrl);
    }

}