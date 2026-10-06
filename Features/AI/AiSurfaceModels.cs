using System;
using System.IO;

namespace Avalanche.Features.AI
{
    /// <summary>Which model answers each surface. Five dials - the summary,
    /// the sidechat, the browser sidechat, the recaller (the recap) and the
    /// AI tester - each wearing Nemotron by default, each one dropdown away
    /// from gpt-oss:120b-cloud on the local Ollama bridge. The dial owns ONLY who answers: the model and
    /// its host. Tokens, temperature, top-p, reasoning effort, prompts,
    /// retrieval - every other dial stays exactly where the reader set it,
    /// whatever the choice. The choices persist in their own file beside the
    /// AI settings so a surface never needs the panel to be opened first.
    /// (v1.19.31: "two sets of AIs".)</summary>
    internal enum AiSurface { Summary, Sidechat, WebSidechat, Recaller, AiTester }

    internal static class AiSurfaceModels
    {
        public const string NemotronChoice = "nemotron";
        public const string GptChoice = "gpt-oss";

        private sealed class SurfaceChoices
        {
            public string Summary { get; set; } = NemotronChoice;
            public string Sidechat { get; set; } = NemotronChoice;
            public string WebSidechat { get; set; } = NemotronChoice;
            public string Recaller { get; set; } = NemotronChoice;
            public string AiTester { get; set; } = NemotronChoice;
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
        /// not the gpt choice reads as the Nemotron default.</summary>
        public static string NormalizeChoice(string? value)
            => string.Equals(value, GptChoice, StringComparison.OrdinalIgnoreCase)
                ? GptChoice : NemotronChoice;

        public static string Get(AiSurface surface)
        {
            Load();
            return surface switch
            {
                AiSurface.Summary => _choices.Summary,
                AiSurface.WebSidechat => _choices.WebSidechat,
                AiSurface.Recaller => _choices.Recaller,
                AiSurface.AiTester => _choices.AiTester,
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
                default: _choices.Sidechat = clean; break;
            }
            Save();
        }

        /// <summary>The surface's request config: the settings' own config
        /// untouched while the dial says Nemotron; the SAME config wearing the
        /// gpt-oss model and the local Ollama bridge when it says gpt-oss.
        /// Only the model and its host are rewritten - everything else copies
        /// over verbatim, exactly as the reader tuned it.</summary>
        public static AiProviderConfig Configure(AiProviderConfig baseConfig, AiSurface surface)
        {
            if (baseConfig is null) return new AiProviderConfig();
            if (Get(surface) != GptChoice) return baseConfig;
            return new AiProviderConfig
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
        }
    }
}