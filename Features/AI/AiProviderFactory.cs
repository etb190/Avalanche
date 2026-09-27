using System.Collections.Generic;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Factory for creating AI providers.
    /// </summary>
    internal static class AiProviderFactory
    {
        public static IAiProvider CreateProvider(string providerType)
        {
            return providerType?.ToLowerInvariant() switch
            {
                "openai" or "openaicompatible" or "openai-compatible" => new OpenAiCompatibleProvider(),
                _ => new OpenAiCompatibleProvider() // Default
            };
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
    }
}