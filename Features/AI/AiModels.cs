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
        public List<int[]> WordRanges { get; set; } = new();          // [startWord, endWord] per page
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
        Reranked
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
        // Ollama's localhost bridge is the only AI backend; gpt-oss:120b-cloud is
        // the only model (routed to ollama.com through the local bridge).
        public string ProviderType { get; set; } = "Ollama";
        public string BaseUrl { get; set; } = "http://localhost:11434/v1";
        public string ApiKey { get; set; } = "ollama";
        public string Model { get; set; } = "gpt-oss:120b-cloud";
        public double Temperature { get; set; } = 0.1;
        public int MaxTokens { get; set; } = 4096;  // Increased default for reasoning models
        public double TopP { get; set; } = 1.0;
        public string? ReasoningEffort { get; set; } = "low";  // low, medium, high
        
        // Ollama-specific: cloud model detection
        public bool IsCloudModel => Model?.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase) == true;
    }

}