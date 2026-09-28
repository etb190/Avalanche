using System.Collections.Generic;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Abstraction for embedding generation. Keeps generation and embeddings separate.
    /// Supports local models and OpenAI-compatible endpoints.
    /// </summary>
    public interface IEmbeddingProvider
    {
        /// <summary>
        /// Generates embeddings for the given texts.
        /// </summary>
        /// <param name="texts">Texts to embed.</param>
        /// <returns>Array of embedding vectors (one per input text).</returns>
        Task<float[][]> GenerateEmbeddingsAsync(IReadOnlyList<string> texts);

        /// <summary>
        /// Generates a single embedding for a query text.
        /// </summary>
        Task<float[]> GenerateEmbeddingAsync(string text);

        /// <summary>
        /// Dimension of the embedding vectors.
        /// </summary>
        int Dimension { get; }

        /// <summary>
        /// Maximum number of tokens per input (for truncation guidance).
        /// </summary>
        int MaxTokens { get; }

        /// <summary>
        /// Checks if the provider is available.
        /// </summary>
        Task<bool> IsAvailableAsync();
        
        /// <summary>
        /// Gets the model name and dimension for index compatibility checking.
        /// </summary>
        (string ModelName, int Dimension) GetModelInfo();
    }

    /// <summary>
    /// Configuration for embedding providers.
    /// </summary>
    public sealed class EmbeddingProviderConfig
    {
        public string ProviderType { get; set; } = "OpenAICompatible";
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "nomic-embed-text";
        public int Dimension { get; set; } = 768;  // nomic-embed-text is 768
        public int MaxTokens { get; set; } = 8191;
        public int BatchSize { get; set; } = 16;  // Smaller batches for Ollama
        
        // Task prefixes for embedding models that support them (e.g., nomic-embed-text)
        public string DocumentPrefix { get; set; } = "search_document: ";
        public string QueryPrefix { get; set; } = "search_query: ";
        
        // Embedding model info for index compatibility
        public string EmbeddingModelName { get; set; } = "";
        public int EmbeddingDimension { get; set; } = 0;
    }
}