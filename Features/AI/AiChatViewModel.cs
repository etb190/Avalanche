using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Avalanche.Services;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// ViewModel for the AI Chat sidebar.
    /// </summary>
    internal sealed class AiChatViewModel : INotifyPropertyChanged
    {
        private readonly IAiProvider _aiProvider;
        private readonly AiProviderConfig _config;
        private readonly MainWindow _mainWindow;
        private DocumentIndex? _currentIndex;
        private string _currentDocumentId = "";
        private string _currentFilePath = "";
        private bool _isIndexing;
        private string _indexingStatus = "";
        private bool _isProcessing;

        public ObservableCollection<ChatMessage> Messages { get; } = new();
        public event Action? RequestClose;

        public bool IsIndexing
        {
            get => _isIndexing;
            private set { _isIndexing = value; OnPropertyChanged(); }
        }

        public string IndexingStatus
        {
            get => _indexingStatus;
            private set { _indexingStatus = value; OnPropertyChanged(); }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            private set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSend)); }
        }

        public bool CanSend => !IsProcessing && !string.IsNullOrWhiteSpace(CurrentInput);

        public string CurrentInput { get; set; } = "";

        public AiChatViewModel(MainWindow mainWindow, AiProviderConfig config)
        {
            _mainWindow = mainWindow;
            _config = config;
            _aiProvider = AiProviderFactory.CreateProvider(config.ProviderType);
        }

        /// <summary>
        /// Initializes the AI chat for the current document.
        /// </summary>
        public async Task InitializeForDocumentAsync(string filePath)
        {
            if (_currentFilePath == filePath && _currentIndex != null)
                return; // Already indexed for this document

            _currentFilePath = filePath;
            _currentDocumentId = ComputeDocumentId(filePath);
            Messages.Clear();

            await IndexDocumentAsync(filePath);
        }

        private string ComputeDocumentId(string filePath)
        {
            var info = new FileInfo(filePath);
            return $"{info.FullName}_{info.Length}_{info.LastWriteTimeUtc.Ticks}";
        }

        private async Task IndexDocumentAsync(string filePath)
        {
            IsIndexing = true;
            IndexingStatus = "Preparing document...";

            try
            {
                _currentIndex = await DocumentIndexer.CreateIndexAsync(filePath, _currentDocumentId);
                IndexingStatus = "Ready";
            }
            catch (Exception ex)
            {
                IndexingStatus = $"Indexing failed: {ex.Message}";
            }
            finally
            {
                await Task.Delay(500);
                IsIndexing = false;
            }
        }

        /// <summary>
        /// Sends a user message and gets AI response.
        /// </summary>
        public async Task SendMessageAsync(string userInput)
        {
            if (string.IsNullOrWhiteSpace(userInput) || IsProcessing || _currentIndex == null)
                return;

            var input = userInput.Trim();
            CurrentInput = "";
            OnPropertyChanged(nameof(CurrentInput));
            OnPropertyChanged(nameof(CanSend));

            // Add user message
            var userMsg = new ChatMessage
            {
                MessageRole = ChatMessage.Role.User,
                Content = input
            };
            Messages.Add(userMsg);

            // Add loading assistant message
            var assistantMsg = new ChatMessage
            {
                MessageRole = ChatMessage.Role.Assistant,
                Content = "",
                IsLoading = true
            };
            Messages.Add(assistantMsg);

            IsProcessing = true;

            try
            {
                // Retrieve relevant chunks
                var retrieved = DocumentRetriever.Retrieve(_currentIndex, input, 5);

                if (retrieved.Count == 0)
                {
                    assistantMsg.Content = "I couldn't find any relevant passages in the document for your question.";
                    assistantMsg.IsLoading = false;
                    return;
                }

                // Build system prompt
                var systemPrompt = BuildSystemPrompt();

                // Get AI response
                var response = await _aiProvider.GetChatCompletionAsync(
                    systemPrompt,
                    Messages,
                    retrieved.ConvertAll(r => r.Chunk),
                    _config);

                assistantMsg.Content = response.Answer;
                assistantMsg.Sources = response.Sources;
                assistantMsg.IsLoading = false;
            }
            catch (Exception ex)
            {
                assistantMsg.Content = $"Error: {ex.Message}";
                assistantMsg.Error = ex.Message;
                assistantMsg.IsLoading = false;
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private string BuildSystemPrompt()
        {
            return @"You are an AI assistant helping a user understand a PDF document. 
Your task is to answer questions based ONLY on the provided document context.

RULES:
1. Only use information from the provided document context.
2. If the context doesn't contain the answer, clearly state that.
3. Return your response as a JSON object with two fields:
   - ""answer"": Your response text (can include markdown for formatting)
   - ""sources"": Array of source objects, each with:
     - ""page"": The page number (1-based) from the context
     - ""quote"": Exact text from the document that supports your answer
     - ""reason"": Brief explanation of why this source is relevant

Be concise but thorough. Use markdown formatting (bold, italics, lists) when helpful.";
        }

        /// <summary>
        /// Navigates to a source page and highlights the passage.
        /// </summary>
        public void NavigateToSource(AiSource source)
        {
            if (source == null || _mainWindow == null) return;

            // Convert 1-based page number to 0-based index
            int pageIndex = source.PageNumber - 1;

            // Use the existing navigation mechanism
            _mainWindow.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                // This will be implemented in MainWindow to handle AI source navigation
                _mainWindow.NavigateToAiSource(pageIndex, source.Quote);
            });
        }

        /// <summary>
        /// Clears the current conversation and index (for document switch).
        /// </summary>
        public void ClearForDocumentSwitch()
        {
            _currentIndex = null;
            _currentFilePath = "";
            _currentDocumentId = "";
            Messages.Clear();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}