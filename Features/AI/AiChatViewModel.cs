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
        private readonly HybridRetriever _retriever;
        private readonly VectorIndex _vectorIndex;
        private readonly DocumentIndexer _indexer;
        private readonly AiProviderConfig _genConfig;
        private readonly MainWindow _mainWindow;
        private readonly Func<string, string> _loc;
        private readonly RetrievalOptions _retrievalOptions;
        
        private DocumentIndex? _currentIndex;
        private string _currentDocumentId = "";
        private string _currentFilePath = "";
        private bool _isIndexing;
        private string _indexingStatus = "";
        private double _indexingProgress = 0.0;
        private bool _isProcessing;
        private readonly object _processingLock = new();
        private int _maxHistoryMessages = 10;
        private string? _pendingUserInput;  // Queue message if indexing not complete
        private ChatMessage? _pendingPlaceholder;  // "Preparing..." bubble shown while a queued message waits

        public ObservableCollection<ChatMessage> Messages { get; } = new();

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

        public bool CanSend => !IsProcessing && !string.IsNullOrWhiteSpace(CurrentInput);

        public string CurrentInput 
        { 
            get => _currentInput; 
            set { _currentInput = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSend)); }
        }
        private string _currentInput = "";

        public AiChatViewModel(
            MainWindow mainWindow, 
            AiProviderConfig genConfig, 
            Func<string, string> loc)
        {
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            _genConfig = genConfig ?? throw new ArgumentNullException(nameof(genConfig));
            _loc = loc ?? (k => k);

            _retrievalOptions = new RetrievalOptions
            {
                TopK = 8,
                EvidenceCharBudget = 12000,
                CandidatePoolSize = 30,
                EnableReranking = true,
                MinScore = 0.15f
            };

            _aiProvider = AiProviderFactory.CreateProvider(genConfig.ProviderType);
            _vectorIndex = new VectorIndex(GetIndexDbPath());
            _retriever = new HybridRetriever(_vectorIndex, _retrievalOptions);
            _indexer = new DocumentIndexer(_vectorIndex);

            // Inline citation footnotes inside answer bubbles route their
            // clicks through this bridge into this conversation's navigation.
            AiMarkdown.CitationClicked += OnInlineCitationClicked;
        }

        private void OnInlineCitationClicked(ChatMessage message, AiSource source)
        {
            if (message is null || source is null)
                return;
            if (!Messages.Contains(message))
                return; // a bubble from another conversation/viewmodel instance
            NavigateToSource(source);
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
                var progress = new Progress<IndexingProgress>(p =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        IndexingStatus = p.Message;
                        IndexingProgress = p.Progress;
                    });
                });

                _currentIndex = await _indexer.CreateOrLoadIndexAsync(filePath, progress);
                IndexingStatus = _loc("Str_AiChatReady");

                // Process any pending user input after indexing completes
                if (!string.IsNullOrEmpty(_pendingUserInput))
                {
                    var pendingInput = _pendingUserInput;
                    _pendingUserInput = null;
                    var placeholder = _pendingPlaceholder;
                    _pendingPlaceholder = null;
                    if (placeholder is not null)
                        Application.Current.Dispatcher.Invoke(() => Messages.Remove(placeholder));
                    _ = GenerateReplyAsync(pendingInput);
                }
            }
            catch (Exception ex)
            {
                IndexingStatus = $"{_loc("Str_AiChatIndexingFailed")}: {ex.Message}";

                // Never leave a deferred "Preparing document..." bubble stuck:
                // if a message was queued while indexing, surface the failure
                // in-chat instead of waiting for an answer that cannot come.
                var failedInput = _pendingUserInput;
                var failedPlaceholder = _pendingPlaceholder;
                _pendingUserInput = null;
                _pendingPlaceholder = null;
                if (failedInput is not null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (failedPlaceholder is not null)
                            Messages.Remove(failedPlaceholder);
                        Messages.Add(new ChatMessage
                        {
                            MessageRole = ChatMessage.Role.Assistant,
                            Content = IndexingStatus,
                            Error = IndexingStatus
                        });
                    });
                }
            }
            finally
            {
                await Task.Delay(500);
                Application.Current.Dispatcher.Invoke(() => IsIndexing = false);
            }
        }

        /// <summary>
        /// Sends a user message and gets AI response with hybrid retrieval.
        /// The user's message is added to the conversation immediately so typed
        /// text can never vanish; if the document is still being indexed the
        /// reply is deferred and a "preparing" bubble acknowledges the message.
        /// </summary>
        public async Task SendMessageAsync(string userInput)
        {
            if (string.IsNullOrWhiteSpace(userInput) || IsProcessing)
                return;

            var input = userInput.Trim();

            // Always echo the user's message into the conversation right away.
            // Previously the message was queued or dropped silently while the
            // index was building, which looked like the sent text disappeared.
            var userMsg = new ChatMessage
            {
                MessageRole = ChatMessage.Role.User,
                Content = input
            };
            Application.Current.Dispatcher.Invoke(() => Messages.Add(userMsg));
            ClearInput();

            // If indexing not complete, defer the AI reply until the index is ready
            if (_currentIndex == null)
            {
                if (IsIndexing)
                {
                    _pendingUserInput = input;
                    _pendingPlaceholder = new ChatMessage
                    {
                        MessageRole = ChatMessage.Role.Assistant,
                        Content = _loc("Str_AiChatPreparing"),
                        IsLoading = true
                    };
                    Application.Current.Dispatcher.Invoke(() => Messages.Add(_pendingPlaceholder));

                    // Indexing can complete while the placeholder is being added
                    // (the completion callback only consumes input queued before
                    // it ran) - resolve immediately so this message is never left
                    // waiting on a placeholder that nothing will replace.
                    if (_currentIndex != null)
                    {
                        var queued = _pendingUserInput;
                        var queuedPlaceholder = _pendingPlaceholder;
                        _pendingUserInput = null;
                        _pendingPlaceholder = null;
                        if (queuedPlaceholder is not null)
                            Application.Current.Dispatcher.Invoke(() => Messages.Remove(queuedPlaceholder));
                        if (!string.IsNullOrEmpty(queued))
                            _ = GenerateReplyAsync(queued);
                    }
                    return;
                }

                // No index and not indexing (e.g. indexing failed): surface the
                // problem to the user instead of silently dropping the message.
                // Prefer the detailed status (e.g. the underlying exception text).
                var failureDetail = !string.IsNullOrEmpty(IndexingStatus)
                    ? IndexingStatus
                    : _loc("Str_AiChatIndexingFailed");
                Application.Current.Dispatcher.Invoke(() => Messages.Add(new ChatMessage
                {
                    MessageRole = ChatMessage.Role.Assistant,
                    Content = failureDetail,
                    Error = failureDetail
                }));
                return;
            }

            await GenerateReplyAsync(input);
        }

        private void ClearInput()
        {
            CurrentInput = "";
            OnPropertyChanged(nameof(CurrentInput));
            OnPropertyChanged(nameof(CanSend));
        }

        /// <summary>
        /// Runs retrieval and generation for an already-displayed user message.
        /// Waits for any in-flight reply so messages are answered in order and
        /// none are dropped.
        /// </summary>
        private async Task GenerateReplyAsync(string input)
        {
            while (true)
            {
                lock (_processingLock)
                {
                    if (!IsProcessing)
                    {
                        IsProcessing = true;
                        break;
                    }
                }
                await Task.Delay(100);
            }

            // Add loading assistant message. This is the reply indicator, so
            // it must not reuse the indexing placeholder text ("Preparing
            // document...") - during the embedding stall users could not
            // tell a working reply from a stuck index build.
            var assistantMsg = new ChatMessage
            {
                MessageRole = ChatMessage.Role.Assistant,
                Content = _loc("Str_AiChatThinking"),
                IsLoading = true
            };
            Application.Current.Dispatcher.Invoke(() => Messages.Add(assistantMsg));

            try
            {
                // Greetings and other small talk need no document retrieval;
                // answering them directly avoids replying with "no matching
                // passages" to a plain "hi".
                if (IsSmallTalk(input))
                {
                    assistantMsg.Content = _loc("Str_AiChatGreeting");
                    assistantMsg.IsLoading = false;
                    return;
                }

                // Retrieve relevant chunks using hybrid search with TopK and evidence budget
                var retrieved = await _retriever.RetrieveAsync(_currentDocumentId, input, _retrievalOptions.TopK);

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
                    retrieved.ConvertAll(r => r.Chunk),
                    sourceRefs,
                    _genConfig);

                // Resolve each returned sourceId through THIS reply's evidence
                // list before anything binds to Sources: the chip row and the
                // inline footnote circles both read this list while rendering.
                ResolveSources(response.Sources, retrieved);

                assistantMsg.Sources = response.Sources; // chip row binds on this change
                assistantMsg.Content = response.Answer;  // markdown rebuild sees the sources
                assistantMsg.IsLoading = false;

                // Scroll to bottom - fire-and-forget UI update
                _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    // Scroll logic would go here
                });
            }
            catch (Exception ex)
            {
                assistantMsg.Content = MapErrorToFriendlyMessage(ex);
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

        /// <summary>
        /// Maps each SOURCE_n id the model returned back to the n-th chunk of
        /// this reply's retrieved evidence, fills the exact page data from that
        /// chunk (never from model-supplied numbers) and drops invented or
        /// out-of-range ids so every citation button points at real evidence.
        /// </summary>
        private static void ResolveSources(List<AiSource> sources, List<RetrievedChunk> retrieved)
        {
            if (sources.Count == 0)
                return;

            var kept = new List<AiSource>(sources.Count);
            foreach (var src in sources)
            {
                int n = AiCitations.ParseSourceId(src.SourceId);
                if (n < 1 || n > retrieved.Count)
                    continue; // invented/unknown id: drop it rather than guess

                var chunk = retrieved[n - 1].Chunk;
                src.SourceId = AiCitations.FormatId(n); // normalize spelling ("source_3" -> "SOURCE_3")
                src.ResolvedChunk = chunk;
                src.PageIndex = chunk.PageIndex;
                src.PageNumber = chunk.PageNumber;
                kept.Add(src);
            }

            sources.Clear();
            sources.AddRange(kept);
        }

        private string BuildSystemPrompt(List<RetrievedChunk> retrieved)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are an AI assistant helping a user understand a PDF document.");
            sb.AppendLine("Answer ONLY using the provided document evidence.");
            sb.AppendLine("If the evidence doesn't contain the answer, clearly state that.");
            sb.AppendLine("Distinguish the document's claims from your own explanation.");
            sb.AppendLine("Cite only the given SOURCE_n IDs; never invent IDs, page numbers or quotes.");
            sb.AppendLine("Quotes must be copied exactly from the cited source.");
            sb.AppendLine("Prefer several supporting sources; do not cite passages merely because they share words.");
            sb.AppendLine("Inline citations: right after each claim, append the supporting source's marker in the exact form [SOURCE_n] using plain ASCII square brackets.");
            sb.AppendLine("Example: 'The trial lasted twelve weeks. [SOURCE_2]'.");
            sb.AppendLine("Use [SOURCE_n] only - never full-width brackets like \u3010SOURCE_n\u3011, never (SOURCE_n).");
            sb.AppendLine("Every source listed in 'sources' must also appear as an inline [SOURCE_n] marker in the answer.");
            sb.AppendLine();
            sb.AppendLine("EVIDENCE FORMAT:");
            sb.AppendLine("[SOURCE_1] Page 147 (section: ...)");
            sb.AppendLine("<text>");
            sb.AppendLine();
            sb.AppendLine("RETRIEVED EVIDENCE:");
            sb.AppendLine();

            for (int i = 0; i < retrieved.Count; i++)
            {
                var chunk = retrieved[i].Chunk;
                sb.AppendLine($"[SOURCE_{i + 1}] Page {chunk.PageNumber}{(string.IsNullOrEmpty(chunk.SectionHeading) ? "" : $" (section: {chunk.SectionHeading})")}");
                sb.AppendLine(chunk.Text);
                sb.AppendLine();
            }

            sb.AppendLine("INSTRUCTIONS:");
            sb.AppendLine("1. Answer based ONLY on the provided sources above.");
            sb.AppendLine("2. If sources don't contain the answer, say: 'The document does not contain information about this.'");
            sb.AppendLine("3. Return JSON with 'answer' and 'sources' fields.");
            sb.AppendLine("4. Each source must include: 'sourceId' (use the SOURCE_n ID above), 'quote' (exact text from source), 'reason' (why it supports the answer).");
            sb.AppendLine("5. Use the EXACT sourceId from the evidence (e.g., 'SOURCE_1', 'SOURCE_2').");
            sb.AppendLine("6. Do NOT invent page numbers or source IDs.");
            sb.AppendLine("7. Cite multiple sources when appropriate.");
            sb.AppendLine("8. Distinguish the document's claims from your explanation.");
            sb.AppendLine("Output ONLY a JSON object:");
            sb.AppendLine("{\"answer\": \"The fee doubles after the first year. [SOURCE_1]\", \"sources\": [{\"sourceId\": \"SOURCE_1\", \"quote\": \"<exact text from that source>\", \"reason\": \"<why it supports the answer>\"}]}");
            sb.AppendLine("(Do not ask the model for page numbers; the app derives them from sourceId.)");

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
                sb.AppendLine($"SOURCE_{i + 1}");
                sb.AppendLine($"Page: {chunk.PageNumber}");
                sb.AppendLine($"Text: {chunk.Text}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private List<ChatMessage> GetRecentMessages()
        {
            return Messages
                .Where(m => m.MessageRole != ChatMessage.Role.System
                            && !m.IsLoading
                            && string.IsNullOrWhiteSpace(m.Error))
                .TakeLast(_maxHistoryMessages)
                .ToList();
        }

        /// <summary>
        /// Navigates to a source using the chunk that was resolved from THIS
        /// reply's retrieved evidence when the response was parsed.
        /// </summary>
        public void NavigateToSource(AiSource source)
        {
            if (source == null || _mainWindow == null) return;

            // SOURCE_n refers to the n-th chunk of this reply's evidence list,
            // not to the document's global chunk list; that mapping was captured
            // in ResolvedChunk at parse time. Without it there is no trustworthy
            // location to show, so an unresolved citation stays inert.
            var chunk = source.ResolvedChunk;
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
            _pendingUserInput = null;
            _pendingPlaceholder = null;
            Application.Current.Dispatcher.Invoke(() => Messages.Clear());
        }

        /// <summary>
        /// Detects greetings and other small talk that need no document retrieval.
        /// </summary>
        private static bool IsSmallTalk(string input)
        {
            var s = input.Trim().ToLowerInvariant();
            if (s.Length == 0 || s.Length > 32 || s.Contains('?'))
                return false;

            var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Length > 3)
                return false;

            var cleaned = string.Join(" ",
                words.Select(w => new string(w.Where(char.IsLetter).ToArray())));
            if (string.IsNullOrWhiteSpace(cleaned))
                return false;

            string[] greetings =
            {
                "hi", "hello", "hey", "yo", "hiya", "howdy", "sup", "greetings",
                "good morning", "good afternoon", "good evening", "morning",
                "hi there", "hello there", "hey there", "how are you",
                "how is it going", "hows it going", "whats up"
            };
            return greetings.Contains(cleaned);
        }

        private string MapErrorToFriendlyMessage(Exception ex)
        {
            var message = ex.Message?.ToLowerInvariant() ?? "";
            
            // Connection refused / timeout on localhost
            if (ex is System.Net.Http.HttpRequestException hre)
            {
                if (message.Contains("connection refused") || message.Contains("timeout") || message.Contains("unreachable"))
                    return _loc("Str_AiErrorOllamaNotRunning");
                if (message.Contains("401") || message.Contains("unauthorized") || message.Contains("sign in"))
                    return _loc("Str_AiErrorNotSignedIn");
                if (message.Contains("404") || message.Contains("not found"))
                    return _loc("Str_AiErrorModelNotFound");
                if (message.Contains("429") || message.Contains("503") || message.Contains("queue") || message.Contains("busy"))
                    return _loc("Str_AiErrorBusy");
                if (message.Contains("usage") || message.Contains("credit") || message.Contains("limit"))
                    return _loc("Str_AiErrorUsageLimit");
            }
            
            // Check for empty content with finish_reason = length
            if (message.Contains("cut off") || message.Contains("length"))
                return _loc("Str_AiErrorCutOff");

            // Generic fallback
            return _loc("Str_AiChatError");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}