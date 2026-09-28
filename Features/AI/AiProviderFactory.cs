using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Factory for creating AI providers.
    /// </summary>
    public static class AiProviderFactory
    {
        public static IAiProvider CreateProvider(string providerType)
        {
            return providerType?.ToLowerInvariant() switch
            {
                "openai" or "openaicompatible" or "openai-compatible" => new OpenAiCompatibleProvider(),
                _ => new OpenAiCompatibleProvider()
            };
        }

        public static IEmbeddingProvider CreateEmbeddingProvider(EmbeddingProviderConfig config)
        {
            return EmbeddingProviderFactory.CreateProvider(config);
        }

        public static async Task<bool> TestProviderAsync(IAiProvider provider, AiProviderConfig config)
        {
            try
            {
                return await provider.IsAvailableAsync(config);
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> TestEmbeddingProviderAsync(IEmbeddingProvider provider)
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