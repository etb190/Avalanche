using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Settings for AI provider configuration.
    /// </summary>
    public sealed class AiSettingsViewModel : INotifyPropertyChanged
    {
        // Generation provider settings
        private string _genProviderType = "NVIDIA NIM";  // Default to NVIDIA NIM (cloud)
        private string _genBaseUrl = "https://integrate.api.nvidia.com/v1";
        private string _genApiKey = "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";
        private string _genModel = "nvidia/nemotron-3-ultra-550b-a55b";
        private double _genTemperature = 0.5;
        private int _genMaxTokens = 16384;
        private double _genTopP = 0.95;
        private string _genReasoningEffort = "low";
        private string _embeddingModel = "embeddinggemma:latest";
        private int _topK = 24;
        private int _evidenceCharBudget = 40000;
        private int _maxHistoryMessages = 6;
        private string _embeddingDocumentPrefix = "title: none | text: ";
        private string _embeddingQueryPrefix = "task: search results | query: ";

        // v1.19.31: the four model dials - Nemotron by default, gpt-oss one
        // dropdown away. The dial itself lives in AiSurfaceModels (shared with
        // every surface); these properties only mirror it for the combos and
        // write through on every change.
        private string _summaryModelChoice = AiSurfaceModels.NemotronChoice;
        private string _sidechatModelChoice = AiSurfaceModels.NemotronChoice;
        private string _webSidechatModelChoice = AiSurfaceModels.NemotronChoice;
        private string _recallerModelChoice = AiSurfaceModels.NemotronChoice;
        private string _aiTesterModelChoice = AiSurfaceModels.NemotronChoice;
        private string _notesModelChoice = AiSurfaceModels.NemotronChoice;   // v1.19.34: the sidebar's notes
        private string _editorGrammarModelChoice = AiSurfaceModels.NemotronChoice;   // v1.19.78: the sheet's proofreader
        private string _editorRewriteModelChoice = AiSurfaceModels.NemotronChoice;   // v1.19.78: the sheet's rewriter
        private string _embeddingChoice = AiSurfaceModels.EmbeddingDefaultChoice;   // v1.19.55: who embeds

        private bool _isEnabled = false;
        private string _connectionStatus = "";
        private bool _isTestingConnection;

        // Generation
        public string GenProviderType
        {
            get => _genProviderType;
            set { _genProviderType = value; OnPropertyChanged(); }
        }

        public string GenBaseUrl
        {
            get => _genBaseUrl;
            set { _genBaseUrl = value; OnPropertyChanged(); }
        }

        public string GenApiKey
        {
            get => _genApiKey;
            set { _genApiKey = value; OnPropertyChanged(); }
        }

        public string GenModel
        {
            get => _genModel;
            set { _genModel = value; OnPropertyChanged(); }
        }

        public double GenTemperature
        {
            get => _genTemperature;
            set { _genTemperature = value; OnPropertyChanged(); }
        }

        public int GenMaxTokens
        {
            get => _genMaxTokens;
            set { _genMaxTokens = value; OnPropertyChanged(); }
        }

        public double GenTopP
        {
            get => _genTopP;
            set { _genTopP = value; OnPropertyChanged(); }
        }

        public string GenReasoningEffort
        {
            get => _genReasoningEffort;
            set { _genReasoningEffort = value; OnPropertyChanged(); }
        }

        /// <summary>Model used ONLY for the semantic retrieval layer
        /// (Ollama /api/embed). Chat generation keeps GenModel.
        /// v1.19.56: no settings text box any more - the embedding dial is
        /// the one control; this value rides from config and presets.</summary>
        public string EmbeddingModel
        {
            get => _embeddingModel;
            set { _embeddingModel = value; OnPropertyChanged(); }
        }

        public int TopK
        {
            get => _topK;
            set { _topK = Math.Max(1, value); OnPropertyChanged(); }
        }

        public int EvidenceCharBudget
        {
            get => _evidenceCharBudget;
            set { _evidenceCharBudget = Math.Max(1000, value); OnPropertyChanged(); }
        }

        public int MaxHistoryMessages
        {
            get => _maxHistoryMessages;
            set { _maxHistoryMessages = Math.Max(2, value); OnPropertyChanged(); }
        }

        /// <summary>Which model answers the summary: nemotron | gpt-oss.</summary>
        public string SummaryModelChoice
        {
            get => _summaryModelChoice;
            set { _summaryModelChoice = AiSurfaceModels.NormalizeChoice(value); AiSurfaceModels.Set(AiSurface.Summary, _summaryModelChoice); OnPropertyChanged(); }
        }

        /// <summary>Which model answers the book sidechat.</summary>
        public string SidechatModelChoice
        {
            get => _sidechatModelChoice;
            set { _sidechatModelChoice = AiSurfaceModels.NormalizeChoice(value); AiSurfaceModels.Set(AiSurface.Sidechat, _sidechatModelChoice); OnPropertyChanged(); }
        }

        /// <summary>Which model answers the browser sidechat.</summary>
        public string WebSidechatModelChoice
        {
            get => _webSidechatModelChoice;
            set { _webSidechatModelChoice = AiSurfaceModels.NormalizeChoice(value); AiSurfaceModels.Set(AiSurface.WebSidechat, _webSidechatModelChoice); OnPropertyChanged(); }
        }

        /// <summary>Which model answers the AI tester.</summary>
        public string AiTesterModelChoice
        {
            get => _aiTesterModelChoice;
            set { _aiTesterModelChoice = AiSurfaceModels.NormalizeChoice(value); AiSurfaceModels.Set(AiSurface.AiTester, _aiTesterModelChoice); OnPropertyChanged(); }
        }

        /// <summary>Which model answers the recaller.</summary>
        public string RecallerModelChoice
        {
            get => _recallerModelChoice;
            set { _recallerModelChoice = AiSurfaceModels.NormalizeChoice(value); AiSurfaceModels.Set(AiSurface.Recaller, _recallerModelChoice); OnPropertyChanged(); }
        }

        /// <summary>Which model writes the left sidebar's notes (v1.19.34) -
        /// the dial the reader thought was removed, now genuinely its own.</summary>
        public string NotesModelChoice
        {
            get => _notesModelChoice;
            set { _notesModelChoice = AiSurfaceModels.NormalizeChoice(value); AiSurfaceModels.Set(AiSurface.Notes, _notesModelChoice); OnPropertyChanged(); }
        }

        /// <summary>v1.19.78: the sheet's seat split in two - which model
        /// proofreads the timer's batched scan (Grammar) and which model
        /// answers the seven-voice rewriter (Rewrite). Two dials, one law:
        /// only who answers moves.</summary>
        public string EditorGrammarModelChoice
        {
            get => _editorGrammarModelChoice;
            set { _editorGrammarModelChoice = AiSurfaceModels.NormalizeChoice(value); AiSurfaceModels.Set(AiSurface.EditorGrammar, _editorGrammarModelChoice); OnPropertyChanged(); }
        }

        /// <summary>v1.19.78: the rewriter's own dial - the seven voices
        /// bill whoever the reader seated here, nothing else moves.</summary>
        public string EditorRewriteModelChoice
        {
            get => _editorRewriteModelChoice;
            set { _editorRewriteModelChoice = AiSurfaceModels.NormalizeChoice(value); AiSurfaceModels.Set(AiSurface.EditorRewrite, _editorRewriteModelChoice); OnPropertyChanged(); }
        }

        /// <summary>v1.19.55: the embedding dial - the app default (the
        /// reader's own embedding model on its own server) or Google's
        /// gemini-embedding-2 behind the OpenAI-compatible door. Persists
        /// beside the surface dials.</summary>
        public string EmbeddingChoice
        {
            get => _embeddingChoice;
            set { _embeddingChoice = AiSurfaceModels.NormalizeEmbeddingChoice(value); AiSurfaceModels.SetEmbeddingChoice(_embeddingChoice); OnPropertyChanged(); }
        }

        public string EmbeddingDocumentPrefix
        {
            get => _embeddingDocumentPrefix;
            set { _embeddingDocumentPrefix = value ?? ""; OnPropertyChanged(); }
        }

        public string EmbeddingQueryPrefix
        {
            get => _embeddingQueryPrefix;
            set { _embeddingQueryPrefix = value ?? ""; OnPropertyChanged(); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public string ConnectionStatus
        {
            get => _connectionStatus;
            private set { _connectionStatus = value; OnPropertyChanged(); }
        }

        public bool IsTestingConnection
        {
            get => _isTestingConnection;
            private set { _isTestingConnection = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanTestConnection)); }
        }

        public bool CanTestConnection => !IsTestingConnection;


        public AiProviderConfig ToGenConfig()
        {
            return new AiProviderConfig
            {
                ProviderType = GenProviderType,
                BaseUrl = GenBaseUrl,
                ApiKey = GenApiKey,
                Model = GenModel,
                EmbeddingModel = EmbeddingModel,
                Temperature = GenTemperature,
                MaxTokens = GenMaxTokens,
                TopP = GenTopP,
                ReasoningEffort = GenReasoningEffort,
                TopK = TopK,
                EvidenceCharBudget = EvidenceCharBudget,
                MaxHistoryMessages = MaxHistoryMessages,
                EmbeddingDocumentPrefix = EmbeddingDocumentPrefix,
                EmbeddingQueryPrefix = EmbeddingQueryPrefix
            };
        }

        public void LoadFromGenConfig(AiProviderConfig config)
        {
            GenProviderType = config.ProviderType;
            GenBaseUrl = config.BaseUrl;
            GenApiKey = config.ApiKey;
            GenModel = config.Model;
            EmbeddingModel = string.IsNullOrWhiteSpace(config.EmbeddingModel)
                ? "embeddinggemma:latest" : config.EmbeddingModel;
            GenTemperature = config.Temperature;
            GenMaxTokens = config.MaxTokens;
            GenTopP = config.TopP;
            GenReasoningEffort = config.ReasoningEffort ?? "low";
            TopK = config.TopK;
            EvidenceCharBudget = config.EvidenceCharBudget;
            MaxHistoryMessages = config.MaxHistoryMessages;
            EmbeddingDocumentPrefix = string.IsNullOrEmpty(config.EmbeddingDocumentPrefix)
                ? "title: none | text: " : config.EmbeddingDocumentPrefix;
            EmbeddingQueryPrefix = string.IsNullOrEmpty(config.EmbeddingQueryPrefix)
                ? "task: search results | query: " : config.EmbeddingQueryPrefix;
        }

        public void ApplyOllamaPreset()
        {
            GenProviderType = "Ollama";
            GenBaseUrl = "http://localhost:11434/v1";
            GenApiKey = "ollama";
            GenModel = "gpt-oss:120b-cloud";
            EmbeddingModel = "embeddinggemma:latest";
            GenTemperature = 0.2;
            GenMaxTokens = 4096;
            GenTopP = 1.0;
            GenReasoningEffort = "low";
        }

        /// <summary>The default: chat generation on NVIDIA's NIM cloud API,
        /// embeddings still on the local Ollama bridge.</summary>
        public void ApplyNvidiaNimPreset()
        {
            GenProviderType = "NVIDIA NIM";
            GenBaseUrl = "https://integrate.api.nvidia.com/v1";
            GenApiKey = "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";
            GenModel = "nvidia/nemotron-3-ultra-550b-a55b";
            EmbeddingModel = "embeddinggemma:latest";  // embeddings stay on local Ollama
            GenTemperature = 0.5;
            GenMaxTokens = 16384;
            GenTopP = 0.95;
            GenReasoningEffort = "low";
        }

        public async Task TestConnectionAsync(Func<string, string> loc)
        {
            if (IsTestingConnection) return;
            IsTestingConnection = true;
            ConnectionStatus = loc("Str_AiTestConnecting");

            try
            {
                var genConfig = ToGenConfig();

                var results = new System.Text.StringBuilder();
                bool allOk = true;

                // Test 1: generation endpoint reachable. A local bridge answers
                // its native /api/version; a cloud endpoint (NIM) has no such
                // route, so for cloud the reachability question is left to
                // Test 2 itself - a real chat reply is the only honest word.
                bool ollamaReachable;
                if (AiEndpoints.IsLocal(GenBaseUrl))
                {
                    ConnectionStatus = loc("Str_AiTestOllamaReachable");
                    ollamaReachable = await TestOllamaReachableAsync(GenBaseUrl);
                    results.AppendLine(ollamaReachable ? "✓ " + loc("Str_AiTestOllamaOk") : "✗ " + loc("Str_AiTestOllamaFail"));
                    allOk &= ollamaReachable;
                }
                else
                {
                    ollamaReachable = true;   // the chat test speaks for the cloud
                }

                // Test 2: chat model answers a tiny request (only over a
                // reachable server - and with typed errors, only a REAL reply
                // passes; cut-off/bad responses fail with their category).
                bool chatOk = false;
                if (ollamaReachable)
                {
                    ConnectionStatus = loc("Str_AiTestChatModel");
                    chatOk = await TestChatModelAsync(genConfig, loc);
                }
                results.AppendLine(chatOk ? "✓ " + loc("Str_AiTestChatOk") : "✗ " + loc("Str_AiTestChatFail"));
                allOk &= chatOk;

                // Test 3: embedding model returns a vector (previously missing
                // entirely - Str_AiTestEmbeddingModel existed but was unused).
                ConnectionStatus = loc("Str_AiTestEmbeddingModel");
                bool embOk = await TestEmbeddingModelAsync(genConfig);
                results.AppendLine(embOk ? "✓ " + loc("Str_AiTestEmbOk") : "✗ " + loc("Str_AiTestEmbFail"));
                allOk &= embOk;

                ConnectionStatus = (allOk ? loc("Str_AiTestAllPassed") : loc("Str_AiTestSomeFailed"))
                                   + "\n\n" + results.ToString();
            }
            catch
            {
                // No exception details in the UI (B3): the per-check lines
                // above carry the outcome; raw error text stays out.
                ConnectionStatus = loc("Str_AiTestError");
            }
            finally
            {
                IsTestingConnection = false;
            }
        }

        private async Task<bool> TestOllamaReachableAsync(string baseUrl)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var ollamaUrl = baseUrl.TrimEnd('/');
                if (ollamaUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    ollamaUrl = ollamaUrl[..^3]; // strip only a TRAILING /v1 (a base URL containing /v1 elsewhere must survive)
                var response = await client.GetAsync($"{ollamaUrl}/api/version");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TestChatModelAsync(AiProviderConfig config, Func<string, string> loc)
        {
            try
            {
                var provider = AiProviderFactory.CreateProvider(config.ProviderType);
                try
                {
                    var response = await provider.GetChatCompletionAsync(
                        loc("Str_AiTestChatPrompt"),
                        new List<ChatMessage> { new ChatMessage { MessageRole = ChatMessage.Role.User, Content = loc("Str_AiTestChatContent") } },
                        new List<DocumentChunk>(),
                        "",
                        config);
                    return !string.IsNullOrWhiteSpace(response.Answer);
                }
                finally
                {
                    (provider as IDisposable)?.Dispose();
                }
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> TestEmbeddingModelAsync(AiProviderConfig config)
        {
            var client = new OllamaEmbeddingClient(() => config);
            try
            {
                var vector = await client.GenerateEmbeddingAsync("connection test");
                return vector is { Length: > 0 };
            }
            catch
            {
                return false;
            }
            finally
            {
                client.Dispose();
            }
        }

        // ---- Persistence (F1): JSON in %LocalAppData%\Avalanche\AI --------

        private static string SettingsPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = System.IO.Path.Combine(appData, "Avalanche", "AI");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "settings.json");
        }

        private bool _loaded;

        /// <summary>Loads persisted settings once; values already set by the
        /// caller (defaults) are replaced only when a file exists.</summary>
        public void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var path = SettingsPath();
                if (!System.IO.File.Exists(path)) return;
                var json = System.IO.File.ReadAllText(path);
                var config = System.Text.Json.JsonSerializer.Deserialize<AiProviderConfig>(json);
                // v1.19.31: the four dials ride their own file; the panel mirrors
                // whatever they say right now.
                _summaryModelChoice = AiSurfaceModels.Get(AiSurface.Summary);
                _sidechatModelChoice = AiSurfaceModels.Get(AiSurface.Sidechat);
                _webSidechatModelChoice = AiSurfaceModels.Get(AiSurface.WebSidechat);
                _recallerModelChoice = AiSurfaceModels.Get(AiSurface.Recaller);
                _aiTesterModelChoice = AiSurfaceModels.Get(AiSurface.AiTester);
                _notesModelChoice = AiSurfaceModels.Get(AiSurface.Notes);
                _embeddingChoice = AiSurfaceModels.GetEmbeddingChoice();
                if (config is not null)
                {
                    LoadFromGenConfig(config);
                    // One-time upgrade for settings saved before the evidence scale-up: the
                    // exact old default pair (TopK 8 / 12000 chars = roughly one page of
                    // context) marks values that were never customized, and one page of
                    // context is what made answers terse. Lift it to the new defaults and
                    // persist immediately so this runs once. A deliberate 8/12000 entered
                    // by hand afterwards is honored (and saved as-is by the panel).
                    if (TopK == 8 && EvidenceCharBudget == 12000)
                    {
                        TopK = 24;
                        EvidenceCharBudget = 40000;
                        Save();
                    }
                }
            }
            catch
            {
                // Corrupt settings fall back to defaults; never block the panel.
            }
        }

        public void Save()
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(ToGenConfig(),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(SettingsPath(), json);
            }
            catch
            {
                // Best-effort persistence; a read-only disk must not crash the panel.
            }
        }


        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}