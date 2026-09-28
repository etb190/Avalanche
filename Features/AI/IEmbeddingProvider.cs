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
}