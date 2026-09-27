using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Settings for AI provider configuration.
    /// </summary>
    internal sealed class AiSettingsViewModel : INotifyPropertyChanged
    {
        private string _providerType = "OpenAICompatible";
        private string _baseUrl = "https://api.openai.com/v1";
        private string _apiKey = "";
        private string _model = "gpt-4o-mini";
        private double _temperature = 0.1;
        private int _maxTokens = 2000;
        private bool _isEnabled = false;

        public string ProviderType
        {
            get => _providerType;
            set { _providerType = value; OnPropertyChanged(); }
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set { _baseUrl = value; OnPropertyChanged(); }
        }

        public string ApiKey
        {
            get => _apiKey;
            set { _apiKey = value; OnPropertyChanged(); }
        }

        public string Model
        {
            get => _model;
            set { _model = value; OnPropertyChanged(); }
        }

        public double Temperature
        {
            get => _temperature;
            set { _temperature = value; OnPropertyChanged(); }
        }

        public int MaxTokens
        {
            get => _maxTokens;
            set { _maxTokens = value; OnPropertyChanged(); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public AiProviderConfig ToConfig()
        {
            return new AiProviderConfig
            {
                ProviderType = ProviderType,
                BaseUrl = BaseUrl,
                ApiKey = ApiKey,
                Model = Model,
                Temperature = Temperature,
                MaxTokens = MaxTokens
            };
        }

        public void LoadFromConfig(AiProviderConfig config)
        {
            ProviderType = config.ProviderType;
            BaseUrl = config.BaseUrl;
            ApiKey = config.ApiKey;
            Model = config.Model;
            Temperature = config.Temperature;
            MaxTokens = config.MaxTokens;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}