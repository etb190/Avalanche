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
        private readonly OllamaEmbeddingClient _embeddingClient;
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
        private string _semanticStatus = "";
        private CancellationTokenSource? _indexingCts;
        private bool _isProcessing;
        private readonly object _processingLock = new();
        private int _maxHistoryMessages = 6;
        private string? _pendingUserInput;  // Queue message if indexing not complete
        private ChatMessage? _pendingPlaceholder;  // "Preparing..." bubble shown while a queued message waits

        /// <summary>Cancels the in-flight reply (user Stop button or document
        /// switch). Replaced for every new reply.</summary>
        private CancellationTokenSource? _replyCts;

        /// <summary>Chat transcript per document id - restored when the user
        /// switches back to a document (C6). Entries are snapshots without
        /// loading placeholders or error bubbles.</summary>
        private readonly Dictionary<string, List<ChatMessage>> _historyByDocument = new();

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

        /// <summary>One-line status of the OPTIONAL semantic layer: building,
        /// ready, embedding model missing, or keyword-only fallback. Never
        /// gates the chat - it only explains search quality (D3).</summary>
        public string SemanticStatus
        {
            get => _semanticStatus;
            private set { _semanticStatus = value; OnPropertyChanged(); }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            private set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(CanStartNewChat)); }
        }

        /// <summary>Gate for the New-chat button: a fresh conversation cannot
        /// be swept out from under a reply that is still streaming.</summary>
        public bool CanStartNewChat => !IsProcessing;

        public bool CanSend => !IsProcessing && !IsIndexing && !string.IsNullOrWhiteSpace(CurrentInput);

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
                TopK = 24,
                EvidenceCharBudget = 40000,
                CandidatePoolSize = 80,
                EnableReranking = true,
                MinScore = 0.15f
            };

            _vectorIndex = new VectorIndex(GetIndexDbPath());
            _embeddingClient = new OllamaEmbeddingClient(_configProvider);
            _retriever = new HybridRetriever(_vectorIndex, _embeddingClient, _retrievalOptions);
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

            bool restored = false;
            lock (_processingLock)
            {
                // Same document already indexed OR still being indexed: keep
                // the existing build instead of restarting it - previously a
                // reopen while indexing bumped the generation, cleared the
                // messages and started a SECOND concurrent build/embed pass.
                if (_currentFilePath == filePath && (_currentIndex != null || IsIndexing))
                    return;

                // Claim a new generation: any index build still running for
                // the previous document is now stale and must not commit.
                _initGeneration++;
                int generation = _initGeneration;
                _currentFilePath = filePath;
                _currentIndex = null;
                _currentDocumentId = DocumentIndexer.ComputeDocumentId(filePath);

                // Restore this document's saved transcript, if any (C6).
                restored = _historyByDocument.ContainsKey(_currentDocumentId);
            }

            var docId = _currentDocumentId;
            Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Clear();
                if (restored && _historyByDocument.TryGetValue(docId, out var saved))
                {
                    foreach (var m in saved)
                        Messages.Add(m);
                }
            });

            await IndexDocumentAsync(filePath, _initGeneration);
        }

        private async Task IndexDocumentAsync(string filePath, int generation)
        {
            IsIndexing = true;
            IndexingStatus = _loc("Str_AiChatPreparing");
            IndexingProgress = 0.0;
            var indexingCts = new CancellationTokenSource();
            lock (_processingLock)
            {
                _indexingCts?.Dispose();
                _indexingCts = indexingCts;
            }

            try
            {
                var progress = new Progress<IndexingProgress>(p =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // The indexer's English Message is diagnostics-only;
                        // the panel shows the STAGE localized, with counters
                        // (F3) - hardcoded progress text never reaches users.
                        IndexingStatus = LocalizeIndexingStage(p);
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

                // Publish the lexical index FIRST (D1): the embedding pass is
                // optional and can run long, but the document is answerable
                // the moment its BM25 index exists.
                lock (_processingLock)
                {
                    if (generation != _initGeneration)
                        return;
                    _currentIndex = index;
                }
                IndexingStatus = _loc("Str_AiChatReady");

                // Partially scanned PDFs: pages without a text layer are
                // silently unsearchable - say so instead of hiding it (D8).
                int pagesWithText = index.Chunks.SelectMany(c => c.PageIndices).Distinct().Count();
                int textlessPages = Math.Max(0, index.PageCount - pagesWithText);
                if (textlessPages > 0)
                    SemanticStatus = string.Format(_loc("Str_AiChatPartialTextLayer"), textlessPages);

                // Consume anything queued while indexing ran.
                lock (_processingLock)
                {
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

                // Semantic layer: embed in the BACKGROUND so readiness never
                // waited on it (D1). Failures degrade search to keyword-only
                // and surface as a one-line localized status - they are never
                // fatal for the chat (D3).
                var embeddingModel = _configProvider().EmbeddingModel;
                var documentPrefix = _configProvider().EmbeddingDocumentPrefix;
                var queryPrefix = _configProvider().EmbeddingQueryPrefix;
                SemanticStatus = _loc("Str_AiChatSemanticBuilding");

                // Live per-batch progress for the semantic status line: the
                // IndexingStatus row goes dark once IsIndexing clears, so a
                // background pass used to show a FROZEN "building..." for its
                // whole duration - indistinguishable from a hang (regression
                // report: "wont go away"). Now every batch updates the line.
                var embedProgress = new FanOutProgress(progress, p =>
                {
                    if (p.Stage != IndexingStage.Embedding || p.Total <= 0) return;
                    try
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                            SemanticStatus = string.Format(_loc("Str_AiChatSemanticBuildingProgress"), p.Done, p.Total));
                    }
                    catch { /* app shutting down */ }
                });

                _ = Task.Run(async () =>
                {
                    CancellationToken passToken = default;
                    var passStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    int chunkCount = index.Chunks.Count;
                    bool staleStatus = false;

                    // One full embedding attempt. Success publishes the ready
                    // status; failures propagate to the handlers below.
                    async Task AttemptAsync()
                    {
                        await _indexer.EnsureEmbeddingsAsync(index,
                            (texts, ct) => _embeddingClient.GenerateEmbeddingsAsync(texts, ct),
                            embeddingModel, embedProgress, passToken,
                            documentPrefix, queryPrefix);

                        lock (_processingLock)
                        {
                            if (generation != _initGeneration)
                            {
                                staleStatus = true;
                                return;
                            }
                        }
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (textlessPages > 0)
                                SemanticStatus = string.Format(_loc("Str_AiChatPartialTextLayer"), textlessPages) +
                                                 " " + _loc("Str_AiChatSemanticReady");
                            else
                                SemanticStatus = _loc("Str_AiChatSemanticReady");
                        });
                        Avalanche.Services.AiHighlightLog.Log(
                            $"embedding pass DONE in {passStopwatch.ElapsedMilliseconds}ms: {chunkCount} chunk(s), semantic channel ready");
                    }

                    // NO total pass deadline. The previous 10-minute CancelAfter
                    // murdered HEALTHY passes on slow machines: CPU-only Ollama
                    // needs more than ten minutes for a large PDF, the pass died
                    // mid-flight, the semantic channel never came up and every
                    // answer degraded to keyword-only ("not as detailed as they
                    // used to be"). The pass is already bounded where it matters:
                    // probe 15s, each batch 60s (split-retried when a single
                    // batch alone cannot fit), so a wedged endpoint surfaces as
                    // a failure within minutes - while a slow one runs to
                    // completion with live progress above.
                    try
                    {
                        passToken = indexingCts.Token;
                        Avalanche.Services.AiHighlightLog.Log(
                            $"embedding pass START: gen={generation} chunks={chunkCount} model={embeddingModel}");
                        await AttemptAsync();
                    }
                    catch (OperationCanceledException) when (passToken.IsCancellationRequested)
                    {
                        // Document switched (or panel re-bound): the pass was
                        // cancelled - previously this exited SILENTLY and left
                        // the status line stuck on "building" forever.
                        staleStatus = true;
                        Avalanche.Services.AiHighlightLog.Log(
                            $"embedding pass CANCELLED after {passStopwatch.ElapsedMilliseconds}ms (document switch, gen={generation})");
                    }
                    catch (OperationCanceledException)
                    {
                        // Batch deadlines survived every split (the endpoint
                        // cannot embed even a handful of passages in time):
                        // visible keyword-only state instead of an eternal wait.
                        Avalanche.Services.AiHighlightLog.Log(
                            $"embedding pass TIMEOUT after {passStopwatch.ElapsedMilliseconds}ms - endpoint never finished; keyword-only mode");
                        try
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                                SemanticStatus = _loc("Str_AiChatEmbeddingsUnavailable"));
                        }
                        catch { /* app shutting down */ }
                    }
                    catch (AiProviderException pex) when (pex.Category == AiErrorCategory.ModelNotFound)
                    {
                        Avalanche.Services.AiHighlightLog.Log(
                            $"embedding pass FAILED in {passStopwatch.ElapsedMilliseconds}ms: model missing ({pex.ModelName})");
                        Application.Current.Dispatcher.Invoke(() =>
                            SemanticStatus = string.Format(_loc("Str_AiChatEmbeddingModelMissing"), pex.ModelName ?? embeddingModel));
                    }
                    catch (Exception embedEx)
                    {
                        // Transient endpoint trouble (Ollama cold-starting the
                        // model, user restarting it): ONE delayed retry after
                        // the probe cool-down. Cold model loads exceed the 15s
                        // probe window on slow disks, and the pass used to die
                        // permanently on exactly that - the semantic channel
                        // never recovered until the next app start.
                        Avalanche.Services.AiHighlightLog.Log(
                            $"embedding pass FAILED after {passStopwatch.ElapsedMilliseconds}ms ({embedEx.GetType().Name}: {TruncLog(embedEx.Message)}) - retrying once in 65s");

                        var outcome = await RetryEmbeddingOnceAsync(
                            passToken, generation, AttemptAsync,
                            stale => staleStatus |= stale);

                        if (outcome == SemanticRetryOutcome.ModelMissing)
                        {
                            string missingModel = embeddingModel;
                            if (embedEx is AiProviderException firstTyped && !string.IsNullOrEmpty(firstTyped.ModelName))
                                missingModel = firstTyped.ModelName;
                            else if (embedEx.InnerException is AiProviderException innerTyped && !string.IsNullOrEmpty(innerTyped.ModelName))
                                missingModel = innerTyped.ModelName;
                            Application.Current.Dispatcher.Invoke(() =>
                                SemanticStatus = string.Format(_loc("Str_AiChatEmbeddingModelMissing"), missingModel));
                        }
                        else if (outcome == SemanticRetryOutcome.Unavailable)
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                                SemanticStatus = _loc("Str_AiChatEmbeddingsUnavailable"));
                        }
                        // Ready / Cancelled / Stale need no message here: Ready
                        // was published by the attempt itself, Cancelled/Stale
                        // clear the line in the finally below.
                    }
                    finally
                    {
                        // A cancelled/orphaned pass must not leave "building"
                        // on the status line: clear it when this generation
                        // no longer owns the panel.
                        if (staleStatus)
                        {
                            try
                            {
                                Application.Current.Dispatcher.Invoke(() =>
                                {
                                    if (SemanticStatus == _loc("Str_AiChatSemanticBuilding"))
                                        SemanticStatus = "";
                                });
                            }
                            catch { /* app shutting down */ }
                        }
                    }
                });

                // Nothing else to publish here: the lexical index went live
                // BEFORE the embedding pass, so readiness never waited on it.
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
                        try { indexingCts.Dispose(); } catch { }
                        if (_indexingCts == indexingCts) _indexingCts = null;
                    }
                });
            }
        }

        /// <summary>Outcome of the single delayed embedding retry.</summary>
        private enum SemanticRetryOutcome
        {
            Ready,        // retry succeeded (or the pass went stale) - no message
            Cancelled,    // document switched while waiting - no message
            Stale,        // generation superseded during the wait - no message
            Unavailable,  // endpoint still failing - show keyword-only line
            ModelMissing  // model absent - show the "ollama pull" line
        }

        /// <summary>Waits out the probe cool-down (65s) and retries the whole
        /// embedding pass ONCE. Gives Ollama time to finish a cold model load
        /// that outran the 15s probe window; a still-dead endpoint ends in the
        /// visible keyword-only state instead of retrying forever.</summary>
        private async Task<SemanticRetryOutcome> RetryEmbeddingOnceAsync(
            CancellationToken passToken,
            int generation,
            Func<Task> attempt,
            Action<bool> markStale)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(65), passToken);
            }
            catch (OperationCanceledException)
            {
                markStale(true);
                return SemanticRetryOutcome.Cancelled;
            }

            lock (_processingLock)
            {
                if (generation != _initGeneration)
                {
                    markStale(true);
                    return SemanticRetryOutcome.Stale;
                }
            }

            try
            {
                await attempt();
                return SemanticRetryOutcome.Ready;
            }
            catch (OperationCanceledException) when (passToken.IsCancellationRequested)
            {
                markStale(true);
                return SemanticRetryOutcome.Cancelled;
            }
            catch (OperationCanceledException)
            {
                return SemanticRetryOutcome.Unavailable;
            }
            catch (AiProviderException pex) when (pex.Category == AiErrorCategory.ModelNotFound)
            {
                return SemanticRetryOutcome.ModelMissing;
            }
            catch (Exception retryEx)
            {
                Avalanche.Services.AiHighlightLog.Log(
                    $"embedding pass RETRY FAILED ({retryEx.GetType().Name}: {TruncLog(retryEx.Message)}) - lexical-only mode");
                return SemanticRetryOutcome.Unavailable;
            }
        }

        /// <summary>Forwards every report to the primary sink (the indexing
        /// progress row) AND runs the extra action - used so the background
        /// embedding pass can drive BOTH the hidden progress row and the
        /// visible semantic status line from one report stream.</summary>
        private sealed class FanOutProgress : IProgress<IndexingProgress>
        {
            private readonly IProgress<IndexingProgress>? _primary;
            private readonly Action<IndexingProgress>? _additional;

            public FanOutProgress(IProgress<IndexingProgress>? primary, Action<IndexingProgress>? additional)
            {
                _primary = primary;
                _additional = additional;
            }

            public void Report(IndexingProgress value)
            {
                _primary?.Report(value);
                _additional?.Invoke(value);
            }
        }

        /// <summary>
        /// Maps indexing failures to user-facing text. Typed failures
        /// (scanned document, password protection) get their own friendly
        /// message instead of leaking raw PdfPig exception text.
        /// </summary>
        private string LocalizeIndexingStage(IndexingProgress p) => p.Stage switch
        {
            IndexingStage.Loaded => _loc("Str_AiIdxLoaded"),
            IndexingStage.Persisting => _loc("Str_AiIdxPersisting"),
            IndexingStage.Complete => _loc("Str_AiIdxComplete"),
            IndexingStage.Embedding => string.Format(_loc("Str_AiIdxEmbedding"), p.Done, p.Total),
            IndexingStage.Extracting when p.Total > 0 => string.Format(_loc("Str_AiIdxExtractPage"), p.Done, p.Total),
            IndexingStage.Extracting => _loc("Str_AiIdxExtracting"),
            _ => _loc("Str_AiChatPreparing")
        };

        private string DescribeIndexingFailure(Exception ex)
        {
            if (ex is AiIndexingException aix)
            {
                return aix.Reason switch
                {
                    AiIndexingFailure.NoTextLayer => _loc("Str_AiChatNoTextLayer"),
                    AiIndexingFailure.PasswordProtected => _loc("Str_AiChatPasswordProtected"),
                    _ => _loc("Str_AiChatIndexingFailed")
                };
            }
            return _loc("Str_AiChatIndexingFailed");
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

                // No index and not indexing: either the panel was opened with
                // no document at all, or indexing failed. Tell the user which.
                if (string.IsNullOrEmpty(_currentFilePath))
                {
                    var hint = _loc("Str_AiChatNoDocument");
                    Application.Current.Dispatcher.Invoke(() => Messages.Add(new ChatMessage
                    {
                        MessageRole = ChatMessage.Role.Assistant,
                        Content = hint
                    }));
                    return;
                }

                // Indexing failed earlier: surface the problem instead of
                // silently dropping the message.
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
        /// Only one reply can run at a time; the reply runs on its own
        /// CancellationTokenSource so the user (or a document switch) can
        /// cancel it - a cancelled reply removes the loading bubble without
        /// showing an error.
        /// </summary>
        private async Task GenerateReplyAsync(string input)
        {
            var cts = new CancellationTokenSource();
            CancellationToken ct;
            int generation;
            lock (_processingLock)
            {
                if (IsProcessing)
                {
                    cts.Dispose();
                    return; // serialized by the caller paths; never queue a second reply
                }
                IsProcessing = true;
                _replyCts?.Dispose();
                _replyCts = cts;
                ct = cts.Token;
                generation = _initGeneration;
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
                // Refresh configurable retrieval settings + semantic gate keys
                // from live config (D2/D4).
                RefreshRetrievalOptions();

                var retrievalQuery = BuildRetrievalQuery(input);
                var retrieved = await _retriever.RetrieveAsync(_currentDocumentId, retrievalQuery, _retrievalOptions.TopK, ct);

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

                // Evidence is embedded ONCE (inside the system prompt). A
                // second AVAILABLE SOURCES copy previously doubled the payload
                // (D6).
                string sourceRefs = "";

                // Get AI response with the CURRENT provider settings
                var config = _configProvider();
                var response = await GetProvider(config).GetChatCompletionAsync(
                    systemPrompt,
                    GetRecentMessages(),
                    retrieved.ConvertAll(r => r.Chunk),
                    sourceRefs,
                    config,
                    ct);

                // A document switch while the request ran must not write the
                // old document's answer into the new document's conversation.
                lock (_processingLock)
                {
                    if (generation != _initGeneration)
                    {
                        Application.Current.Dispatcher.Invoke(() => Messages.Remove(assistantMsg));
                        return;
                    }
                }

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
            catch (OperationCanceledException)
            {
                // User cancel (Stop button) or document switch: the loading
                // bubble disappears without an error bubble.
                Application.Current.Dispatcher.Invoke(() => Messages.Remove(assistantMsg));
            }
            catch (Exception ex)
            {
                // The error box is the single surface: Content stays empty so
                // the failure is not shown twice, and only the localized
                // friendly message lands in Error - raw exception text is
                // never bound to the UI.
                assistantMsg.Content = "";
                assistantMsg.Error = MapErrorToFriendlyMessage(ex);
                assistantMsg.IsLoading = false;
            }
            finally
            {
                lock (_processingLock)
                {
                    if (_replyCts == cts)
                    {
                        _replyCts.Dispose();
                        _replyCts = null;
                    }
                    IsProcessing = false;
                }
            }
        }

        /// <summary>Cancels the in-flight reply, if any (Stop button).</summary>
        public void CancelReply()
        {
            lock (_processingLock)
            {
                try { _replyCts?.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>Releases the DB connection, HTTP clients and the static
        /// citation-click subscription (which was never removed before, so a
        /// closed window's VM kept responding to clicks).</summary>
        public void Dispose()
        {
            AiMarkdown.CitationClicked -= OnInlineCitationClicked;
            CancelReply();
            _vectorIndex.Dispose();
            _embeddingClient.Dispose();
            (_aiProvider as IDisposable)?.Dispose();
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
            var seenIds = new HashSet<int>();
            foreach (var src in sources)
            {
                int n = AiCitations.ParseSourceId(src.SourceId) + offset;
                if (n < 1 || n > retrieved.Count)
                    continue; // invented/unknown id: drop it rather than guess
                if (!seenIds.Add(n))
                    continue; // repeated sourceId: keep only the first occurrence (E11)

                var chunk = retrieved[n - 1].Chunk;
                // Keep the model's own spelling when it numbered 0-based: the
                // inline markers and click tags carry SOURCE_0-style ids and
                // must keep resolving to this source.
                src.SourceId = offset == 1
                    ? AiCitations.FormatId(n - offset)
                    : AiCitations.FormatId(n); // normalize spelling ("source_3" -> "SOURCE_3")
                src.ResolvedChunk = chunk;

                // Locate the quote INSIDE the cited chunk (never elsewhere):
                // the located page becomes the citation's page (a quote on the
                // chunk's 2nd page navigates to the 2nd page - E3) and the
                // location state decides whether highlighting is allowed (E2).
                var hit = QuoteLocator.LocateInChunk(chunk, src.Quote);
                src.Location = hit.State;
                src.QuoteVerified = hit.State != AiQuoteLocation.Unlocated;
                if (hit.State != AiQuoteLocation.Unlocated)
                {
                    src.PageIndex = hit.PageIndex;
                    src.PageNumber = hit.PageIndex + 1;
                }
                else
                {
                    // No highlight later; navigation still lands on the chunk's
                    // first page and the UI shows the "could not locate" status.
                    src.PageIndex = chunk.PageIndex;
                    src.PageNumber = chunk.PageNumber;
                }
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
            sb.AppendLine("ANSWER STYLE:");
            sb.AppendLine("Write a thorough, well-structured answer that fully covers what the evidence says about the question.");
            sb.AppendLine("Include every distinct aspect, mechanism, technique, step, or example the evidence provides; never compress the answer into a single short sentence when the evidence supports more.");
            sb.AppendLine("When there are several distinct points, present them as short paragraphs or a bulleted list ('- '), each point carrying its own inline [SOURCE_n] citation.");
            sb.AppendLine("Briefly explain terms or context the document uses when that aids understanding, staying grounded in the evidence.");
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

            return sb.ToString();
        }

        private List<ChatMessage> GetRecentMessages()
        {
            return Messages
                .Where(m => m.MessageRole != ChatMessage.Role.System
                            && !m.IsLoading
                            && string.IsNullOrWhiteSpace(m.Error))
                .TakeLast(_maxHistoryMessages)
                .Select(m => m.MessageRole == ChatMessage.Role.Assistant
                    // Old assistant turns carry THIS turn's SOURCE_n numbering;
                    // the next turn renumbers, so stale markers are stripped
                    // before they can bait wrong citations (D5).
                    ? new ChatMessage { MessageRole = m.MessageRole, Content = AiChatText.StripCitationMarkers(m.Content) }
                    : m)
                .ToList();
        }

        /// <summary>Refreshes the retrieval options from LIVE settings before
        /// each reply: TopK/evidence budget/history cap are configurable, and
        /// the semantic gate needs the current embedding model + prefix set
        /// (D2/D4).</summary>
        private void RefreshRetrievalOptions()
        {
            var cfg = _configProvider();
            _retrievalOptions.TopK = cfg.TopK;
            _retrievalOptions.EvidenceCharBudget = cfg.EvidenceCharBudget;
            _retrievalOptions.EmbeddingModel = cfg.EmbeddingModel;
            _retrievalOptions.EmbeddingQueryPrefix = cfg.EmbeddingQueryPrefix;
            _retrievalOptions.EmbeddingPrefixKey = (cfg.EmbeddingDocumentPrefix ?? "") + "\u0001" + (cfg.EmbeddingQueryPrefix ?? "");
            _maxHistoryMessages = Math.Max(2, cfg.MaxHistoryMessages);
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
        /// Cancels the in-flight reply and any queued message: without that,
        /// a reply for the PREVIOUS document kept IsProcessing true for up to
        /// five minutes and the new document's chat could not send.
        /// </summary>
        public void ClearForDocumentSwitch()
        {
            lock (_processingLock)
            {
                // Invalidate any in-flight index build or deferred reply.
                _initGeneration++;
                try { _replyCts?.Cancel(); } catch (ObjectDisposedException) { }
                try { _indexingCts?.Cancel(); } catch (ObjectDisposedException) { }
                _currentIndex = null;
                _currentFilePath = "";
                _currentDocumentId = "";
                _pendingUserInput = null;
                _pendingPlaceholder = null;
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Clear();
                SemanticStatus = "";
            });
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
        /// indexed - but ONLY when the AI panel is visible; while hidden the
        /// identity is re-bound and indexing is deferred to the next open
        /// (no background embedding for tabs the user is not looking at).
        /// </summary>
        public void HandleDocumentSwitch(string? filePath, bool panelVisible = true)
        {
            if (_currentFilePath == filePath)
                return;

            SaveHistory();
            ClearForDocumentSwitch();

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            if (!panelVisible)
            {
                // Re-bind identity only; OpenAiChat calls
                // InitializeForDocumentAsync when the user actually opens the
                // panel.
                lock (_processingLock)
                {
                    _currentFilePath = filePath;
                    _currentDocumentId = DocumentIndexer.ComputeDocumentId(filePath);
                }
                return;
            }

            _ = InitializeForDocumentAsync(filePath);
        }

        /// <summary>Snapshots the current transcript (without placeholders or
        /// error bubbles) under the current document id so switching back
        /// restores the conversation (C6).</summary>
        private void SaveHistory()
        {
            string docId;
            lock (_processingLock) docId = _currentDocumentId;
            if (string.IsNullOrEmpty(docId)) return;

            List<ChatMessage> snapshot = new();
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var m in Messages)
                {
                    if (m.IsLoading || !string.IsNullOrWhiteSpace(m.Error) || string.IsNullOrWhiteSpace(m.Content))
                        continue;
                    snapshot.Add(m);
                }
            });
            lock (_processingLock) _historyByDocument[docId] = snapshot;
        }

        /// <summary>
        /// Retrieval query for the user's input, the previous question and the
        /// shared follow-up heuristics now live in the testable AiChatText
        /// helper (D5): only genuine follow-ups are widened, and widening
        /// caps the PREVIOUS question, never the current one.
        /// </summary>
        private string BuildRetrievalQuery(string input)
        {
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
            return AiChatText.BuildRetrievalQuery(input, previous);
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

        /// <summary>
        /// Maps a failure to localized user-facing text. Provider failures
        /// arrive typed (AiProviderException.Category) - keyword matching on
        /// English exception text misfired on Windows wording ("actively
        /// refused"), on the provider's own signin message and on timeouts.
        /// Raw exception text and response bodies never reach the UI.
        /// </summary>
        private string MapErrorToFriendlyMessage(Exception ex)
        {
            if (ex is AiProviderException ape)
            {
                switch (ape.Category)
                {
                    case AiErrorCategory.OllamaNotRunning:
                        return _loc("Str_AiErrorOllamaNotRunning");
                    case AiErrorCategory.NotSignedIn:
                        return _loc("Str_AiErrorNotSignedIn");
                    case AiErrorCategory.ModelNotFound:
                        return string.Format(
                            _loc("Str_AiErrorModelNotFound"),
                            ape.ModelName ?? _configProvider().Model);
                    case AiErrorCategory.Busy:
                        return _loc("Str_AiErrorBusy");
                    case AiErrorCategory.UsageLimit:
                        return _loc("Str_AiErrorUsageLimit");
                    case AiErrorCategory.CutOff:
                        return _loc("Str_AiErrorCutOff");
                    case AiErrorCategory.Timeout:
                        return _loc("Str_AiErrorTimeout");
                    case AiErrorCategory.BadResponse:
                        return _loc("Str_AiErrorBadResponse");
                    default:
                        return _loc("Str_AiChatError");
                }
            }

            // Non-provider failures (e.g. retrieval/parse layers that never
            // went through the provider): classify the few well-known shapes
            // without echoing any exception text.
            if (ex is System.Net.Http.HttpRequestException hre
                && OpenAiCompatibleProvider.IsConnectionFailure(hre))
                return _loc("Str_AiErrorOllamaNotRunning");

            return _loc("Str_AiChatError");
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}