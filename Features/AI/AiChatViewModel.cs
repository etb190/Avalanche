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

        // v1.19.5: the semantic embedding pass is OPT-IN. Rapidly browsing papers
        // must not fan the GPU for books the reader only skims: the lexical channel
        // (text extraction, chunking, BM25) stays instant and automatic, and the
        // vector pass runs only when the reader clicks the research button.
        // v1.19.46: semantic research state is PER DOCUMENT and survives every
        // context switch. Leaving a document no longer cancels its embedding
        // pass - the pass keeps building in the background, a single lane
        // serializes the passes, and returning to a requested document resumes
        // its pass from the embedding store's checkpoint (already-embedded
        // chunks are skipped, so nothing repeats and nothing is lost).
        private readonly HashSet<string> _semanticRequestedDocs = new();   // the reader asked research for these documents
        private readonly HashSet<string> _semanticReadyDocs = new();       // these documents' semantic layers are done
        private readonly Dictionary<string, string> _semanticStatusByDoc = new();   // per-document terminal status line
        private readonly Dictionary<string, int> _semanticTextlessByDoc = new();    // carried from indexing for the ready line
        private string _semanticBuildingDoc = "";     // the document whose embedding pass is in flight ("" = idle lane)
        private int _semanticProgressDone;            // live batch counters of the running pass
        private int _semanticProgressTotal;
        private CancellationTokenSource? _semanticPassCts;   // the running pass's lifetime
        private CancellationTokenSource? _indexingCts;
        private readonly object _processingLock = new();
        private int _maxHistoryMessages = 6;

        // v1.19.46: a message queued behind a document's index build is per
        // context - the "Preparing..." bubble and the question both survive a
        // switch away and come back with the conversation.
        private sealed class PendingMessage
        {
            public string Input = "";
            public ChatMessage Placeholder = null!;
        }
        private readonly Dictionary<string, PendingMessage> _pendingByContext = new();

        // v1.19.46: replies are PER CONTEXT and keep running when the reader
        // routes elsewhere. The thinking bubble belongs to its conversation:
        // returning re-attaches it, and the finished answer (or the error)
        // lands in that conversation's history even if the reader was on
        // another tab when the model finished - "it stops generating" is over.
        private sealed class InflightReply
        {
            public ChatMessage Bubble = null!;
            public CancellationTokenSource Cts = null!;
        }
        private readonly Dictionary<string, InflightReply> _inflightByContext = new();
        // Web sessions die with their tab; a reply for a dead session is
        // never delivered anywhere.
        private readonly HashSet<string> _deadSessions = new();

        /// <summary>Chat transcript per document id - restored when the user
        /// switches back to a document (C6). Entries are snapshots without
        /// loading placeholders or error bubbles.</summary>
        private readonly Dictionary<string, List<ChatMessage>> _historyByDocument = new();

        // v1.19.22: the browser sidechat. When the web pane leads, the chat's
        // context is the active browser TAB: its transcript lives under its own
        // "wt_" key in this dictionary (never a document's "doc_" key), no index
        // is built and no embedding runs - the page itself, read fresh on every
        // question, is the whole context.
        private bool _isWebContext;
        private string _currentWebTabId = "";
        private string _webTitle = "";
        private string _webUrl = "";

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

        /// <summary>Drives the research button's face: lit in the accent while
        /// THIS document's semantic layer builds or is ready, muted while idle.
        /// (v1.19.5) v1.19.46: the state is the document's own - the button
        /// stays lit across switches while its background pass runs.</summary>
        public bool SemanticResearchActive
        {
            get
            {
                lock (_processingLock)
                {
                    string doc = _isWebContext ? "" : _currentDocumentId;
                    return doc.Length > 0
                        && (doc == _semanticBuildingDoc || _semanticReadyDocs.Contains(doc));
                }
            }
        }

        private void OnSemanticStateChanged() => OnPropertyChanged(nameof(SemanticResearchActive));

        /// <summary>True while the chat's context is a browser tab (v1.19.22):
        /// the window switches the context whenever the browser leads.</summary>
        public bool IsWebContext => _isWebContext;

        private string _contextTitle = "";

        /// <summary>One muted line under the header naming what this chat is
        /// about - the page's title in web mode, the book's file name otherwise.
        /// An empty string shows nothing.</summary>
        public string ContextTitle
        {
            get => _contextTitle;
            private set { _contextTitle = value; OnPropertyChanged(); }
        }

        private string _lastDurationText = "";

        /// <summary>How long the last completed reply took (v1.19.25) - the
        /// quiet line above the input. Cleared when the next question is
        /// sent, and with every context switch.</summary>
        public string LastDurationText
        {
            get => _lastDurationText;
            private set { _lastDurationText = value; OnPropertyChanged(); }
        }

        private string _lastAnswerModel = "";

        /// <summary>The model that produced the last completed answer
        /// (v1.19.32) - the bottom-right word beside the reply's timing
        /// line. Cleared with it: a new question, a context switch, a fresh
        /// conversation all leave no model behind.</summary>
        public string LastAnswerModel
        {
            get => _lastAnswerModel;
            private set { _lastAnswerModel = value; OnPropertyChanged(); }
        }

        /// <summary>The attribution line's text: "Model: {0}" in the reader's
        /// language, or empty when there is nothing to name.</summary>
        private string FormatModelUsed(string? model)
            => string.IsNullOrWhiteSpace(model) ? "" : string.Format(_loc("Str_AiModelUsed"), model);

        /// <summary>The window's page reader: extracts the named tab's readable
        /// page text fresh on every call (null when there is no page). The view
        /// model knows nothing about WebView2 - the window owns the browser.</summary>
        public Func<string, System.Threading.CancellationToken, Task<WebPageSnapshot?>>? WebPageReader { get; set; }

        // v1.19.46: "a reply is running" is now a question about the CURRENT
        // conversation, not the whole view model - a reply the reader left
        // behind keeps running without blocking the conversation on screen.
        public bool IsProcessing
        {
            get
            {
                lock (_processingLock)
                    return _currentDocumentId.Length > 0 && _inflightByContext.ContainsKey(_currentDocumentId);
            }
        }

        /// <summary>Re-announces the bindings derived from the in-flight set
        /// (Stop button, send gating) after a reply started or ended, or the
        /// active context changed.</summary>
        private void RaiseProcessingStateChanged()
        {
            OnPropertyChanged(nameof(IsProcessing));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(CanStartNewChat));
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

            // v1.19.25: a web conversation's footnote jumps ON THE PAGE - the
            // browser finds the quote, selects it, paints the custom
            // highlight and scrolls it into view. Nothing touches the viewer.
            bool webContext;
            string webTabId;
            lock (_processingLock) { webContext = _isWebContext; webTabId = _currentWebTabId; }
            if (webContext)
            {
                Avalanche.Services.AiHighlightLog.Log(
                    $"navigate: web tab {webTabId} quote={source.Quote?.Length ?? 0}ch");
                _mainWindow.Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    () => _mainWindow.NavigateToWebPageCitation(webTabId, source.Quote ?? ""));
                return;
            }

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
                // Same document already indexed OR still being indexed: keep
                // the existing build instead of restarting it - previously a
                // reopen while indexing bumped the generation, cleared the
                // messages and started a SECOND concurrent build/embed pass.
                if (_currentFilePath == filePath && (_currentIndex != null || IsIndexing))
                    return;

                // Claim a new generation: any index build still running for
                // the previous document is now stale and must not commit.
                _initGeneration++;
                _currentFilePath = filePath;
                _currentIndex = null;
                _currentDocumentId = DocumentIndexer.ComputeDocumentId(filePath);
                _isWebContext = false;   // a document took the context back from the browser
                _currentWebTabId = "";

                // v1.19.5: the research button is per-document. A new book starts
                // idle - zero embedding compute until the reader asks for it.
                // v1.19.46: the ask is REMEMBERED per document, so a book the
                // reader left mid-research picks its pass back up here.
            }

            string docId = _currentDocumentId;
            InflightReply? inflight = null;
            bool inflightLoading = false;
            PendingMessage? pending = null;
            List<ChatMessage> savedSnapshot = new();
            lock (_processingLock)
            {
                // Restore this document's saved transcript, if any (C6) -
                // snapshotted under the lock so a background delivery that is
                // appending an answer can never tear the walk (v1.19.46).
                if (_historyByDocument.TryGetValue(docId, out var savedList))
                    savedSnapshot.AddRange(savedList);
                if (_inflightByContext.TryGetValue(docId, out inflight))
                    inflightLoading = inflight.Bubble.IsLoading;
                _pendingByContext.TryGetValue(docId, out pending);
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Clear();
                foreach (var m in savedSnapshot)
                    Messages.Add(m);
                // v1.19.46: a reply still running for THIS conversation puts its
                // thinking bubble straight back - routing away no longer hides
                // it, and the answer lands inside it when the model finishes.
                if (inflight is not null && inflightLoading && !Messages.Contains(inflight.Bubble))
                    Messages.Add(inflight.Bubble);
                // v1.19.46: a message queued behind this document's index build
                // returns with its "Preparing..." placeholder, same story.
                else if (pending is not null && !Messages.Contains(pending.Placeholder))
                    Messages.Add(pending.Placeholder);
                ContextTitle = Path.GetFileName(filePath);
                // The status line is this document's own: research it left
                // building reads as still building, a finished pass reads ready.
                SemanticStatus = CurrentSemanticStatusLine();
                OnSemanticStateChanged();
                RaiseProcessingStateChanged();
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

                // Consume anything queued while indexing ran (v1.19.46: the
                // queue is per context - only THIS document's waiting question
                // is served here, keyed by its own conversation).
                lock (_processingLock)
                {
                    if (generation == _initGeneration
                        && _pendingByContext.TryGetValue(_currentDocumentId, out var queuedMsg))
                    {
                        string pendingKey = _currentDocumentId;
                        _pendingByContext.Remove(pendingKey);
                        var placeholder = queuedMsg.Placeholder;
                        Application.Current.Dispatcher.Invoke(() => Messages.Remove(placeholder));
                        _ = GenerateReplyAsync(queuedMsg.Input, pendingKey);
                    }
                }

                // Semantic layer: embed in the BACKGROUND so readiness never
                // waited on it (D1). Failures degrade search to keyword-only
                // and surface as a one-line localized status - they are never
                // fatal for the chat (D3).
                // v1.19.5: the pass is OPT-IN. Rapidly browsing papers must not fan
                // the GPU for books the reader only skims: the lexical channel above
                // is live, and the embedding pass runs only when the reader clicks
                // the research button (ToggleSemanticResearch).
                // v1.19.46: the ask is remembered per document - a book the reader
                // left mid-research resumes its pass right here, from whatever
                // checkpoint the embedding store already holds.
                string embedDocId;
                lock (_processingLock)
                {
                    embedDocId = _currentDocumentId;
                    _semanticTextlessByDoc[embedDocId] = textlessPages;
                }
                if (generation == _initGeneration
                    && _semanticRequestedDocs.Contains(embedDocId)
                    && !_semanticReadyDocs.Contains(embedDocId))
                {
                    EnsureSemanticPassRunning(index, embedDocId, progress);
                }

                // Nothing else to publish here: the lexical index went live
                // BEFORE the embedding pass, so readiness never waited on it.
            }
            catch (Exception ex)
            {
                IndexingStatus = DescribeIndexingFailure(ex);

                // Never leave a deferred "Preparing document..." bubble stuck:
                // if a message was queued while indexing, surface the failure
                // in-chat instead of waiting for an answer that cannot come
                // (v1.19.46: the queue is per context).
                PendingMessage? failed = null;
                lock (_processingLock)
                {
                    if (generation == _initGeneration
                        && _pendingByContext.TryGetValue(_currentDocumentId, out var fm))
                    {
                        failed = fm;
                        _pendingByContext.Remove(_currentDocumentId);
                    }
                }
                if (failed is not null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Messages.Remove(failed.Placeholder);
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

/// <summary>Starts THIS document's semantic embedding pass when the lane
        /// is free (v1.19.46). The pass belongs to the DOCUMENT, not to the
        /// panel's current context: switching tabs, opening pages or hiding the
        /// chat leaves it running, and every status update only paints while
        /// its document is the one on screen. A pass already running for another
        /// document parks this request (the requested set) - when the lane
        /// frees, the document on screen that asked for research starts at once,
        /// and a background document resumes on the reader's return from the
        /// embedding store's checkpoint.</summary>
        private void EnsureSemanticPassRunning(DocumentIndex index, string docId, IProgress<IndexingProgress>? progress)
        {
            bool started = false;
            lock (_processingLock)
            {
                if (_semanticBuildingDoc.Length == 0)
                {
                    started = true;
                    try { _semanticPassCts?.Dispose(); } catch (ObjectDisposedException) { }
                    _semanticPassCts = new CancellationTokenSource();
                    _semanticBuildingDoc = docId;
                    _semanticProgressDone = 0;
                    _semanticProgressTotal = 0;
                }
            }
            if (!started)
            {
                // The lane is busy with another document's pass; this request
                // stays parked in the requested set. The line says research is
                // on its way - and the store's checkpoint makes the eventual
                // run as short as the work left over allows.
                PaintSemanticStatus(docId, () => _loc("Str_AiChatSemanticBuilding"));
                return;
            }

            var passCt = _semanticPassCts!.Token;
            var embeddingModel = _configProvider().EmbeddingModel;
            var documentPrefix = _configProvider().EmbeddingDocumentPrefix;
            var queryPrefix = _configProvider().EmbeddingQueryPrefix;

            // Live per-batch progress for the semantic status line: the
            // IndexingStatus row goes dark once IsIndexing clears, so a
            // background pass used to show a FROZEN "building..." for its
            // whole duration - indistinguishable from a hang (regression
            // report: "wont go away"). Now every batch updates the line -
            // and v1.19.46 keeps it honest across switches: the line only
            // paints while this document is on screen, and returning to the
            // document repaints the last counters it left behind.
            var embedProgress = new FanOutProgress(progress, p =>
            {
                if (p.Stage != IndexingStage.Embedding || p.Total <= 0) return;
                lock (_processingLock)
                {
                    _semanticProgressDone = p.Done;
                    _semanticProgressTotal = p.Total;
                }
                PaintSemanticStatus(docId, () => string.Format(
                    _loc("Str_AiChatSemanticBuildingProgress"), p.Done, p.Total));
            });

            Avalanche.Services.AiHighlightLog.Log(
                $"embedding pass START: doc={TruncLog(docId)} chunks={index.Chunks.Count} model={embeddingModel}");

            _ = Task.Run(async () =>
            {
                var passStopwatch = System.Diagnostics.Stopwatch.StartNew();

                // One full embedding attempt. Success publishes the ready
                // status; failures propagate to the handlers below.
                async Task AttemptAsync()
                {
                    await _indexer.EnsureEmbeddingsAsync(index,
                        (texts, ct) => _embeddingClient.GenerateEmbeddingsAsync(texts, ct),
                        embeddingModel, embedProgress, passCt,
                        documentPrefix, queryPrefix);

                    // v1.19.46: no generation veto here any more - the pass
                    // belongs to its document, and the vectors it wrote are the
                    // document's own. A switch away must not undo the work.
                    if (passCt.IsCancellationRequested) return;
                    lock (_processingLock) _semanticReadyDocs.Add(docId);
                    StoreSemanticStatus(docId, () => SemanticReadyLine(docId));
                    PaintSemanticStatus(docId, () => SemanticReadyLine(docId));
                    Avalanche.Services.AiHighlightLog.Log(
                        $"embedding pass DONE in {passStopwatch.ElapsedMilliseconds}ms: {index.Chunks.Count} chunk(s), semantic channel ready");
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
                    await AttemptAsync();
                }
                catch (OperationCanceledException) when (passCt.IsCancellationRequested)
                {
                    // The reader toggled research off for this document (or the
                    // app is closing) - the only hands that stop a pass now
                    // (v1.19.46). Nothing is lost: embedded chunks stay in the
                    // store and a later ask resumes from there.
                    Avalanche.Services.AiHighlightLog.Log(
                        $"embedding pass CANCELLED after {passStopwatch.ElapsedMilliseconds}ms (doc={TruncLog(docId)})");
                    StoreSemanticStatus(docId, null);
                    PaintSemanticStatus(docId, () => "");
                }
                catch (OperationCanceledException)
                {
                    // Batch deadlines survived every split (the endpoint
                    // cannot embed even a handful of passages in time):
                    // visible keyword-only state instead of an eternal wait.
                    Avalanche.Services.AiHighlightLog.Log(
                        $"embedding pass TIMEOUT after {passStopwatch.ElapsedMilliseconds}ms - endpoint never finished; keyword-only mode");
                    StoreSemanticStatus(docId, () => _loc("Str_AiChatEmbeddingsUnavailable"));
                    PaintSemanticStatus(docId, () => _loc("Str_AiChatEmbeddingsUnavailable"));
                }
                catch (AiProviderException pex) when (pex.Category == AiErrorCategory.ModelNotFound)
                {
                    Avalanche.Services.AiHighlightLog.Log(
                        $"embedding pass FAILED in {passStopwatch.ElapsedMilliseconds}ms: model missing ({pex.ModelName})");
                    StoreSemanticStatus(docId, () => string.Format(
                        _loc("Str_AiChatEmbeddingModelMissing"), pex.ModelName ?? embeddingModel));
                    PaintSemanticStatus(docId, () => string.Format(
                        _loc("Str_AiChatEmbeddingModelMissing"), pex.ModelName ?? embeddingModel));
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

                    var outcome = await RetryEmbeddingOnceAsync(passCt, AttemptAsync);

                    if (outcome == SemanticRetryOutcome.ModelMissing)
                    {
                        string missingModel = embeddingModel;
                        if (embedEx is AiProviderException firstTyped && !string.IsNullOrEmpty(firstTyped.ModelName))
                            missingModel = firstTyped.ModelName;
                        else if (embedEx.InnerException is AiProviderException innerTyped && !string.IsNullOrEmpty(innerTyped.ModelName))
                            missingModel = innerTyped.ModelName;
                        StoreSemanticStatus(docId, () => string.Format(
                            _loc("Str_AiChatEmbeddingModelMissing"), missingModel));
                        PaintSemanticStatus(docId, () => string.Format(
                            _loc("Str_AiChatEmbeddingModelMissing"), missingModel));
                    }
                    else if (outcome == SemanticRetryOutcome.Unavailable)
                    {
                        StoreSemanticStatus(docId, () => _loc("Str_AiChatEmbeddingsUnavailable"));
                        PaintSemanticStatus(docId, () => _loc("Str_AiChatEmbeddingsUnavailable"));
                    }
                    // Ready / Cancelled need no message here: Ready
                    // was published by the attempt itself, Cancelled
                    // cleared the line in its own handler.
                }
                finally
                {
                    lock (_processingLock)
                    {
                        if (_semanticBuildingDoc == docId)
                            _semanticBuildingDoc = "";
                        _semanticProgressDone = 0;
                        _semanticProgressTotal = 0;
                    }
                    try { Application.Current.Dispatcher.Invoke(OnSemanticStateChanged); }
                    catch { /* app shutting down */ }
                    // The lane freed: a document on screen that asked for
                    // research and has none yet starts its pass at once
                    // (v1.19.46) - queued demand never waits for a click.
                    HandSemanticLaneToNextDemand();
                }
            });
        }

        /// <summary>Remembers a document's terminal research verdict for the
        /// status line (v1.19.46): the ready line, a failure line, or (null)
        /// silence after a cancel. The factory runs on the UI thread.</summary>
        private void StoreSemanticStatus(string docId, Func<string>? lineFactory)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    string? line = lineFactory?.Invoke();
                    lock (_processingLock)
                    {
                        if (line is null) _semanticStatusByDoc.Remove(docId);
                        else _semanticStatusByDoc[docId] = line;
                    }
                });
            }
            catch { /* app shutting down */ }
        }

        /// <summary>The ready line for a document's completed research: the
        /// partial-text-layer caveat rides along when the book has scanned
        /// pages (v1.19.46 - the line is per document, so returning to the
        /// book shows the same verdict it earned). UI thread only.</summary>
        private string SemanticReadyLine(string docId)
        {
            int textlessPages;
            lock (_processingLock) _semanticTextlessByDoc.TryGetValue(docId, out textlessPages);
            return textlessPages > 0
                ? string.Format(_loc("Str_AiChatPartialTextLayer"), textlessPages) +
                  " " + _loc("Str_AiChatSemanticReady")
                : _loc("Str_AiChatSemanticReady");
        }

        /// <summary>Paints a semantic status line, but ONLY when the named
        /// document is the context on screen (v1.19.46): a background pass for
        /// a document the reader left never writes over the live conversation.</summary>
        private void PaintSemanticStatus(string docId, Func<string> lineFactory)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    lock (_processingLock)
                    {
                        if (_isWebContext || _currentDocumentId != docId) return;
                        SemanticStatus = lineFactory();
                    }
                    OnSemanticStateChanged();
                });
            }
            catch { /* app shutting down */ }
        }

        /// <summary>The semantic status line for the CURRENT document from its
        /// own state (v1.19.46): the live building counters, the terminal ready
        /// line, a stored failure, or silence. Each document keeps its own line,
        /// so returning to a document restores what its research was doing.
        /// UI thread only.</summary>
        private string CurrentSemanticStatusLine()
        {
            lock (_processingLock)
            {
                if (_isWebContext) return "";
                string doc = _currentDocumentId;
                if (doc.Length == 0) return "";
                if (_semanticBuildingDoc == doc)
                {
                    return _semanticProgressTotal > 0
                        ? string.Format(_loc("Str_AiChatSemanticBuildingProgress"),
                            _semanticProgressDone, _semanticProgressTotal)
                        : _loc("Str_AiChatSemanticBuilding");
                }
                if (_semanticReadyDocs.Contains(doc))
                    return SemanticReadyLine(doc);
                return _semanticStatusByDoc.TryGetValue(doc, out var line) ? line : "";
            }
        }

        /// <summary>The embedding lane freed (v1.19.46): if the document
        /// currently on screen asked for research and has none yet, its pass
        /// starts at once. Requested documents in the background wait for their
        /// reader to return - their pass resumes then, from the checkpoint.</summary>
        private void HandSemanticLaneToNextDemand()
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    DocumentIndex? index = null;
                    string docId = "";
                    lock (_processingLock)
                    {
                        if (!_isWebContext)
                        {
                            docId = _currentDocumentId;
                            index = _currentIndex;
                        }
                        if (docId.Length == 0
                            || !_semanticRequestedDocs.Contains(docId)
                            || _semanticReadyDocs.Contains(docId))
                            return;
                    }
                    if (index is not null)
                        EnsureSemanticPassRunning(index, docId, null);
                });
            }
            catch { /* app shutting down */ }
        }

        /// <summary>The reader clicked the research button (v1.19.5): build the
        /// semantic layer for the current document on demand - or, while it is
        /// building, cancel the pass. A ready index needs no second build; the
        /// accent on the button IS the feedback. No lexical index yet (the
        /// document is still preparing): a no-op worth one more click.
        /// v1.19.46: the request survives switches - clicking research and
        /// leaving the document does NOT kill the pass; only a second click on
        /// the same document (or app shutdown) stops it. While another
        /// document's pass holds the lane, this document's ask parks and its
        /// line says research is coming.</summary>
        public void ToggleSemanticResearch()
        {
            DocumentIndex? index;
            string docId;
            lock (_processingLock)
            {
                if (_isWebContext) return;
                index = _currentIndex;
                docId = _currentDocumentId;
            }
            if (index is null || docId.Length == 0) return;

            bool stopThisDoc = false;
            bool queuedHere = false;
            lock (_processingLock)
            {
                if (_semanticBuildingDoc == docId)
                    stopThisDoc = true;
                else if (_semanticReadyDocs.Contains(docId))
                    return;   // already built; the accent is the feedback
                else if (_semanticRequestedDocs.Contains(docId) && _semanticBuildingDoc.Length > 0)
                    queuedHere = true;   // parked behind another document's pass
                else
                    _semanticRequestedDocs.Add(docId);
            }

            if (stopThisDoc)
            {
                lock (_processingLock) _semanticRequestedDocs.Remove(docId);
                try { _semanticPassCts?.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
            if (queuedHere)
            {
                // A parked ask: a second click takes it back.
                lock (_processingLock) _semanticRequestedDocs.Remove(docId);
                PaintSemanticStatus(docId, () => "");
                OnSemanticStateChanged();
                return;
            }
            EnsureSemanticPassRunning(index, docId, null);
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
        /// visible keyword-only state instead of retrying forever.
        /// v1.19.46: no generation veto - the retry belongs to the document.</summary>
        private async Task<SemanticRetryOutcome> RetryEmbeddingOnceAsync(
            CancellationToken passToken,
            Func<Task> attempt)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(65), passToken);
            }
            catch (OperationCanceledException)
            {
                return SemanticRetryOutcome.Cancelled;
            }

            try
            {
                await attempt();
                return SemanticRetryOutcome.Ready;
            }
            catch (OperationCanceledException) when (passToken.IsCancellationRequested)
            {
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

            // v1.19.25: a new question retires the previous answer's timing
            // line - above the input there is either the answer just
            // delivered or nothing at all.
            LastDurationText = "";
            LastAnswerModel = "";

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

            // v1.19.22: the browser sidechat never queues behind an index -
            // there is no index. The reply path reads the page fresh itself.
            bool webContext;
            lock (_processingLock) webContext = _isWebContext;
            if (webContext)
            {
                await GenerateReplyAsync(input);
                return;
            }

            // If indexing not complete, defer the AI reply until the index is ready
            if (_currentIndex == null)
            {
                if (IsIndexing)
                {
                    string queueKey;
                    lock (_processingLock) queueKey = _currentDocumentId;
                    var placeholder = new ChatMessage
                    {
                        MessageRole = ChatMessage.Role.Assistant,
                        Content = _loc("Str_AiChatPreparing"),
                        IsLoading = true
                    };
                    var queuedMsg = new PendingMessage { Input = input, Placeholder = placeholder };
                    bool indexArrived;
                    lock (_processingLock)
                    {
                        _pendingByContext[queueKey] = queuedMsg;
                        indexArrived = _currentIndex != null;
                    }
                    Application.Current.Dispatcher.Invoke(() => Messages.Add(placeholder));

                    // Indexing can complete while the placeholder is being added
                    // (the completion callback only consumes input queued before
                    // it ran) - resolve immediately so this message is never left
                    // waiting on a placeholder that nothing will replace.
                    if (indexArrived)
                    {
                        PendingMessage? resolved = null;
                        lock (_processingLock)
                        {
                            if (_pendingByContext.TryGetValue(queueKey, out var q) && q == queuedMsg)
                            {
                                _pendingByContext.Remove(queueKey);
                                resolved = q;
                            }
                        }
                        if (resolved is not null)
                        {
                            Application.Current.Dispatcher.Invoke(() => Messages.Remove(resolved.Placeholder));
                            _ = GenerateReplyAsync(resolved.Input, queueKey);
                        }
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
                // v1.19.53: the error box is the single surface here too -
                // with the error border sharing the bubble's cell, a message
                // carrying both would render the same words twice.
                Application.Current.Dispatcher.Invoke(() => Messages.Add(new ChatMessage
                {
                    MessageRole = ChatMessage.Role.Assistant,
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
        /// v1.19.46: the reply belongs to the CONVERSATION it was asked in, not
        /// to the panel's momentary context. Routing elsewhere in the sidechat
        /// leaves it running: the thinking bubble re-attaches when the reader
        /// comes back, and the finished answer (or the error) is delivered into
        /// that conversation's history even if the reader was elsewhere when
        /// the model finished. Only that conversation's own Stop cancels.
        /// </summary>
        private async Task GenerateReplyAsync(string input, string? contextKey = null)
        {
            string key;
            ChatMessage assistantMsg;
            CancellationTokenSource cts;
            lock (_processingLock)
            {
                key = contextKey ?? _currentDocumentId;
                if (key.Length == 0) return;
                if (_inflightByContext.ContainsKey(key)) return;   // one reply per conversation
                cts = new CancellationTokenSource();
                // Add loading assistant message. This is the reply indicator, so
                // it must not reuse the indexing placeholder text ("Preparing
                // document...") - during the embedding stall users could not
                // tell a working reply from a stuck index build.
                assistantMsg = new ChatMessage
                {
                    MessageRole = ChatMessage.Role.Assistant,
                    Content = _loc("Str_AiChatThinking"),
                    IsLoading = true
                };
                _inflightByContext[key] = new InflightReply { Bubble = assistantMsg, Cts = cts };
            }
            RaiseProcessingStateChanged();

            // The bubble joins the live view only when its conversation is the
            // one on screen; otherwise it travels with the in-flight record and
            // is delivered to the conversation when it finishes (v1.19.46).
            bool onScreen;
            lock (_processingLock) onScreen = _currentDocumentId == key;
            if (onScreen)
                Application.Current.Dispatcher.Invoke(() => Messages.Add(assistantMsg));

            CancellationToken ct = cts.Token;

            // v1.19.46: the prompt's memory is snapshotted NOW, from the reply's
            // own conversation - a switch mid-reply can never pollute it with
            // another conversation's turns.
            List<ChatMessage> recentHistory = onScreen
                ? GetRecentMessages()
                : RecentHistoryFor(key);
            if (!recentHistory.Any(m => m.MessageRole == ChatMessage.Role.User && m.Content == input))
                recentHistory.Add(new ChatMessage { MessageRole = ChatMessage.Role.User, Content = input });

            // v1.19.25: the reply's wall clock - extraction/retrieval plus
            // the provider's turn, everything the reader waits through.
            var replyClock = System.Diagnostics.Stopwatch.StartNew();
            bool cancelled = false;

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

                // v1.19.22: the browser sidechat - the page itself is the
                // context. Readability lifts the active tab's article text
                // fresh for this turn (nothing cached, nothing shared), and
                // the page becomes the prompt's only evidence. No retrieval,
                // no vectors. v1.19.25: the answer cites the page - the
                // prompt's numbered segments are the evidence list, and each
                // footnote's quote is what the browser hunts down, selects
                // and highlights on the live page.
                // v1.19.46: the reply's key decides the kind - a web session
                // key stays a web reply even when the reader moved to a book.
                bool webContext = WebChat.IsWebSessionKey(key);
                if (webContext)
                {
                    // The tab rides the key, not the panel's current context:
                    // the page the question was asked about is the page it
                    // reads, wherever the reader went.
                    string tabId = key.StartsWith(WebChat.SessionPrefix, StringComparison.Ordinal)
                        ? key[WebChat.SessionPrefix.Length..]
                        : key;
                    var reader = WebPageReader;
                    WebPageSnapshot? page = null;
                    if (reader is not null)
                    {
                        try { page = await reader(tabId, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { page = null; }
                    }

                    // A tab with no readable page (blank tab, browser error
                    // page, extraction timeout) answers honestly instead of
                    // pretending the model read something.
                    if (page is null)
                    {
                        assistantMsg.Content = _loc("Str_AiWebNoPage");
                        assistantMsg.IsLoading = false;
                        return;
                    }

                    var webConfig = Features.AI.AiSurfaceModels.Configure(_configProvider(), AiSurface.WebSidechat);
                    var webResponse = await GetProvider(webConfig).GetChatCompletionAsync(
                        WebChat.BuildSystemPrompt(page),
                        recentHistory,
                        new List<DocumentChunk>(),
                        "",
                        webConfig,
                        ct);

                    // v1.19.25: the segments the prompt numbered are this
                    // reply's evidence list; the model's sources resolve
                    // against them and against the page's own text before a
                    // single footnote circle is allowed to render.
                    var segments = WebChat.SplitPageSegments(page.Text);
                    WebChat.ResolveWebSources(webResponse.Sources, webResponse.Answer, segments, page.Text);
                    assistantMsg.Sources = webResponse.Sources;
                    assistantMsg.Content = webResponse.Answer;
                    assistantMsg.IsLoading = false;
                    // v1.19.25: this answer's wall clock, above the input -
                    // only when this conversation is the one on screen (v1.19.46).
                    PaintReplyAttribution(key,
                        () => string.Format(_loc("Str_AiChatTook"), AiChatText.FormatDuration(replyClock.Elapsed)),
                        () => FormatModelUsed(webConfig.Model));
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

                var retrievalQuery = BuildRetrievalQuery(input, recentHistory);
                var retrieved = await _retriever.RetrieveAsync(key, retrievalQuery, _retrievalOptions.TopK, ct);

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
                var config = Features.AI.AiSurfaceModels.Configure(_configProvider(), AiSurface.Sidechat);
                var response = await GetProvider(config).GetChatCompletionAsync(
                    systemPrompt,
                    recentHistory,
                    retrieved.ConvertAll(r => r.Chunk),
                    sourceRefs,
                    config,
                    ct);

                // Resolve each returned sourceId through THIS reply's evidence
                // list before anything binds to Sources: the chip row and the
                // inline footnote circles both read this list while rendering.
                ResolveSources(response.Sources, retrieved);

                assistantMsg.Sources = response.Sources; // chip row binds on this change
                assistantMsg.Content = response.Answer;  // markdown rebuild sees the sources
                assistantMsg.IsLoading = false;
                // v1.19.25: this answer's wall clock, above the input -
                // only when this conversation is the one on screen (v1.19.46).
                PaintReplyAttribution(key,
                    () => string.Format(_loc("Str_AiChatTook"), AiChatText.FormatDuration(replyClock.Elapsed)),
                    () => FormatModelUsed(config.Model));

                // Scroll to bottom - fire-and-forget UI update
                _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    // Scroll logic would go here
                });
            }
            catch (OperationCanceledException)
            {
                // The conversation's own Stop button (or a dead web session
                // letting go): the loading bubble disappears without an error
                // bubble, and nothing is recorded (v1.19.46).
                cancelled = true;
                try { Application.Current.Dispatcher.Invoke(() => Messages.Remove(assistantMsg)); }
                catch { /* app shutting down */ }
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
                // v1.19.46: retire the in-flight record; when the reader is
                // elsewhere the finished bubble (answer or error - never a
                // cancel) is delivered into its own conversation's history,
                // so coming back shows what was produced.
                RetireInflight(key, assistantMsg, record: !cancelled);
            }
        }

        /// <summary>The timing/model line above the input (v1.19.25/v1.19.32).
        /// Only the reply whose conversation is on screen may write it
        /// (v1.19.46): an answer delivered in the background never repaints
        /// the context the reader is looking at.</summary>
        private void PaintReplyAttribution(string key, Func<string> durationFactory, Func<string> modelFactory)
        {
            bool isCurrent;
            lock (_processingLock) isCurrent = _currentDocumentId == key;
            if (!isCurrent) return;
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    lock (_processingLock)
                    {
                        if (_currentDocumentId != key) return;
                        LastDurationText = durationFactory();
                        LastAnswerModel = modelFactory();
                    }
                });
            }
            catch { /* app shutting down */ }
        }

        /// <summary>Ends a reply's bookkeeping (v1.19.46): the in-flight record
        /// is retired, and when the bubble is not on screen the finished
        /// message - the answer, or the error - is appended to its own
        /// conversation's history so returning shows it. A bubble that IS on
        /// screen stays put; the next SaveHistory picks it up.</summary>
        private void RetireInflight(string key, ChatMessage assistantMsg, bool record)
        {
            lock (_processingLock)
            {
                if (_inflightByContext.TryGetValue(key, out var rec) && rec.Bubble == assistantMsg)
                {
                    _inflightByContext.Remove(key);
                    try { rec.Cts.Dispose(); } catch { }
                }
            }
            RaiseProcessingStateChanged();
            if (!record) return;
            bool displayed = false;
            try
            {
                Application.Current.Dispatcher.Invoke(() => displayed = Messages.Contains(assistantMsg));
            }
            catch { return; /* app shutting down */ }
            if (displayed) return;
            lock (_processingLock)
            {
                if (_deadSessions.Contains(key)) return;
                // Copy-on-write: a restore walking the old list on the UI
                // thread can never meet the append mid-enumeration.
                var list = _historyByDocument.TryGetValue(key, out var existing)
                    ? new List<ChatMessage>(existing)
                    : new List<ChatMessage>();
                list.Add(assistantMsg);
                _historyByDocument[key] = list;
            }
        }

        /// <summary>The saved transcript of a conversation the reader is not
        /// looking at (v1.19.46): a reply that keeps running in the background
        /// builds its prompt from its OWN conversation's history, never from
        /// whatever context is on screen now.</summary>
        private List<ChatMessage> RecentHistoryFor(string key)
        {
            List<ChatMessage> savedCopy;
            lock (_processingLock)
            {
                savedCopy = _historyByDocument.TryGetValue(key, out var saved)
                    ? new List<ChatMessage>(saved)
                    : new List<ChatMessage>();
            }
            return GetRecentMessagesFrom(savedCopy);
        }

        /// <summary>Cancels the CURRENT conversation's in-flight reply, if any
        /// (Stop button). Replies running for conversations the reader left
        /// keep running - their answers still reach their own history
        /// (v1.19.46).</summary>
        public void CancelReply()
        {
            InflightReply? rec;
            lock (_processingLock)
            {
                _inflightByContext.TryGetValue(_currentDocumentId, out rec);
            }
            if (rec is null) return;
            try { rec.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>Releases the DB connection, HTTP clients and the static
        /// citation-click subscription (which was never removed before, so a
        /// closed window's VM kept responding to clicks). v1.19.46: the
        /// research pass and every background reply are stopped too - the app
        /// is going down, nothing should outlive it.</summary>
        public void Dispose()
        {
            AiMarkdown.CitationClicked -= OnInlineCitationClicked;
            CancelReply();
            try { _semanticPassCts?.Cancel(); } catch (ObjectDisposedException) { }
            lock (_processingLock)
            {
                foreach (var rec in _inflightByContext.Values)
                {
                    try { rec.Cts.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
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
            => GetRecentMessagesFrom(Messages);

        /// <summary>v1.19.46: the same trim over any transcript - the live
        /// conversation for a reply on screen, or the saved transcript of the
        /// conversation a background reply belongs to.</summary>
        private List<ChatMessage> GetRecentMessagesFrom(IEnumerable<ChatMessage> source)
        {
            return source
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
        /// Clears the current conversation binding (for document switch).
        /// v1.19.46: the in-flight reply and the research pass belong to their
        /// document now - NEITHER is cancelled here. The reply lands in its
        /// conversation's history when the model answers, the embedding pass
        /// keeps building, and the new context's chat can send at once because
        /// "processing" is a per-conversation question.
        /// </summary>
        public void ClearForDocumentSwitch()
        {
            lock (_processingLock)
            {
                // Invalidate any in-flight index build for the leaving document.
                _initGeneration++;
                try { _indexingCts?.Cancel(); } catch (ObjectDisposedException) { }
                _currentIndex = null;
                _currentFilePath = "";
                _currentDocumentId = "";
                _isWebContext = false;
                _currentWebTabId = "";
                _webTitle = "";
                _webUrl = "";
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Clear();
                ContextTitle = "";
                SemanticStatus = "";
                LastDurationText = "";   // no answer of this context is on screen
                LastAnswerModel = "";
                OnSemanticStateChanged();   // the research face follows the leaving document
            });
            RaiseProcessingStateChanged();
        }

        /// <summary>
        /// Starts a fresh conversation: clears bubbles, the input box and any
        /// message queued for THIS conversation, without dropping the document
        /// index, so the next question answers immediately (no re-index wait).
        /// The New-chat button is disabled (CanStartNewChat) while a reply is
        /// streaming.
        /// </summary>
        public void StartNewChat()
        {
            PendingMessage? parked = null;
            lock (_processingLock)
            {
                if (_currentDocumentId.Length > 0
                    && _pendingByContext.TryGetValue(_currentDocumentId, out var pm))
                {
                    _pendingByContext.Remove(_currentDocumentId);
                    parked = pm;
                }
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (parked is not null)
                    Messages.Remove(parked.Placeholder);
                Messages.Clear();
                ClearInput();
                LastDurationText = "";   // a fresh conversation has no last answer
                LastAnswerModel = "";
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
                string fileName = Path.GetFileName(filePath);
                Application.Current.Dispatcher.Invoke(() => ContextTitle = fileName);
                return;
            }

            _ = InitializeForDocumentAsync(filePath);
        }

        /// <summary>
        /// The browser pane took (or moved within) the chat's context
        /// (v1.19.22): switch to that tab's own session - transcript restored,
        /// no indexing, no embeddings. The same tab again only refreshes the
        /// title and address the context line shows. Web keys live under the
        /// "wt_" prefix, so a page and a book can never share a conversation.
        /// </summary>
        public void HandleWebContextChanged(string tabId, string title, string url)
        {
            if (string.IsNullOrWhiteSpace(tabId)) return;
            string key = WebChat.SessionKey(tabId);
            bool switchSession;
            lock (_processingLock)
                switchSession = !_isWebContext || _currentWebTabId != tabId;

            if (!switchSession)
            {
                // Same tab: the conversation continues; only the header follows.
                lock (_processingLock) { _webTitle = title; _webUrl = url; }
                Application.Current.Dispatcher.Invoke(() =>
                    ContextTitle = WebContextLabel(title, url));
                return;
            }

            SaveHistory();
            ClearForDocumentSwitch();   // bump generation, re-bind identity, clear bubbles
            InflightReply? inflight = null;
            bool inflightLoading = false;
            PendingMessage? pending = null;
            List<ChatMessage> savedSnapshot = new();
            lock (_processingLock)
            {
                _isWebContext = true;
                _currentWebTabId = tabId;
                _currentDocumentId = key;
                _currentFilePath = "";
                _currentIndex = null;
                _webTitle = title;
                _webUrl = url;
                // Restore this tab's saved transcript (C6) - snapshotted under
                // the lock so a background delivery appending an answer can
                // never tear the walk (v1.19.46).
                if (_historyByDocument.TryGetValue(key, out var savedList))
                    savedSnapshot.AddRange(savedList);
                if (_inflightByContext.TryGetValue(key, out inflight))
                    inflightLoading = inflight.Bubble.IsLoading;
                _pendingByContext.TryGetValue(key, out pending);
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Clear();
                foreach (var m in savedSnapshot)
                    Messages.Add(m);
                // v1.19.46: this tab's own in-flight reply re-attaches its
                // thinking bubble - the answer lands inside it when the model
                // finishes, however many times the reader routed away.
                if (inflight is not null && inflightLoading && !Messages.Contains(inflight.Bubble))
                    Messages.Add(inflight.Bubble);
                else if (pending is not null && !Messages.Contains(pending.Placeholder))
                    Messages.Add(pending.Placeholder);
                ContextTitle = WebContextLabel(title, url);
                IndexingStatus = "";
                IndexingProgress = 0.0;
                // The research button has nothing to build for a page, and
                // the status line no longer says so forever - the one message
                // that never went away was noise, not information. Silence
                // collapses the row; the button itself explains on click.
                SemanticStatus = "";
                OnSemanticStateChanged();
                RaiseProcessingStateChanged();
            });
        }

        /// <summary>
        /// The browser pane went away (v1.19.22): park the page's transcript
        /// and drop the web context. The window re-binds the active document
        /// right after, so nothing else needs to happen here.
        /// </summary>
        public void HandleWebContextCleared()
        {
            bool was;
            lock (_processingLock) was = _isWebContext;
            if (!was) return;
            SaveHistory();
            ClearForDocumentSwitch();
            lock (_processingLock)
            {
                _isWebContext = false;
                _currentWebTabId = "";
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                ContextTitle = "";
                SemanticStatus = "";
            });
        }

        /// <summary>A browser tab died (v1.19.22): its sidechat session dies
        /// with it. When the dead tab WAS the chat's context, the live
        /// transcript is dropped too, so a later SaveHistory cannot resurrect
        /// the session under its key - the window re-binds right after.</summary>
        public void DiscardWebSession(string tabId)
        {
            if (string.IsNullOrWhiteSpace(tabId)) return;
            string key = WebChat.SessionKey(tabId);
            bool wasCurrent;
            lock (_processingLock)
            {
                _historyByDocument.Remove(key);
                // v1.19.46: a dead tab's queued question and in-flight reply
                // die with the session - the reply is cancelled and its result
                // is never delivered anywhere.
                _deadSessions.Add(key);
                _pendingByContext.Remove(key);
                if (_inflightByContext.TryGetValue(key, out var rec))
                {
                    _inflightByContext.Remove(key);
                    try { rec.Cts.Cancel(); } catch (ObjectDisposedException) { }
                    try { rec.Cts.Dispose(); } catch { }
                }
                wasCurrent = _isWebContext && _currentDocumentId == key;
                if (wasCurrent)
                {
                    _isWebContext = false;
                    _currentWebTabId = "";
                    _currentDocumentId = "";
                }
            }
            RaiseProcessingStateChanged();
            if (wasCurrent)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Messages.Clear();
                    ContextTitle = "";
                    SemanticStatus = "";
                });
            }
        }

        /// <summary>The context line's text for a web page: the title, or the
        /// host when no title arrived, or the raw address as the last resort.</summary>
        private static string WebContextLabel(string title, string url)
        {
            if (!string.IsNullOrWhiteSpace(title)) return title;
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrWhiteSpace(u.Host))
                return u.Host;
            return url;
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
        /// caps the PREVIOUS question, never the current one. v1.19.46: the
        /// previous question comes from the reply's own conversation snapshot,
        /// never from whatever context is on screen when the model is read.
        /// </summary>
        private string BuildRetrievalQuery(string input, List<ChatMessage> transcript)
        {
            string? previous = null;
            for (int i = transcript.Count - 1; i >= 0; i--)
            {
                var m = transcript[i];
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
        /// When the server answered with its own error text, that text is
        /// quoted verbatim - the reader sees what the provider said, not a
        /// paraphrase of it.
        /// </summary>
        private string MapErrorToFriendlyMessage(Exception ex)
        {
            if (ex is AiProviderException ape)
            {
                // The server's own words first: a bare "HTTP 400" never
                // helped anyone debug a dial. Local failures (no server
                // involved) carry no detail and keep their localized
                // wording below.
                if (!string.IsNullOrWhiteSpace(ape.ServerDetail))
                {
                    return ape.ServerDetail;
                }

                switch (ape.Category)
                {
                    case AiErrorCategory.OllamaNotRunning:
                        return _loc("Str_AiErrorOllamaNotRunning");
                    case AiErrorCategory.ServiceUnreachable:
                        return _loc("Str_AiErrorServiceUnreachable");
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