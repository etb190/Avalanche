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
    }

    /// <summary>
    /// Configuration for embedding providers.
    /// </summary>
    public sealed class EmbeddingProviderConfig
    {
        public string ProviderType { get; set; } = "OpenAICompatible";
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "text-embedding-3-small";
        public int Dimension { get; set; } = 1536;
        public int MaxTokens { get; set; } = 8191;
        public int BatchSize { get; set; } = 100;
    }
}