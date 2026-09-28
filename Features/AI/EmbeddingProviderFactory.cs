using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Factory for creating embedding providers.
    /// </summary>
    public static class EmbeddingProviderFactory
    {
        public static IEmbeddingProvider CreateProvider(EmbeddingProviderConfig config)
        {
            return config?.ProviderType?.ToLowerInvariant() switch
            {
                "openai" or "openaicompatible" or "openai-compatible" => new OpenAiCompatibleEmbeddingProvider(config),
                _ => new OpenAiCompatibleEmbeddingProvider(config ?? new EmbeddingProviderConfig())
            };
        }

        public static async Task<bool> TestProviderAsync(IEmbeddingProvider provider)
        {
            try
            {
                return await provider.IsAvailableAsync();
            }
            catch
            {
                return false;
            }
        }
    }
}