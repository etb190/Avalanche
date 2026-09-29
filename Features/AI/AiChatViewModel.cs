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
        private IAiProvider? _aiProvider;
        private string _aiProviderType = "";
        private readonly HybridRetriever _retriever;
        private readonly VectorIndex _vectorIndex;
        private readonly DocumentIndexer _indexer;
        private readonly Func<AiProviderConfig> _configProvider;
        private readonly MainWindow _mainWindow;
        private readonly Func<string, string> _loc;
        private readonly RetrievalOptions _retrievalOptions;
        
        private DocumentIndex? _currentIndex;
        private string _currentDocumentId = "";
        private string _currentFilePath = "";
        /// <summary>Bumped on every document switch so a still-running
        /// index build for the previous document can never commit stale
        /// state over the current one.</summary>
        private int _initGeneration;
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
            private set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(CanStartNewChat)); }
        }

        /// <summary>Gate for the New-chat button: a fresh conversation cannot
        /// be swept out from under a reply that is still streaming.</summary>
        public bool CanStartNewChat => !IsProcessing;

        public bool CanSend => !IsProcessing && !string.IsNullOrWhiteSpace(CurrentInput);

        public string CurrentInput 
        { 
            get => _currentInput; 
            set { _currentInput = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSend)); }
        }
        private string _currentInput = "";

        public AiChatViewModel(
            MainWindow mainWindow, 
            Func<AiProviderConfig> configProvider, 
            Func<string, string> loc)
        {
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _loc = loc ?? (k => k);

            _retrievalOptions = new RetrievalOptions
            {
                TopK = 8,
                EvidenceCharBudget = 12000,
                CandidatePoolSize = 30,
                EnableReranking = true,
                MinScore = 0.15f
            };

            _vectorIndex = new VectorIndex(GetIndexDbPath());
            _retriever = new HybridRetriever(_vectorIndex, _retrievalOptions);
            _indexer = new DocumentIndexer(_vectorIndex);

            // Inline citation footnotes inside answer bubbles route their
            // clicks through this bridge into this conversation's navigation.
            AiMarkdown.CitationClicked += OnInlineCitationClicked;
        }

        /// <summary>
        /// Resolves the provider from LIVE settings at send time. Settings
        /// were previously captured once at view model creation, so changes
        /// made in the AI settings panel never reached the chat until the
        /// app was restarted.
        /// </summary>
        private IAiProvider GetProvider(AiProviderConfig config)
        {
            if (_aiProvider is null || config.ProviderType != _aiProviderType)
            {
                _aiProvider = AiProviderFactory.CreateProvider(config.ProviderType);
                _aiProviderType = config.ProviderType ?? "";
            }
            return _aiProvider;
        }

        private void OnInlineCitationClicked(ChatMessage message, AiSource source)
        {
            if (message is null || source is null)
            {
                Avalanche.Services.AiHighlightLog.Log(
                    $"citation click: null message={message is null} source={source is null}");
                return;
            }
            if (!Messages.Contains(message))
            {
                Avalanche.Services.AiHighlightLog.Log(
                    "citation click: bubble not in current conversation - ignored");
                return; // a bubble from another conversation/viewmodel instance
            }
            Avalanche.Services.AiHighlightLog.Log(
                $"citation click: {source.SourceId} " +
                $"resolved={(source.ResolvedChunk is not null ? "yes" : "NO")} " +
                $"quote={source.Quote?.Length ?? 0}ch");
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

            int generation;
            lock (_processingLock)
            {
                if (_currentFilePath == filePath && _currentIndex != null)
                    return;

                // Claim a new generation: any index build still running for
                // the previous document is now stale and must not commit.
                generation = ++_initGeneration;
                _currentFilePath = filePath;
                _currentIndex = null;
                _currentDocumentId = DocumentIndexer.ComputeDocumentId(filePath);
            }

            // Clear conversation for new document (or could preserve per-document history)
            Application.Current.Dispatcher.Invoke(() => Messages.Clear());

            await IndexDocumentAsync(filePath, generation);
        }

        private async Task IndexDocumentAsync(string filePath, int generation)
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

                var index = await _indexer.CreateOrLoadIndexAsync(filePath, progress);

                // A newer InitializeForDocumentAsync started while this one
                // was building (rapid tab switching): discard, never clobber.
                lock (_processingLock)
                {
                    if (generation != _initGeneration)
                        return;
                }

                _currentIndex = index;
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
                IndexingStatus = DescribeIndexingFailure(ex);

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
                Application.Current.Dispatcher.Invoke(() =>
                {
                    lock (_processingLock)
                    {
                        // Only the current generation may clear the flag; a
                        // stale build finishing late must not hide the new
                        // build's progress.
                        if (generation == _initGeneration)
                            IsIndexing = false;
                    }
                });
            }
        }

        /// <summary>
        /// Maps indexing failures to user-facing text. Typed failures
        /// (scanned document, password protection) get their own friendly
        /// message instead of leaking raw PdfPig exception text.
        /// </summary>
        private string DescribeIndexingFailure(Exception ex)
        {
            if (ex is AiIndexingException aix)
            {
                return aix.Reason switch
                {
                    AiIndexingFailure.NoTextLayer => _loc("Str_AiChatNoTextLayer"),
                    AiIndexingFailure.PasswordProtected => _loc("Str_AiChatPasswordProtected"),
                    _ => $"{_loc("Str_AiChatIndexingFailed")}: {aix.Message}"
                };
            }
            return $"{_loc("Str_AiChatIndexingFailed")}: {ex.Message}";
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

                // Retrieve relevant chunks using hybrid search with TopK and
                // evidence budget. Short follow-ups ("why does he think
                // that?") are almost all pronouns and stopwords; retrieving
                // on their raw words found nothing, so the query is widened
                // with the previous user question for context.
                var retrievalQuery = BuildRetrievalQuery(input);
                var retrieved = await _retriever.RetrieveAsync(_currentDocumentId, retrievalQuery, _retrievalOptions.TopK);

                Avalanche.Services.AiHighlightLog.Log(
                    $"retrieve: '{TruncLog(retrievalQuery)}' -> {retrieved.Count} chunk(s)" +
                    (retrieved.Count > 0
                        ? $"; top: {string.Join("; ", retrieved.Take(3).Select(r => $"p{r.Chunk.PageNumber} score={r.Score:0.00} len={r.Chunk.Text?.Length ?? 0}"))}"
                        : " - the reply cannot cite anything"));

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

                // Get AI response with the CURRENT provider settings
                var config = _configProvider();
                var response = await GetProvider(config).GetChatCompletionAsync(
                    systemPrompt,
                    GetRecentMessages(),
                    retrieved.ConvertAll(r => r.Chunk),
                    sourceRefs,
                    config);

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
            {
                Avalanche.Services.AiHighlightLog.Log(
                    "resolve: model returned NO sources - inline citation markers stay inert");
                return;
            }

            // Some models number the evidence 0-based (SOURCE_0..SOURCE_{k-1})
            // even though the prompt asks for 1-based. With strict 1-based
            // parsing every one of their ids failed to resolve and the whole
            // reply's citations went inert. When EVERY returned id is a valid
            // 0-based index and at least one is 0, treat the set as 0-based -
            // the only interpretation that yields usable citations.
            int offset = 0;
            var parsedIds = sources.Select(s => AiCitations.ParseSourceId(s.SourceId)).ToList();
            if (retrieved.Count > 0 && parsedIds.Count > 0
                && parsedIds.All(v => v >= 0 && v < retrieved.Count)
                && parsedIds.Any(v => v == 0))
            {
                offset = 1;
                Avalanche.Services.AiHighlightLog.Log(
                    "resolve: model numbered sources 0-based - shifting ids up by one");
            }

            var kept = new List<AiSource>(sources.Count);
            foreach (var src in sources)
            {
                int n = AiCitations.ParseSourceId(src.SourceId) + offset;
                if (n < 1 || n > retrieved.Count)
                    continue; // invented/unknown id: drop it rather than guess

                var chunk = retrieved[n - 1].Chunk;
                // Keep the model's own spelling when it numbered 0-based: the
                // inline markers and click tags carry SOURCE_0-style ids and
                // must keep resolving to this source.
                src.SourceId = offset == 1
                    ? AiCitations.FormatId(n - offset)
                    : AiCitations.FormatId(n); // normalize spelling ("source_3" -> "SOURCE_3")
                src.ResolvedChunk = chunk;
                src.PageIndex = chunk.PageIndex;
                src.PageNumber = chunk.PageNumber;
                // Verify the model's quote against the chunk's own text;
                // unverified quotes are never used as highlight needles.
                src.QuoteVerified = PassageQuoteValidator.IsValidQuote(src.Quote, chunk.Text);
                kept.Add(src);
            }

            int dropped = sources.Count - kept.Count;
            int verified = kept.Count(s => s.QuoteVerified);
            sources.Clear();
            sources.AddRange(kept);

            Avalanche.Services.AiHighlightLog.Log(
                $"resolve: model sent {kept.Count + dropped} id(s), kept {kept.Count} " +
                $"(dropped {dropped}), quotes verified {verified}/{kept.Count}" +
                (kept.Count == 0 ? " - all ids invented/out of range, citations inert" : ""));
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

        /// <summary>First 60 characters of a query for the diagnostics log.</summary>
        private static string TruncLog(string s)
            => s.Length <= 60 ? s : s[..60] + "...";

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
            if (chunk == null)
            {
                Avalanche.Services.AiHighlightLog.Log(
                    $"navigate BAIL: source {source.SourceId} has no ResolvedChunk (unresolved or dropped)");
                return;
            }

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
            lock (_processingLock)
            {
                // Invalidate any in-flight index build or deferred reply.
                _initGeneration++;
                _currentIndex = null;
                _currentFilePath = "";
                _currentDocumentId = "";
                _pendingUserInput = null;
                _pendingPlaceholder = null;
            }
            Application.Current.Dispatcher.Invoke(() => Messages.Clear());
        }

        /// <summary>
        /// Starts a fresh conversation: clears bubbles, the input box and any
        /// deferred reply without dropping the document index, so the next
        /// question answers immediately (no re-index wait). The New-chat
        /// button is disabled (CanStartNewChat) while a reply is streaming.
        /// </summary>
        public void StartNewChat()
        {
            lock (_processingLock)
            {
                _pendingUserInput = null;
                _pendingPlaceholder = null;
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Clear();
                ClearInput();
            });
        }

        /// <summary>
        /// Called by the window when the active document changes (tab
        /// switch, new open, close). Same file: no-op. Otherwise chat,
        /// pending queue and index are cleared and the new document is
        /// indexed. ClearForDocumentSwitch previously had NO callers, so
        /// switching tabs with the panel open kept the old document's
        /// index and messages and answered from the wrong document.
        /// </summary>
        public void HandleDocumentSwitch(string? filePath)
        {
            if (_currentFilePath == filePath)
                return;

            ClearForDocumentSwitch();

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                _ = InitializeForDocumentAsync(filePath);
        }

        /// <summary>
        /// Retrieval query for the user's input. Short follow-up questions
        /// are mostly pronouns and stopwords; prepending the previous user
        /// question gives the lexical query something substantive to match
        /// without changing what the model is asked to answer.
        /// </summary>
        private string BuildRetrievalQuery(string input)
        {
            if (!LooksLikeFollowUp(input))
                return input;

            string? previous = null;
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                var m = Messages[i];
                if (m.MessageRole == ChatMessage.Role.User && !string.IsNullOrWhiteSpace(m.Content))
                {
                    previous = m.Content.Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(previous) || previous == input)
                return input;

            var combined = previous + " " + input;
            return combined.Length <= 400 ? combined : combined[..400];
        }

        /// <summary>Short questions, or ones opening with pronouns or
        /// question words, lean on the previous turn for their subject.</summary>
        private static bool LooksLikeFollowUp(string input)
        {
            var trimmed = input.Trim();
            if (trimmed.Length < 80)
                return true;

            var first = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                               .FirstOrDefault();
            if (first is null) return true;
            first = new string(first.Where(char.IsLetter).ToArray()).ToLowerInvariant();
            return first is "why" or "what" or "how" or "who" or "when" or "where"
                or "it" or "its" or "that" or "this" or "these" or "those"
                or "they" or "them" or "he" or "she" or "his" or "her"
                or "so" or "and" or "but" or "because" or "then" or "also"
                or "more" or "else" or "explain" or "continue";
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