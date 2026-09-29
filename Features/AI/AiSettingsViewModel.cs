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
        private string _genProviderType = "Ollama";  // Default to Ollama
        private string _genBaseUrl = "http://localhost:11434/v1";
        private string _genApiKey = "ollama";  // Dummy key for Ollama
        private string _genModel = "gpt-oss:120b-cloud";
        private double _genTemperature = 0.2;
        private int _genMaxTokens = 4096;
        private double _genTopP = 1.0;
        private string _genReasoningEffort = "low";
        private string _embeddingModel = "embeddinggemma:latest";
        private int _topK = 8;
        private int _evidenceCharBudget = 12000;
        private int _maxHistoryMessages = 6;
        private string _embeddingDocumentPrefix = "title: none | text: ";
        private string _embeddingQueryPrefix = "task: search results | query: ";

        private bool _isEnabled = false;
        private string _connectionStatus = "";
        private bool _isTestingConnection;
        private bool _showCloudWarning;

        // Generation
        public string GenProviderType
        {
            get => _genProviderType;
            set { _genProviderType = value; OnPropertyChanged(); UpdateCloudWarning(); }
        }

        public string GenBaseUrl
        {
            get => _genBaseUrl;
            set { _genBaseUrl = value; OnPropertyChanged(); UpdateCloudWarning(); }
        }

        public string GenApiKey
        {
            get => _genApiKey;
            set { _genApiKey = value; OnPropertyChanged(); }
        }

        public string GenModel
        {
            get => _genModel;
            set { _genModel = value; OnPropertyChanged(); UpdateCloudWarning(); }
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
        /// (Ollama /api/embed). Chat generation keeps GenModel.</summary>
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

        public bool ShowCloudWarning
        {
            get => _showCloudWarning;
            private set { _showCloudWarning = value; OnPropertyChanged(); }
        }

        public string CloudWarningText => "⚠ This model sends your questions and document passages to the cloud (ollama.com). Documents leave this computer.";

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

        public async Task TestConnectionAsync(Func<string, string> loc)
        {
            if (IsTestingConnection) return;
            IsTestingConnection = true;
            ConnectionStatus = loc("Str_AiTestConnecting");

            try
            {
                var genConfig = ToGenConfig();

                var genProvider = AiProviderFactory.CreateProvider(genConfig.ProviderType);

                var results = new System.Text.StringBuilder();
                bool allOk = true;

                // Test 1: Ollama reachable
                ConnectionStatus = loc("Str_AiTestOllamaReachable");
                bool ollamaReachable = await TestOllamaReachableAsync(GenBaseUrl);
                results.AppendLine(ollamaReachable ? "✓ Ollama reachable" : "✗ Ollama not reachable");
                if (!ollamaReachable) allOk = false;

                // Test 2: Chat model
                ConnectionStatus = loc("Str_AiTestChatModel");
                bool chatOk = await TestChatModelAsync(genConfig, loc);
                results.AppendLine(chatOk ? "✓ Chat model responds" : "✗ Chat model failed");
                if (!chatOk) allOk = false;

                ConnectionStatus = allOk ? loc("Str_AiTestAllPassed") : loc("Str_AiTestSomeFailed");
                ConnectionStatus += "\n\n" + results.ToString();
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"{loc("Str_AiTestError")}: {ex.Message}";
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
                var ollamaUrl = baseUrl.Replace("/v1", "");
                var response = await client.GetAsync($"{ollamaUrl.TrimEnd('/')}/api/version");
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
                var response = await provider.GetChatCompletionAsync(
                    loc("Str_AiTestChatPrompt"),
                    new List<ChatMessage> { new ChatMessage { MessageRole = ChatMessage.Role.User, Content = loc("Str_AiTestChatContent") } },
                    new List<DocumentChunk>(),
                    "",
                    config);
                return !string.IsNullOrEmpty(response.Answer);
            }
            catch
            {
                return false;
            }
        }

        private void UpdateCloudWarning()
        {
            ShowCloudWarning = GenModel?.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase) == true
                || !AiEndpoints.IsLocal(GenBaseUrl);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}