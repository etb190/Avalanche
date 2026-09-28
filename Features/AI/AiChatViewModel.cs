using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Avalanche.Services;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// ViewModel for the AI Chat sidebar with conversation memory and hybrid retrieval.
    /// </summary>
    public sealed class AiChatViewModel : INotifyPropertyChanged
    {
        private readonly IAiProvider _aiProvider;
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly HybridRetriever _retriever;
        private readonly VectorIndex _vectorIndex;
        private readonly AiProviderConfig _genConfig;
        private readonly EmbeddingProviderConfig _embConfig;
        private readonly MainWindow _mainWindow;
        private readonly Func<string, string> _loc;
        
        private DocumentIndex? _currentIndex;
        private string _currentDocumentId = "";
        private string _currentFilePath = "";
        private bool _isIndexing;
        private string _indexingStatus = "";
        private double _indexingProgress = 0.0;
        private bool _isProcessing;
        private readonly object _processingLock = new();
        private int _maxHistoryMessages = 10;

        public ObservableCollection<ChatMessage> Messages { get; } = new();
        public event Action? RequestClose;

        public bool IsIndexing
        {
            get => _isIndexing;
            private set { _isIndexing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSend)); }
        }

        public string IndexingStatus
        {
            get => _indexingStatus;
            private set { _indexingStatus = value; OnPropertyChanged(); }
        }

        public double IndexingProgress
        {
            get => _indexingProgress;
            private set { _indexingProgress = value; OnPropertyChanged(); }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            private set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSend)); }
        }

        public bool CanSend => !IsProcessing && !IsIndexing && !string.IsNullOrWhiteSpace(CurrentInput);

        public string CurrentInput { get; set; } = "";

        public AiChatViewModel(
            MainWindow mainWindow, 
            AiProviderConfig genConfig, 
            EmbeddingProviderConfig embConfig,
            Func<string, string> loc)
        {
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            _genConfig = genConfig ?? throw new ArgumentNullException(nameof(genConfig));
            _embConfig = embConfig ?? throw new ArgumentNullException(nameof(embConfig));
            _loc = loc ?? (k => k);

            _aiProvider = AiProviderFactory.CreateProvider(genConfig.ProviderType);
            _embeddingProvider = AiProviderFactory.CreateEmbeddingProvider(embConfig);
            _vectorIndex = new VectorIndex(GetIndexDbPath());
            _retriever = new HybridRetriever(_vectorIndex, _embeddingProvider);
        }

        private static string GetIndexDbPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "Avalanche", "AI");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "vector_index.db");
        }

        /// <summary>
        /// Initializes the AI chat for the current document.
        /// </summary>
        public async Task InitializeForDocumentAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            lock (_processingLock)
            {
                if (_currentFilePath == filePath && _currentIndex != null)
                    return;
            }

            _currentFilePath = filePath;
            _currentDocumentId = ComputeDocumentId(filePath);

            // Clear conversation for new document (or could preserve per-document history)
            Application.Current.Dispatcher.Invoke(() => Messages.Clear());

            await IndexDocumentAsync(filePath);
        }

        private string ComputeDocumentId(string filePath)
        {
            var info = new FileInfo(filePath);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var input = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return "doc_" + Convert.ToHexString(hash).Substring(0, 16);
        }

        private async Task IndexDocumentAsync(string filePath)
        {
            IsIndexing = true;
            IndexingStatus = _loc("Str_AiChatPreparing");
            IndexingProgress = 0.0;

            try
            {
                var indexer = new DocumentIndexer(_embeddingProvider, _vectorIndex);
                
                var progress = new Progress<IndexingProgress>(p =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        IndexingStatus = p.Message;
                        IndexingProgress = p.Progress;
                    });
                });

                _currentIndex = await indexer.CreateOrLoadIndexAsync(filePath, progress);
                IndexingStatus = _loc("Str_AiChatReady");
            }
            catch (Exception ex)
            {
                IndexingStatus = $"{_loc("Str_AiChatIndexingFailed")}: {ex.Message}";
            }
            finally
            {
                await Task.Delay(500);
                Application.Current.Dispatcher.Invoke(() => IsIndexing = false);
            }
        }

        /// <summary>
        /// Sends a user message and gets AI response with hybrid retrieval.
        /// </summary>
        public async Task SendMessageAsync(string userInput)
        {
            if (string.IsNullOrWhiteSpace(userInput) || IsProcessing || _currentIndex == null)
                return;

            lock (_processingLock)
            {
                if (IsProcessing) return;
                IsProcessing = true;
            }

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
            Application.Current.Dispatcher.Invoke(() => Messages.Add(userMsg));

            // Add loading assistant message
            var assistantMsg = new ChatMessage
            {
                MessageRole = ChatMessage.Role.Assistant,
                Content = "",
                IsLoading = true
            };
            Application.Current.Dispatcher.Invoke(() => Messages.Add(assistantMsg));

            try
            {
                // Retrieve relevant chunks using hybrid search
                var retrieved = await _retriever.RetrieveAsync(_currentDocumentId, input, _genConfig.MaxTokens / 500);

                if (retrieved.Count == 0)
                {
                    assistantMsg.Content = _loc("Str_AiChatNoMatches");
                    assistantMsg.IsLoading = false;
                    return;
                }

                // Build system prompt with structured citation instructions
                var systemPrompt = BuildSystemPrompt(retrieved);

                // Prepare available sources for the model
                var sourceRefs = BuildSourceReferences(retrieved);

                // Get AI response
                var response = await _aiProvider.GetChatCompletionAsync(
                    systemPrompt,
                    GetRecentMessages(),
                    retrieved,
                    sourceRefs,
                    _genConfig);

                assistantMsg.Content = response.Answer;
                assistantMsg.Sources = response.Sources;
                assistantMsg.IsLoading = false;

                // Scroll to bottom
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    // Scroll logic would go here
                });
            }
            catch (Exception ex)
            {
                assistantMsg.Content = $"{_loc("Str_AiChatError")} {ex.Message}";
                assistantMsg.Error = ex.Message;
                assistantMsg.IsLoading = false;
            }
            finally
            {
                lock (_processingLock)
                {
                    IsProcessing = false;
                }
            }
        }

        private string BuildSystemPrompt(List<RetrievedChunk> retrieved)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are an AI assistant helping a user understand a PDF document.");
            sb.AppendLine("Answer ONLY using the provided document evidence.");
            sb.AppendLine("If the evidence doesn't contain the answer, clearly state that.");
            sb.AppendLine();
            sb.AppendLine("RETRIEVED EVIDENCE:");
            sb.AppendLine();

            for (int i = 0; i < retrieved.Count; i++)
            {
                var chunk = retrieved[i].Chunk;
                sb.AppendLine($"--- SOURCE {i} (ID: {chunk.ChunkId}) ---");
                sb.AppendLine($"Page: {chunk.PageNumber}");
                if (!string.IsNullOrEmpty(chunk.SectionHeading))
                    sb.AppendLine($"Section: {chunk.SectionHeading}");
                sb.AppendLine($"Text: {chunk.Text}");
                sb.AppendLine();
            }

            sb.AppendLine("INSTRUCTIONS:");
            sb.AppendLine("1. Answer based ONLY on the provided sources above.");
            sb.AppendLine("2. If sources don't contain the answer, say: 'The document does not contain information about this.'");
            sb.AppendLine("3. Return JSON with 'answer' and 'sources' fields.");
            sb.AppendLine("4. Each source must include: 'sourceId' (use the SOURCE X ID above), 'page', 'quote' (exact text from source), 'reason'.");
            sb.AppendLine("5. Use the EXACT sourceId from the evidence (e.g., 'SOURCE_0').");
            sb.AppendLine("6. Do NOT invent page numbers or source IDs.");
            sb.AppendLine("7. Cite multiple sources when appropriate.");
            sb.AppendLine("8. Distinguish the document's claims from your explanation.");

            return sb.ToString();
        }

        private string BuildSourceReferences(List<RetrievedChunk> retrieved)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("AVAILABLE SOURCES:");
            sb.AppendLine();

            for (int i = 0; i < retrieved.Count; i++)
            {
                var chunk = retrieved[i].Chunk;
                sb.AppendLine($"SOURCE_{i}");
                sb.AppendLine($"Page: {chunk.PageNumber}");
                sb.AppendLine($"Text: {chunk.Text}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private List<ChatMessage> GetRecentMessages()
        {
            return Messages
                .Where(m => m.MessageRole != ChatMessage.Role.System)
                .TakeLast(_maxHistoryMessages)
                .ToList();
        }

        /// <summary>
        /// Navigates to a source using exact coordinates from retrieval.
        /// </summary>
        public void NavigateToSource(AiSource source)
        {
            if (source == null || _mainWindow == null) return;

            // Use the exact source ID to find the chunk
            if (string.IsNullOrEmpty(source.SourceId)) return;

            var chunk = _currentIndex?.Chunks.FirstOrDefault(c => c.ChunkId == source.SourceId);
            if (chunk == null) return;

            // Navigate to the page and highlight using exact coordinates
            _mainWindow.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                _mainWindow.NavigateToAiSource(chunk, source);
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
            Application.Current.Dispatcher.Invoke(() => Messages.Clear());
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}