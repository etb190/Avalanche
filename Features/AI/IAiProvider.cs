using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Interface for AI providers.
    /// </summary>
    public interface IAiProvider
    {
        /// <summary>
        /// Gets a chat completion with structured output.
        /// </summary>
        Task<AiResponse> GetChatCompletionAsync(
            string systemPrompt,
            List<ChatMessage> messages,
            List<DocumentChunk> contextChunks,
            AiProviderConfig config,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Streams a chat completion as SSE deltas: each yield carries the next
        /// piece of raw assistant content (reasoning traces excluded). The
        /// buffered contract is unchanged - the full raw text, parsed at the
        /// end, still decides citations and structure.
        /// </summary>
        IAsyncEnumerable<string> GetChatCompletionStreamAsync(
            string systemPrompt,
            List<ChatMessage> messages,
            List<DocumentChunk> contextChunks,
            string sourceReferences,
            AiProviderConfig config,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a chat completion with structured output and source references.
        /// </summary>
        Task<AiResponse> GetChatCompletionAsync(
            string systemPrompt,
            List<ChatMessage> messages,
            List<DocumentChunk> contextChunks,
            string sourceReferences,
            AiProviderConfig config,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if the provider is configured and available.
        /// </summary>
        Task<bool> IsAvailableAsync(AiProviderConfig config);
    }
}
