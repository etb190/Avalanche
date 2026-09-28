using System.Collections.Generic;
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
            AiProviderConfig config);

        /// <summary>
        /// Gets a chat completion with structured output and source references.
        /// </summary>
        Task<AiResponse> GetChatCompletionAsync(
            string systemPrompt,
            List<ChatMessage> messages,
            List<DocumentChunk> contextChunks,
            string sourceReferences,
            AiProviderConfig config);

        /// <summary>
        /// Checks if the provider is configured and available.
        /// </summary>
        Task<bool> IsAvailableAsync(AiProviderConfig config);
    }
}