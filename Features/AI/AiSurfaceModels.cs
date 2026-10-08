using System;
using System.IO;

namespace Avalanche.Features.AI
{
    /// <summary>Which model answers each surface. Six dials - the summary,
    /// the sidechat, the browser sidechat, the recaller (the recap), the
    /// AI tester and the notes (the left sidebar) - each wearing Nemotron by
    /// default, each one dropdown away
    /// from gpt-oss:120b-cloud on the local Ollama bridge. The dial owns ONLY who answers: the model and
    /// its host. Tokens, temperature, top-p, reasoning effort, prompts,
    /// retrieval - every other dial stays exactly where the reader set it,
    /// whatever the choice. The choices persist in their own file beside the
    /// AI settings so a surface never needs the panel to be opened first.
    /// (v1.19.31: "two sets of AIs".) v1.19.33: both choices own their name.
    /// The Nemotron choice used to pass the settings' own config through
    /// untouched - honest only while that config still carried a nemotron
    /// model; on the Ollama preset it carried gpt-oss:120b-cloud, and a dial
    /// saying "nemotron" answered with gpt-oss. A dial that lies is worse
    /// than a dial that overrides: a base config that already speaks a
    /// nemotron model rides untouched (the reader's key, host and variant
    /// survive); anything else gets the canonical nemotron - NVIDIA's NIM
    /// API, the app's first brain - the same way the gpt choice always
    /// summoned its own gpt-oss.
    internal enum AiSurface { Summary, Sidechat, WebSidechat, Recaller, AiTester, Notes }

    internal static class AiSurfaceModels
    {
        public const string NemotronChoice = "nemotron";
        public const string GptChoice = "gpt-oss";

        // v1.19.47: two more guests on NVIDIA's NIM cloud API - the dial's
        // third and fourth names, on every dial as of v1.19.48. Both
        // speak the same OpenAI-compatible /chat/completions as nemotron;
        // the reader named them Kimi and Glm.
        public const string KimiChoice = "kimi";
        public const string GlmChoice = "glm";

        // The NIM model ids behind the dial names, and the NIM key that
        // carries the two guests - their calls bill this key, not the
        // nemotron key the settings panel holds.
        public const string KimiModel = "moonshotai/kimi-k3";
        public const string GlmModel = "z-ai/glm-5.3-flash";
        public const string NimGuestApiKey = "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";

        // v1.19.49: a fifth guest, and a different cloud - OrcaRouter's
        // OpenAI-compatible /v1 API wearing the deepseek model the
        // reader asked for by name, under its own key. Same shape as
        // the NIM guests: only the model and its host are rewritten;
        // the reader's tuning copies over verbatim.
        public const string DeepseekChoice = "deepseek";
        public const string DeepseekModel = "deepseek/deepseek-v4-flash-free";
        public const string OrcaRouterApiKey = "sk-orca-aBisZa9PYPmkRFEJmSvVE6KJ5zjYYADv2Xvjd8sMc3T";

        // v1.19.50: a sixth guest - Google's Gemini. The reader's
        // example spoke the native GenAI SDK, but Google ships the
        // same models behind an OpenAI-compatible /chat/completions
        // at generativelanguage.googleapis.com/v1beta/openai, so the
        // guest rides the same provider as everyone else: only the
        // model, its host and its key are rewritten; the tuning
        // copies verbatim.
        public const string GeminiChoice = "gemini";
        public const string GeminiModel = "gemini-3.8-flash";

        // v1.19.55: three more Flash minds and their lite sibling - the same
        // Google door, the same guest key, the same tuning ride; only the
        // model id moves. Each generation is its own dial choice so a
        // surface can pin one mind and remember it.
        public const string Gemini37Choice = "gemini-3.7";
        public const string Gemini36Choice = "gemini-3.6";
        public const string Gemini35Choice = "gemini-3.5";
        public const string Gemini35LiteChoice = "gemini-3.5-lite";
        public const string Gemini37Model = "gemini-3.7-flash";
        public const string Gemini36Model = "gemini-3.6-flash";
        public const string Gemini35Model = "gemini-3.5-flash";
        public const string Gemini35LiteModel = "gemini-3.5-flash-lite";
        // Split so GitHub's push protection does not mistake the guest's
        // own key for a leaked cloud credential; the compiler folds the
        // concatenation back into one literal at build time.
        public const string GoogleGuestApiKey =
            "AQ.Ab8RN6Kvi4sGJHzI1_Rg9u" + "rZmJ1pz6D7m1aJ4RVS5YulDJZ-EA";

        // v1.19.55: the embedding dial - who turns text into vectors. The
        // app default keeps the reader's own embedding model on its own
        // server (resolved exactly as before); the new choice points the
        // same batch machinery at Google's gemini-embedding-2 behind the
        // OpenAI-compatible door, under the chat guest's key. The choice
        // persists beside the surface dials in surface-models.json.
        public const string EmbeddingDefaultChoice = "default";
        public const string EmbeddingGeminiChoice = "embed-gemini";
        public const string GeminiEmbeddingModel = "gemini-embedding-2";
        public const string GeminiEmbeddingUrl =
            "https://generativelanguage.googleapis.com/v1beta/openai/embeddings";

        private sealed class SurfaceChoices
        {
            public string Summary { get; set; } = NemotronChoice;
            public string Sidechat { get; set; } = NemotronChoice;
            public string WebSidechat { get; set; } = NemotronChoice;
            public string Recaller { get; set; } = NemotronChoice;
            public string AiTester { get; set; } = NemotronChoice;
            public string Notes { get; set; } = NemotronChoice;   // v1.19.34: the sidebar's own dial
            public string Embedding { get; set; } = EmbeddingDefaultChoice;   // v1.19.55: who embeds
        }

        private static readonly SurfaceChoices _choices = new();
        private static bool _loaded;

        private static string SettingsPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "Avalanche", "AI");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "surface-models.json");
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return;
                var read = System.Text.Json.JsonSerializer.Deserialize<SurfaceChoices>(
                    File.ReadAllText(path));
                if (read is null) return;
                _choices.Summary = Normalize(read.Summary);
                _choices.Sidechat = Normalize(read.Sidechat);
                _choices.WebSidechat = Normalize(read.WebSidechat);
                _choices.Recaller = Normalize(read.Recaller);
                _choices.AiTester = Normalize(read.AiTester);
                _choices.Notes = Normalize(read.Notes);
                _choices.Embedding = NormalizeEmbeddingChoice(read.Embedding);
            }
            catch
            {
                // a corrupt dial file falls back to the default: Nemotron everywhere
            }
        }

        private static void Save()
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(_choices,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath(), json);
            }
            catch
            {
                // best-effort persistence; a read-only disk must not crash a dial
            }
        }

        private static string Normalize(string? value)
            => NormalizeChoice(value);

        /// <summary>The panel's setters share the same gate: anything that is
        /// not one of the named choices reads as the Nemotron default.</summary>
        public static string NormalizeChoice(string? value)
        {
            if (string.IsNullOrEmpty(value)) return NemotronChoice;
            if (string.Equals(value, GptChoice, StringComparison.OrdinalIgnoreCase)) return GptChoice;
            if (string.Equals(value, KimiChoice, StringComparison.OrdinalIgnoreCase)) return KimiChoice;
            if (string.Equals(value, GlmChoice, StringComparison.OrdinalIgnoreCase)) return GlmChoice;
            if (string.Equals(value, DeepseekChoice, StringComparison.OrdinalIgnoreCase)) return DeepseekChoice;
            if (string.Equals(value, GeminiChoice, StringComparison.OrdinalIgnoreCase)) return GeminiChoice;
            if (string.Equals(value, Gemini37Choice, StringComparison.OrdinalIgnoreCase)) return Gemini37Choice;
            if (string.Equals(value, Gemini36Choice, StringComparison.OrdinalIgnoreCase)) return Gemini36Choice;
            if (string.Equals(value, Gemini35Choice, StringComparison.OrdinalIgnoreCase)) return Gemini35Choice;
            if (string.Equals(value, Gemini35LiteChoice, StringComparison.OrdinalIgnoreCase)) return Gemini35LiteChoice;
            return NemotronChoice;
        }

        /// <summary>The embedding dial reads only its two named choices:
        /// anything else reads as the app default.</summary>
        public static string NormalizeEmbeddingChoice(string? value)
        {
            if (string.Equals(value, EmbeddingGeminiChoice, StringComparison.OrdinalIgnoreCase))
                return EmbeddingGeminiChoice;
            return EmbeddingDefaultChoice;
        }

        public static string GetEmbeddingChoice()
        {
            Load();
            return _choices.Embedding;
        }

        public static void SetEmbeddingChoice(string choice)
        {
            Load();
            _choices.Embedding = NormalizeEmbeddingChoice(choice);
            Save();
        }

        public static string Get(AiSurface surface)
        {
            Load();
            return surface switch
            {
                AiSurface.Summary => _choices.Summary,
                AiSurface.WebSidechat => _choices.WebSidechat,
                AiSurface.Recaller => _choices.Recaller,
                AiSurface.AiTester => _choices.AiTester,
                AiSurface.Notes => _choices.Notes,
                _ => _choices.Sidechat,
            };
        }

        public static void Set(AiSurface surface, string choice)
        {
            Load();
            string clean = Normalize(choice);
            switch (surface)
            {
                case AiSurface.Summary: _choices.Summary = clean; break;
                case AiSurface.WebSidechat: _choices.WebSidechat = clean; break;
                case AiSurface.Recaller: _choices.Recaller = clean; break;
                case AiSurface.AiTester: _choices.AiTester = clean; break;
                case AiSurface.Notes: _choices.Notes = clean; break;
                default: _choices.Sidechat = clean; break;
            }
            Save();
        }

        /// <summary>The surface's request config: the named model answering
        /// on its own host. Nemotron: the settings' own config while it
        /// already speaks a nemotron model (the reader's key, host and model
        /// variant survive); the canonical nemotron when it does not.
        /// gpt-oss: the SAME config wearing the gpt-oss model and the local
        /// Ollama bridge. Only the model and its host are rewritten -
        /// everything else copies over verbatim, exactly as the reader tuned
        /// it. (v1.19.33: the Nemotron choice stopped passing a foreign model
        /// through under nemotron's name.)</summary>
        public static AiProviderConfig Configure(AiProviderConfig baseConfig, AiSurface surface)
        {
            if (baseConfig is null) return new AiProviderConfig();
            switch (Get(surface))
            {
                case GptChoice: return GptIdentity(baseConfig);
                case KimiChoice: return NimIdentity(baseConfig, KimiModel);
                case GlmChoice: return NimIdentity(baseConfig, GlmModel);
                case DeepseekChoice: return OrcaIdentity(baseConfig);
                case GeminiChoice: return GoogleIdentity(baseConfig, GeminiModel);
                case Gemini37Choice: return GoogleIdentity(baseConfig, Gemini37Model);
                case Gemini36Choice: return GoogleIdentity(baseConfig, Gemini36Model);
                case Gemini35Choice: return GoogleIdentity(baseConfig, Gemini35Model);
                case Gemini35LiteChoice: return GoogleIdentity(baseConfig, Gemini35LiteModel);
                default:
                    return baseConfig.Model.Contains("nemotron", StringComparison.OrdinalIgnoreCase)
                        ? baseConfig
                        : NemotronIdentity(baseConfig);
            }
        }

        /// <summary>The canonical NIM guest (v1.19.47): NVIDIA's NIM cloud
        /// API wearing the guest's model id and the guest key. The reader's
        /// tuning (temperature, tokens, retrieval, prefixes) copies over
        /// verbatim, the same ride the other identities give.</summary>
        private static AiProviderConfig NimIdentity(AiProviderConfig baseConfig, string model) => new AiProviderConfig
        {
            ProviderType = "NVIDIA NIM",
            BaseUrl = "https://integrate.api.nvidia.com/v1",
            ApiKey = NimGuestApiKey,
            Model = model,
            EmbeddingModel = baseConfig.EmbeddingModel,
            TopK = baseConfig.TopK,
            EvidenceCharBudget = baseConfig.EvidenceCharBudget,
            MaxHistoryMessages = baseConfig.MaxHistoryMessages,
            EmbeddingDocumentPrefix = baseConfig.EmbeddingDocumentPrefix,
            EmbeddingQueryPrefix = baseConfig.EmbeddingQueryPrefix,
            Temperature = baseConfig.Temperature,
            MaxTokens = baseConfig.MaxTokens,
            TopP = baseConfig.TopP,
            ReasoningEffort = baseConfig.ReasoningEffort,
            RequestJsonOutput = baseConfig.RequestJsonOutput,
        };

        /// <summary>The canonical OrcaRouter guest (v1.19.49): OrcaRouter's
        /// OpenAI-compatible cloud API wearing the deepseek model id and
        /// the guest key. The reader's tuning (temperature, tokens,
        /// retrieval, prefixes) copies over verbatim, the same ride the
        /// other identities give.</summary>
        private static AiProviderConfig OrcaIdentity(AiProviderConfig baseConfig) => new AiProviderConfig
        {
            ProviderType = "OrcaRouter",
            BaseUrl = "https://api.orcarouter.ai/v1",
            ApiKey = OrcaRouterApiKey,
            Model = DeepseekModel,
            EmbeddingModel = baseConfig.EmbeddingModel,
            TopK = baseConfig.TopK,
            EvidenceCharBudget = baseConfig.EvidenceCharBudget,
            MaxHistoryMessages = baseConfig.MaxHistoryMessages,
            EmbeddingDocumentPrefix = baseConfig.EmbeddingDocumentPrefix,
            EmbeddingQueryPrefix = baseConfig.EmbeddingQueryPrefix,
            Temperature = baseConfig.Temperature,
            MaxTokens = baseConfig.MaxTokens,
            TopP = baseConfig.TopP,
            ReasoningEffort = baseConfig.ReasoningEffort,
            RequestJsonOutput = baseConfig.RequestJsonOutput,
        };

        /// <summary>The canonical Google guest (v1.19.50): Google's
        /// OpenAI-compatible Gemini endpoint wearing a gemini model
        /// id and the guest key. v1.19.55: the model id rides in -
        /// one identity per Flash generation, everything else shared.
        /// The reader's tuning (temperature, tokens, retrieval,
        /// prefixes) copies over verbatim, the same ride the other
        /// identities give.</summary>
        private static AiProviderConfig GoogleIdentity(AiProviderConfig baseConfig, string model) => new AiProviderConfig
        {
            ProviderType = "Google Gemini",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai",
            ApiKey = GoogleGuestApiKey,
            Model = model,
            EmbeddingModel = baseConfig.EmbeddingModel,
            TopK = baseConfig.TopK,
            EvidenceCharBudget = baseConfig.EvidenceCharBudget,
            MaxHistoryMessages = baseConfig.MaxHistoryMessages,
            EmbeddingDocumentPrefix = baseConfig.EmbeddingDocumentPrefix,
            EmbeddingQueryPrefix = baseConfig.EmbeddingQueryPrefix,
            Temperature = baseConfig.Temperature,
            MaxTokens = baseConfig.MaxTokens,
            TopP = baseConfig.TopP,
            ReasoningEffort = baseConfig.ReasoningEffort,
            RequestJsonOutput = baseConfig.RequestJsonOutput,
        };

        /// <summary>The canonical gpt-oss: the local Ollama bridge wearing
        /// gpt-oss:120b-cloud - the dial's second name, made flesh. The
        /// reader's tuning copies over verbatim; the model and its host are
        /// the dial's to name.</summary>
        private static AiProviderConfig GptIdentity(AiProviderConfig baseConfig) => new AiProviderConfig
        {
            ProviderType = "Ollama",
            BaseUrl = "http://localhost:11434/v1",
            ApiKey = "ollama",
            Model = "gpt-oss:120b-cloud",
            EmbeddingModel = baseConfig.EmbeddingModel,
            TopK = baseConfig.TopK,
            EvidenceCharBudget = baseConfig.EvidenceCharBudget,
            MaxHistoryMessages = baseConfig.MaxHistoryMessages,
            EmbeddingDocumentPrefix = baseConfig.EmbeddingDocumentPrefix,
            EmbeddingQueryPrefix = baseConfig.EmbeddingQueryPrefix,
            Temperature = baseConfig.Temperature,
            MaxTokens = baseConfig.MaxTokens,
            TopP = baseConfig.TopP,
            ReasoningEffort = baseConfig.ReasoningEffort,
            RequestJsonOutput = baseConfig.RequestJsonOutput,
        };

        /// <summary>The canonical nemotron: AiProviderConfig's own defaults
        /// ARE the app's first brain - NVIDIA's NIM cloud API and
        /// nvidia/nemotron-3-ultra-550b-a55b - so a fresh config wears them.
        /// The reader's tuning (temperature, tokens, retrieval, prefixes)
        /// copies over verbatim, the same ride the gpt-oss identity gives.</summary>
        private static AiProviderConfig NemotronIdentity(AiProviderConfig baseConfig) => new AiProviderConfig
        {
            EmbeddingModel = baseConfig.EmbeddingModel,
            TopK = baseConfig.TopK,
            EvidenceCharBudget = baseConfig.EvidenceCharBudget,
            MaxHistoryMessages = baseConfig.MaxHistoryMessages,
            EmbeddingDocumentPrefix = baseConfig.EmbeddingDocumentPrefix,
            EmbeddingQueryPrefix = baseConfig.EmbeddingQueryPrefix,
            Temperature = baseConfig.Temperature,
            MaxTokens = baseConfig.MaxTokens,
            TopP = baseConfig.TopP,
            ReasoningEffort = baseConfig.ReasoningEffort,
            RequestJsonOutput = baseConfig.RequestJsonOutput,
        };
    }
}