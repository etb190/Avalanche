using System;

namespace Avalanche.Features.AI
{
    /// <summary>Why an AI request failed. The provider classifies every
    /// failure into one of these; the view model maps each category to a
    /// localized Str_AiError* string. Raw exception text and response
    /// bodies never reach the UI.</summary>
    public enum AiErrorCategory
    {
        Other,
        OllamaNotRunning,
        NotSignedIn,
        ModelNotFound,
        Busy,
        UsageLimit,
        CutOff,
        Timeout,
        BadResponse
    }

    /// <summary>The only exception type the provider throws at callers.
    /// Carries the machine-readable category (plus HTTP status and model
    /// name where known) instead of English prose that callers would have
    /// to keyword-match.</summary>
    public sealed class AiProviderException : Exception
    {
        public AiErrorCategory Category { get; }
        public System.Net.HttpStatusCode? HttpStatus { get; }
        public string? ModelName { get; }

        public AiProviderException(
            AiErrorCategory category,
            string? modelName = null,
            System.Net.HttpStatusCode? httpStatus = null,
            Exception? inner = null)
            : base($"AI request failed: {category}", inner)
        {
            Category = category;
            HttpStatus = httpStatus;
            ModelName = modelName;
        }
    }
}
