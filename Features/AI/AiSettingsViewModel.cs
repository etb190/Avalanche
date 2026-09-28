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

        // Embedding provider settings
        private string _embProviderType = "Ollama";
        private string _embBaseUrl = "http://localhost:11434/v1";
        private string _embApiKey = "ollama";
        private string _embModel = "nomic-embed-text";
        private int _embDimension = 768;
        private int _embMaxTokens = 8191;
        private int _embBatchSize = 16;
        private string _embDocumentPrefix = "search_document: ";
        private string _embQueryPrefix = "search_query: ";

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

        // Embedding
        public string EmbProviderType
        {
            get => _embProviderType;
            set { _embProviderType = value; OnPropertyChanged(); }
        }

        public string EmbBaseUrl
        {
            get => _embBaseUrl;
            set { _embBaseUrl = value; OnPropertyChanged(); }
        }

        public string EmbApiKey
        {
            get => _embApiKey;
            set { _embApiKey = value; OnPropertyChanged(); }
        }

        public string EmbModel
        {
            get => _embModel;
            set { _embModel = value; OnPropertyChanged(); }
        }

        public int EmbDimension
        {
            get => _embDimension;
            set { _embDimension = value; OnPropertyChanged(); }
        }

        public int EmbMaxTokens
        {
            get => _embMaxTokens;
            set { _embMaxTokens = value; OnPropertyChanged(); }
        }

        public int EmbBatchSize
        {
            get => _embBatchSize;
            set { _embBatchSize = value; OnPropertyChanged(); }
        }

        public string EmbDocumentPrefix
        {
            get => _embDocumentPrefix;
            set { _embDocumentPrefix = value; OnPropertyChanged(); }
        }

        public string EmbQueryPrefix
        {
            get => _embQueryPrefix;
            set { _embQueryPrefix = value; OnPropertyChanged(); }
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
                Temperature = GenTemperature,
                MaxTokens = GenMaxTokens,
                TopP = GenTopP,
                ReasoningEffort = GenReasoningEffort
            };
        }

        public EmbeddingProviderConfig ToEmbConfig()
        {
            return new EmbeddingProviderConfig
            {
                ProviderType = EmbProviderType,
                BaseUrl = EmbBaseUrl,
                ApiKey = EmbApiKey,
                Model = EmbModel,
                Dimension = EmbDimension,
                MaxTokens = EmbMaxTokens,
                BatchSize = EmbBatchSize,
                DocumentPrefix = EmbDocumentPrefix,
                QueryPrefix = EmbQueryPrefix
            };
        }

        public void LoadFromGenConfig(AiProviderConfig config)
        {
            GenProviderType = config.ProviderType;
            GenBaseUrl = config.BaseUrl;
            GenApiKey = config.ApiKey;
            GenModel = config.Model;
            GenTemperature = config.Temperature;
            GenMaxTokens = config.MaxTokens;
            GenTopP = config.TopP;
            GenReasoningEffort = config.ReasoningEffort ?? "low";
        }

        public void LoadFromEmbConfig(EmbeddingProviderConfig config)
        {
            EmbProviderType = config.ProviderType;
            EmbBaseUrl = config.BaseUrl;
            EmbApiKey = config.ApiKey;
            EmbModel = config.Model;
            EmbDimension = config.Dimension;
            EmbMaxTokens = config.MaxTokens;
            EmbBatchSize = config.BatchSize;
            EmbDocumentPrefix = config.DocumentPrefix;
            EmbQueryPrefix = config.QueryPrefix;
        }

        public void ApplyOllamaPreset()
        {
            GenProviderType = "Ollama";
            GenBaseUrl = "http://localhost:11434/v1";
            GenApiKey = "ollama";
            GenModel = "gpt-oss:120b-cloud";
            GenTemperature = 0.2;
            GenMaxTokens = 4096;
            GenTopP = 1.0;
            GenReasoningEffort = "low";

            EmbProviderType = "Ollama";
            EmbBaseUrl = "http://localhost:11434/v1";
            EmbApiKey = "ollama";
            EmbModel = "nomic-embed-text";
            EmbDimension = 768;
            EmbMaxTokens = 8191;
            EmbBatchSize = 16;
            EmbDocumentPrefix = "search_document: ";
            EmbQueryPrefix = "search_query: ";
        }

        public void ApplyOpenAIPreset()
        {
            GenProviderType = "OpenAICompatible";
            GenBaseUrl = "https://api.openai.com/v1";
            GenApiKey = "";
            GenModel = "gpt-4o-mini";
            GenTemperature = 0.1;
            GenMaxTokens = 4096;
            GenTopP = 1.0;
            GenReasoningEffort = "low";

            EmbProviderType = "OpenAICompatible";
            EmbBaseUrl = "https://api.openai.com/v1";
            EmbApiKey = "";
            EmbModel = "text-embedding-3-small";
            EmbDimension = 1536;
            EmbMaxTokens = 8191;
            EmbBatchSize = 100;
            EmbDocumentPrefix = "";
            EmbQueryPrefix = "";
        }

        public async Task TestConnectionAsync(Func<string, string> loc)
        {
            if (IsTestingConnection) return;
            IsTestingConnection = true;
            ConnectionStatus = loc("Str_AiTestConnecting");

            try
            {
                var genConfig = ToGenConfig();
                var embConfig = ToEmbConfig();

                var genProvider = AiProviderFactory.CreateProvider(genConfig.ProviderType);
                var embProvider = AiProviderFactory.CreateEmbeddingProvider(embConfig);

                var results = new System.Text.StringBuilder();
                bool allOk = true;

                // Test 1: Ollama reachable
                ConnectionStatus = loc("Str_AiTestOllamaReachable");
                bool ollamaReachable = await TestOllamaReachableAsync(GenBaseUrl);
                results.AppendLine(ollamaReachable ? "✓ Ollama reachable" : "✗ Ollama not reachable");
                if (!ollamaReachable) allOk = false;

                // Test 2: Chat model
                ConnectionStatus = loc("Str_AiTestChatModel");
                bool chatOk = await TestChatModelAsync(genConfig);
                results.AppendLine(chatOk ? "✓ Chat model responds" : "✗ Chat model failed");
                if (!chatOk) allOk = false;

                // Test 3: Embedding model
                ConnectionStatus = loc("Str_AiTestEmbeddingModel");
                bool embOk = await TestEmbeddingModelAsync(embConfig);
                results.AppendLine(embOk ? "✓ Embedding model responds" : "✗ Embedding model failed");
                if (!embOk) allOk = false;

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

        private async Task<bool> TestChatModelAsync(AiProviderConfig config)
        {
            try
            {
                var provider = AiProviderFactory.CreateProvider(config.ProviderType);
                var response = await provider.GetChatCompletionAsync(
                    "Reply with just 'OK'",
                    new List<ChatMessage> { new ChatMessage { MessageRole = ChatMessage.Role.User, Content = "test" } },
                    new List<DocumentChunk>(),
                    "");
                return !string.IsNullOrEmpty(response.Answer);
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> TestEmbeddingModelAsync(EmbeddingProviderConfig config)
        {
            try
            {
                var provider = AiProviderFactory.CreateEmbeddingProvider(config);
                var embedding = await provider.GenerateEmbeddingAsync("test");
                return embedding != null && embedding.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private void UpdateCloudWarning()
        {
            ShowCloudWarning = GenModel?.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase) == true 
                || (!GenBaseUrl.Contains("localhost") && !GenBaseUrl.Contains("127.0.0.1"));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}