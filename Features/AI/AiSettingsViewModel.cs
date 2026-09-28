using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Settings for AI provider configuration.
    /// </summary>
    public sealed class AiSettingsViewModel : INotifyPropertyChanged
    {
        // Generation provider settings
        private string _genProviderType = "OpenAICompatible";
        private string _genBaseUrl = "https://api.openai.com/v1";
        private string _genApiKey = "";
        private string _genModel = "gpt-4o-mini";
        private double _genTemperature = 0.1;
        private int _genMaxTokens = 2000;

        // Embedding provider settings
        private string _embProviderType = "OpenAICompatible";
        private string _embBaseUrl = "https://api.openai.com/v1";
        private string _embApiKey = "";
        private string _embModel = "text-embedding-3-small";
        private int _embDimension = 1536;
        private int _embMaxTokens = 8191;
        private int _embBatchSize = 100;

        private bool _isEnabled = false;

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

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public AiProviderConfig ToGenConfig()
        {
            return new AiProviderConfig
            {
                ProviderType = GenProviderType,
                BaseUrl = GenBaseUrl,
                ApiKey = GenApiKey,
                Model = GenModel,
                Temperature = GenTemperature,
                MaxTokens = GenMaxTokens
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
                BatchSize = EmbBatchSize
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
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}