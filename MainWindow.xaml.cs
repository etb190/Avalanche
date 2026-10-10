using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using Avalanche.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace Avalanche
{
    public partial class MainWindow : Window
    {
        private PdfWorkingDocument? _doc { get => ActiveViewer?.DocumentRef; set { ActiveViewer?.DocumentRef = value; } }
        private string? _currentFile { get => ActiveViewer?.CurrentFileRef; set { ActiveViewer?.CurrentFileRef = value; } }
        private string? _originalFile { get => ActiveViewer?.OriginalFileRef; set { ActiveViewer?.OriginalFileRef = value; } }
        private Point _dragStartPoint;

        // Zoom
        private double _zoomLevel { get => _view.ZoomLevel; set => _view.ZoomLevel = value; }
        private double _lastRenderZoom { get => _view.LastRenderZoom; set => _view.LastRenderZoom = value; }
        private int _renderedPrimaryPage { get => _view.RenderedPrimaryPage; set => _view.RenderedPrimaryPage = value; }
        // internal: the viewer control aliases these as its own consts (PdfViewer.Bridge.cs).
        // They stay declared here because MainWindow.xaml.cs and KeyboardShortcuts.cs read them
        // too, and a const costs nothing to alias but would drift if duplicated.
        internal const double ZoomMin = 0.05;
        internal const double ZoomMax = 5.0;
        internal const double ZoomStep = 0.15;
        private FitMode _fitMode { get => _view.Fit; set => _view.Fit = value; }
        private System.Windows.Threading.DispatcherTimer? _rerenderTimer { get => _view.RerenderTimer; set => _view.RerenderTimer = value; }
        private System.Threading.CancellationTokenSource? _secondaryRenderCts { get => _view.SecondaryRenderCts; set => _view.SecondaryRenderCts = value; }
        // ── Split pane ─────────────────────────────────────────────────────────────────────
        // The per-view state lives in one object (Models/ViewerState.cs) so a second pane can have
        // its own, and the VIEWER owns it, not the window. The window reads it back through here,
        // so the forwarding properties below (and the ~500 call sites behind them) stay as they
        // are. See BACKLOG.md "Split pane (F10)".
        // ActiveViewer, NOT Viewer: with two panes this has to be the FOCUSED pane's state, or every
        // forwarding property behind it (view mode, zoom, page maps - ~500 call sites) would keep
        // reporting pane A no matter which pane the user is working in.
        private ViewerState _view => ActiveViewer.State;

        private ViewMode _viewMode { get => _view.Mode; set => _view.Mode = value; }
        private StackPanel _continuousPanel { get => _view.ContinuousPanel; set => _view.ContinuousPanel = value; }
        private System.Threading.CancellationTokenSource? _continuousRenderCts { get => _view.ContinuousRenderCts; set => _view.ContinuousRenderCts = value; }
        private System.Threading.CancellationTokenSource? _continuousSharpenCts { get => _view.ContinuousSharpenCts; set => _view.ContinuousSharpenCts = value; }
        private HashSet<int> _continuousSharpPages => _view.ContinuousSharpPages;
        private int _continuousSharpW { get => _view.ContinuousSharpW; set => _view.ContinuousSharpW = value; }
        private List<double> _continuousTops => _view.ContinuousTops;
        private int _gridScrollToPage { get => _view.GridScrollToPage; set => _view.GridScrollToPage = value; }
        private int _continuousScrollTarget { get => _view.ContinuousScrollTarget; set => _view.ContinuousScrollTarget = value; }
        private double _continuousPageW { get => _view.ContinuousPageW; set => _view.ContinuousPageW = value; }

        // Editing
        private EditTool _currentTool
        {
            get => ActiveViewer?.CurrentToolRef ?? EditTool.Select;
            set { ActiveViewer?.CurrentToolRef = value; }
        }
        // Per-document state. Not readonly: tab switching swaps these by reference so each
        // open document keeps its own annotations, undo history, form values, and search hits.
        private Dictionary<int, List<PageAnnotation>> _annotations { get => ActiveViewer.AnnotationsRef; set => ActiveViewer.AnnotationsRef = value; }
        private Dictionary<int, (int w, int h)> _renderDims { get => ActiveViewer.RenderDimsRef; set => ActiveViewer.RenderDimsRef = value; }
        // Stores the PDF /Rotate value for each page.  The temp file used by Docnet has
        // rotation stripped to zero so FPDF_GetPageWidth/Height returns MediaBox dims and
        // the content isn't clipped; RotateBitmap is applied at render time instead.
        private Dictionary<int, int> _pageRotations { get => ActiveViewer.PageRotationsRef; set => ActiveViewer.PageRotationsRef = value; }

        // Form filling, keyed by each field's qualified name.
        private Dictionary<string, string> _formTextValues { get => ActiveViewer.FormTextValuesRef; set => ActiveViewer.FormTextValuesRef = value; }
        private Dictionary<string, string> _formChoiceValues { get => ActiveViewer.FormChoiceValuesRef; set => ActiveViewer.FormChoiceValuesRef = value; }
        private Dictionary<string, bool> _formCheckValues { get => ActiveViewer.FormCheckValuesRef; set => ActiveViewer.FormCheckValuesRef = value; }
        private Dictionary<string, string> _formRadioValues { get => ActiveViewer.FormRadioValuesRef; set => ActiveViewer.FormRadioValuesRef = value; }
        private Dictionary<string, double> _formFontSizes { get => ActiveViewer.FormFontSizesRef; set => ActiveViewer.FormFontSizesRef = value; }
        // Floating font-size stepper shown while a form text field is focused.
        private Border? _formSizeBar { get => ActiveViewer.FormSizeBarRef; set => ActiveViewer.FormSizeBarRef = value; }
        private TextBox? _activeFormTb { get => ActiveViewer.ActiveFormTbRef; set => ActiveViewer.ActiveFormTbRef = value; }
        private string _activeFormName { get => ActiveViewer.ActiveFormNameRef; set => ActiveViewer.ActiveFormNameRef = value; }
        private double _activeFormScale { get => ActiveViewer.ActiveFormScaleRef; set => ActiveViewer.ActiveFormScaleRef = value; }
        private const string FormOverlayTag = "FormFieldOverlay";

        // Undo stack - each entry is either an annotation removal or a full document snapshot.
        // AnnotationGroup removes a specific set of annotations in one step (a text edit = cover + text).
        // UndoKind / UndoEntry live in Models/UndoTypes.cs, not here - the undo stack is pushed from
        // Annotations.cs and TextEditing.cs, which live in the viewer control.
        private Stack<UndoEntry> _undoStack { get => ActiveViewer.UndoStackRef; set => ActiveViewer.UndoStackRef = value; }
        // Redo: inverses captured by Undo_Click land here; any NEW edit clears it (PushUndo).
        // Swapped per tab alongside _undoStack (Tabs.cs) so redo can never replay another document.
        private Stack<UndoEntry> _redoStack { get => ActiveViewer.RedoStackRef; set => ActiveViewer.RedoStackRef = value; }
        // Jump history for Alt+Left / Alt+Right and the mouse back/forward buttons. Page-granular,
        // recorded at the long-jump sites (bookmark, internal link, jump box, Home/End); cleared on
        // document open and tab switch.
        private Stack<int> _navBack => ActiveViewer.NavBackRef;
        private Stack<int> _navForward => ActiveViewer.NavForwardRef;
        private bool _isDrawing { get => ActiveViewer.IsDrawingRef; set => ActiveViewer.IsDrawingRef = value; }
        private Point _drawStart { get => ActiveViewer.DrawStartRef; set => ActiveViewer.DrawStartRef = value; }
        private UIElement? _activePreview { get => ActiveViewer.ActivePreviewRef; set => ActiveViewer.ActivePreviewRef = value; }
        private InkAnnotation? _activeInk { get => ActiveViewer.ActiveInkRef; set => ActiveViewer.ActiveInkRef = value; }
        private TextBox? _activeTextBox { get => ActiveViewer.ActiveTextBoxRef; set => ActiveViewer.ActiveTextBoxRef = value; }
        private PageAnnotation? _selectedAnnotation { get => ActiveViewer.SelectedAnnotationRef; set => ActiveViewer.SelectedAnnotationRef = value; }
        private Border? _selectionBorder { get => ActiveViewer.SelectionBorderRef; set => ActiveViewer.SelectionBorderRef = value; }
        // Shift+click multi-selection (Select tool): extra annotations selected alongside the
        // primary _selectedAnnotation. Each gets its own outline. Delete removes the whole set.
        private List<PageAnnotation> _selectedSet => ActiveViewer.SelectedSetRef;
        private List<Border> _selectionOutlines => ActiveViewer.SelectionOutlinesRef;

        // Draw/Highlight settings
        private Color _drawColor { get => ActiveViewer.DrawColorRef; set => ActiveViewer.DrawColorRef = value; }
        private double _drawWidth { get => ActiveViewer.DrawWidthRef; set => ActiveViewer.DrawWidthRef = value; }
        private byte _drawOpacity { get => ActiveViewer.DrawOpacityRef; set => ActiveViewer.DrawOpacityRef = value; }
        private bool _lineLevel { get => ActiveViewer.LineLevelRef; set => ActiveViewer.LineLevelRef = value; }
        private bool _highlightErase { get => ActiveViewer.HighlightEraseRef; set => ActiveViewer.HighlightEraseRef = value; }
        private bool _drawErase { get => ActiveViewer.DrawEraseRef; set => ActiveViewer.DrawEraseRef = value; }
        private Color _highlightColor { get => ActiveViewer.HighlightColorRef; set => ActiveViewer.HighlightColorRef = value; }
        // Strikethrough / underline lines: opaque red by default.
        private Color _lineAnnotColor { get => ActiveViewer.LineAnnotColorRef; set => ActiveViewer.LineAnnotColorRef = value; }
        private Border? _drawSettingsBar;

        // Text (typewriter) tool settings
        private double _textFontSize { get => ActiveViewer.TextFontSizeRef; set => ActiveViewer.TextFontSizeRef = value; }
        private double _textLetterSpacing { get => ActiveViewer.TextLetterSpacingRef; set => ActiveViewer.TextLetterSpacingRef = value; }
        // Current text-tool typeface and style (mirrors the text bar; carried onto each new/edited box).
        private string _textFontName { get => ActiveViewer.TextFontNameRef; set => ActiveViewer.TextFontNameRef = value; }
        private bool _textBold { get => ActiveViewer.TextBoldRef; set => ActiveViewer.TextBoldRef = value; }
        private bool _textItalic { get => ActiveViewer.TextItalicRef; set => ActiveViewer.TextItalicRef = value; }
        private bool _textStrike { get => ActiveViewer.TextStrikeRef; set => ActiveViewer.TextStrikeRef = value; }
        private bool _textUnderline { get => ActiveViewer.TextUnderlineRef; set => ActiveViewer.TextUnderlineRef = value; }
        // Installed font-family names, sorted, computed once (the text bar rebuilds often).
        private static List<string>? _systemFontNamesCache;
        internal static List<string> SystemFontNames => _systemFontNamesCache ??=
            [.. System.Windows.Media.Fonts.SystemFontFamilies
                .Select(f => f.Source).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
        private bool _suppressSizeSync;   // guards the slider<->size-box two-way binding from feedback loops
        private TextAnnotation? _reeditOriginal { get => ActiveViewer.ReeditOriginalRef; set => ActiveViewer.ReeditOriginalRef = value; }
        // The opaque cover dropped when starting an existing-text edit, awaiting its paired text commit.
        // Held so the text commit can group both into one undo, and so cancel/empty removes the cover.
        private CoverAnnotation? _pendingCover { get => ActiveViewer.PendingCoverRef; set => ActiveViewer.PendingCoverRef = value; }
        // Dirty state captured before the cover was dropped, so undoing the grouped edit restores it.
        private bool _pendingEditWasDirty { get => ActiveViewer.PendingEditWasDirtyRef; set => ActiveViewer.PendingEditWasDirtyRef = value; }
        private Color _textColor { get => ActiveViewer.TextColorRef; set => ActiveViewer.TextColorRef = value; }
        private byte _textOpacity { get => ActiveViewer.TextOpacityRef; set => ActiveViewer.TextOpacityRef = value; }
        private Color _textFillColor { get => ActiveViewer.TextFillColorRef; set => ActiveViewer.TextFillColorRef = value; }
        private const double TextBoxDefaultWidth = 220;  // canvas-unit width of a freshly placed text box
        private Border? _textSettingsBar { get => ActiveViewer.TextSettingsBarRef; set => ActiveViewer.TextSettingsBarRef = value; }

        // Signature / image resize
        private bool _isResizingSig { get => ActiveViewer.IsResizingSigRef; set => ActiveViewer.IsResizingSigRef = value; }
        private Point _resizeSigStart { get => ActiveViewer.ResizeSigStartRef; set => ActiveViewer.ResizeSigStartRef = value; }
        private double _resizeSigStartScale { get => ActiveViewer.ResizeSigStartScaleRef; set => ActiveViewer.ResizeSigStartScaleRef = value; }
        private PlacedAnnotation? _resizeSigAnnot { get => ActiveViewer.ResizeSigAnnotRef; set => ActiveViewer.ResizeSigAnnotRef = value; }
        private TextAnnotation? _resizeTextAnnot { get => ActiveViewer.ResizeTextAnnotRef; set => ActiveViewer.ResizeTextAnnotRef = value; }
        private HighlightAnnotation? _resizeHlAnnot { get => ActiveViewer.ResizeHlAnnotRef; set => ActiveViewer.ResizeHlAnnotRef = value; }
        private InkAnnotation? _resizeInkAnnot { get => ActiveViewer.ResizeInkAnnotRef; set => ActiveViewer.ResizeInkAnnotRef = value; }
        private List<Point>? _resizeInkOrigPoints { get => ActiveViewer.ResizeInkOrigPointsRef; set => ActiveViewer.ResizeInkOrigPointsRef = value; }
        private Rect _resizeInkOrigBounds { get => ActiveViewer.ResizeInkOrigBoundsRef; set => ActiveViewer.ResizeInkOrigBoundsRef = value; }
        private List<Rectangle> _resizeHandles => ActiveViewer.ResizeHandlesRef;
        private string _resizeCorner { get => ActiveViewer.ResizeCornerRef; set => ActiveViewer.ResizeCornerRef = value; }
        private Point _resizeAnchor { get => ActiveViewer.ResizeAnchorRef; set => ActiveViewer.ResizeAnchorRef = value; }

        // Mid-edit resize handles: 4 corners shown around the live editing TextBox so the user can
        // resize the box (and continue typing) without committing and re-selecting first.
        private List<Rectangle> _textEditHandles => ActiveViewer.TextEditHandlesRef;
        private bool _draggingTextEditHandle { get => ActiveViewer.DraggingTextEditHandleRef; set => ActiveViewer.DraggingTextEditHandleRef = value; }
        private string _tehCorner { get => ActiveViewer.TehCornerRef; set => ActiveViewer.TehCornerRef = value; }
        private Point _tehAnchor { get => ActiveViewer.TehAnchorRef; set => ActiveViewer.TehAnchorRef = value; }
        private TextBox? _tehBox { get => ActiveViewer.TehBoxRef; set => ActiveViewer.TehBoxRef = value; }

        // Placed annotation drag-to-move
        private bool _isDraggingAnnot { get => ActiveViewer.IsDraggingAnnotRef; set => ActiveViewer.IsDraggingAnnotRef = value; }
        private Point _dragAnnotStart { get => ActiveViewer.DragAnnotStartRef; set => ActiveViewer.DragAnnotStartRef = value; }

        // Middle-mouse / spacebar pan
        private bool _spaceHeld;
        private Point _dragAnnotOrigPos { get => ActiveViewer.DragAnnotOrigPosRef; set => ActiveViewer.DragAnnotOrigPosRef = value; }
        private PageAnnotation? _dragAnnot { get => ActiveViewer.DragAnnotRef; set => ActiveViewer.DragAnnotRef = value; }

        // Crop tool
        private Rect _cropCanvasRect { get => ActiveViewer.CropCanvasRectRef; set => ActiveViewer.CropCanvasRectRef = value; }
        private Rectangle? _cropPreviewRect { get => ActiveViewer.CropPreviewRectRef; set => ActiveViewer.CropPreviewRectRef = value; }
        private Rectangle? _cropPreviewRectBorder { get => ActiveViewer.CropPreviewRectBorderRef; set => ActiveViewer.CropPreviewRectBorderRef = value; }
        private List<System.Windows.Shapes.Path> _cropBrackets => ActiveViewer.CropBracketsRef;
        private Border? _cropConfirmBar { get => ActiveViewer.CropConfirmBarRef; set => ActiveViewer.CropConfirmBarRef = value; }
        private readonly Button _toolCropBtn = null!;
        private readonly Button _toolRotateBtn = null!;
        private readonly Button _toolMeasureBtn = null!;
        private List<Rectangle> _cropHandles => ActiveViewer.CropHandlesRef;
        private string? _activeCropHandleTag { get => ActiveViewer.ActiveCropHandleTagRef; set => ActiveViewer.ActiveCropHandleTagRef = value; }
        private Point _cropHandleDragStart { get => ActiveViewer.CropHandleDragStartRef; set => ActiveViewer.CropHandleDragStartRef = value; }
        private Rect _cropRectAtHandleDrag { get => ActiveViewer.CropRectAtHandleDragRef; set => ActiveViewer.CropRectAtHandleDragRef = value; }
        private TextBox? _cropXBox { get => ActiveViewer.CropXBoxRef; set => ActiveViewer.CropXBoxRef = value; }
        private TextBox? _cropYBox { get => ActiveViewer.CropYBoxRef; set => ActiveViewer.CropYBoxRef = value; }
        private TextBox? _cropWBox { get => ActiveViewer.CropWBoxRef; set => ActiveViewer.CropWBoxRef = value; }
        private TextBox? _cropHBox { get => ActiveViewer.CropHBoxRef; set => ActiveViewer.CropHBoxRef = value; }
        private TextBox? _cropRangeBox { get => ActiveViewer.CropRangeBoxRef; set => ActiveViewer.CropRangeBoxRef = value; }
        private string _cropUnit { get => ActiveViewer.CropUnitRef; set => ActiveViewer.CropUnitRef = value; }
        private bool _updatingCropInputs { get => ActiveViewer.UpdatingCropInputsRef; set => ActiveViewer.UpdatingCropInputsRef = value; }

        // PDF link overlays (rendered on top of the annotation canvas)

        // Sidebar + multi-page view
        private bool _sidebarCollapsed;
        private bool _sidebarRight;   // false = sidebar on the left (default), true = on the right
        private bool   _sidebarShowingNotes;
        private double _savedPagesWidth    = 180;
        private double _savedNotesWidth    = 300;
        private readonly Button _sidebarToggleBtn = null!;
        private readonly Border _sidebarBorder = null!;
        private ColumnDefinition _sidebarCol = null!;   // sized column (left or right per _sidebarRight)
        private WrapPanel _pageContentPanel { get => _view.PageContentPanel; set => _view.PageContentPanel = value; }

        // Text selection
        private Rectangle? _pairedCoverOutline { get => ActiveViewer.PairedCoverOutlineRef; set => ActiveViewer.PairedCoverOutlineRef = value; }
        private Rectangle? _reeditCoverOutline { get => ActiveViewer.ReeditCoverOutlineRef; set => ActiveViewer.ReeditCoverOutlineRef = value; }
        private string? _selectedText { get => ActiveViewer.SelectedTextRef; set => ActiveViewer.SelectedTextRef = value; }

        // Search
        private Border? _searchBar;
        private TextBox? _searchBox;
        private TextBlock? _searchStatus;
        private readonly List<Rect> _searchHighlights = [];

        // Signatures
        private readonly SignatureStore _signatureStore = new();
        private SavedSignature? _pendingSignature { get => ActiveViewer.PendingSignatureRef; set => ActiveViewer.PendingSignatureRef = value; }
        private Border? _signaturePopup;
        // Guided AcroForm signing: "pick once, reuse" - the chosen signature/initials are remembered
        // and dropped into every matching field. _pendingSignField, when set, routes the next pick from
        // the popup into that field instead of free placement.
        private SavedSignature? _activeSignatureChoice;
        private SavedSignature? _activeInitialsChoice;
        private (bool Initials, int ObjNum, int Page, double X, double Y, double W, double H)? _pendingSignField;
        // Form fields already signed, so re-clicking one offers change/remove instead of re-stamping.
        private readonly Dictionary<int, SignatureAnnotation> _signedFields = [];

        // Manual element refs. Tile-0's Image + overlay are built in code (BuildPrimaryTile) now that the
        // primary page is no longer a hardcoded XAML singleton - both are reassignable.
        private Canvas _annotationCanvas { get => _view.AnnotationCanvas; set => _view.AnnotationCanvas = value; }
        private Image PageImage { get => _view.PageImage; set => _view.PageImage = value; }
        // Active annotation surface. Single view: always _annotationCanvas. Continuous view:
        // set on mouse-down to the clicked page's overlay. Shared handlers target this.
        private Canvas _activeCanvas { get => _view.ActiveCanvas; set => _view.ActiveCanvas = value; }
        // The page surface a pointer gesture started on, captured on mouse-down. Kept separate
        // from _activeCanvas because RenderAllAnnotations reuses _activeCanvas as its render
        // target; in Grid view tiles stream in asynchronously and each one re-points _activeCanvas
        // mid-gesture, which previously committed annotations to the wrong page and broke
        // select/delete. Mouse-move/up resolve the gesture page and surface from these instead.
        private Canvas? _gestureCanvas { get => _view.GestureCanvas; set => _view.GestureCanvas = value; }
        private int _gesturePage { get => _view.GesturePage; set => _view.GesturePage = value; }
        // Per-page overlay canvases for Continuous view, keyed by page index.
        private Dictionary<int, Canvas> _continuousCanvases => _view.ContinuousCanvases;
        // Unified page -> overlay map covering EVERY rendered page, the primary included (unlike
        // _continuousCanvases, which holds only secondary tiles and is driven by the tile-recycling
        // machinery). This is the single source of truth the canvas accessors read from, so the
        // primary stops being a special case in routing/search/links.
        private Dictionary<int, Canvas> _pages => _view.Pages;
        private Grid _pageContentGrid { get => _view.PageContentGrid; set => _view.PageContentGrid = value; }
        // The page this view is showing. See ViewerState.CurrentPage for why this exists at all
        // (reading the sidebar's SelectedIndex as the current page cannot survive a second pane).
        // The setter drives the sidebar, which is what actually triggers navigation today via
        // PageList_SelectionChanged; the handler mirrors the value
        // straight back, so the two never disagree.
        private int _currentPage
        {
            get => _view.CurrentPage;
            set { _view.CurrentPage = value; if (PageList.SelectedIndex != value) PageList.SelectedIndex = value; }
        }
        private readonly Button _toolSelectBtn = null!;
        private readonly Button _toolFormFieldBtn = null!;
        private readonly Button _toolTextBtn = null!;
        private readonly Button _toolHighlightBtn = null!;
        private readonly Button _toolUnderlineBtn = null!;
        private readonly Button _toolDrawBtn = null!;
        private readonly Button _toolShapeBtn = null!;
        private readonly Button _toolSignatureBtn = null!;
        private readonly Button _toolImageBtn = null!;
        private readonly Button _saveAsBtnRef = null!;
        private readonly Button _closeFileBtnRef = null!;
        private readonly ComboBox _zoomBox = null!;
        private readonly TextBox _pageJumpBox = null!;
        private readonly TextBlock _pageTotalLabel = null!;
        /// <summary>The sidebar list and the total above it, so the two cannot be set apart.</summary>
        private readonly Controls.SidebarPageBinding _sidebarPages = null!;

        // Dirty / unsaved-change tracking
        private bool _isDirty { get => ActiveViewer.IsDirtyRef; set => ActiveViewer.IsDirtyRef = value; }

        // Whole-document search results now live on SearchController (Features/Search); Tabs.cs
        // parks and restores them per tab through its AllSearchRects/ResultPages/PageCursor.

        public MainWindow()
        {
            InitializeComponent();
            // The Discord presence must never introduce a repaired temp copy
            // (killerpdf_repaired_{guid}.pdf) as a book: when the controller
            // holds a temp-like path it asks the window for the real one, and
            // _originalFile - the display path the open flow recorded - is
            // the ground truth it gets.
            Features.Discord.DiscordRpcController.RealPathResolver = ResolveRealDocumentPath;
            // The toolbar's default view vs the extended tool set (Tools switch).
            _toolsMode = Services.AppDataPaths.GetSetting("toolbar.tools") == "1";
            ApplyToolsMode();
            // Companion-window memory: which AI surfaces were open at last close.
            // Same store the session restore reads (OpenTabs), so a "close my tabs"
            // answer and these flags always agree about what a launch shows. Only
            // the chat rail comes back: the summary navigator follows the PDF,
            // not the app, so every launch opens with it closed.
            _restoreChatOpen = App.GetSetting("ui.chat.open") == "1";
            VersionLabel.Text = $"v{AppVersion.Display}";
            // Accept dropped files/folders/archives anywhere on the window (not just the empty drop zone),
            // so dropping onto an open document works too. The empty-state DropZone marks its own drop
            // handled, so a drop there isn't processed twice.
            AllowDrop = true;
            DragOver += DropZone_DragOver;
            Drop     += DropZone_Drop;
            // Safety net: if the window loses focus mid-drag/resize (e.g. Alt-Tab away to type elsewhere),
            // the mouse-up can be lost and the dragged annotation would stay glued to the cursor with the
            // canvas still holding mouse capture. End any in-progress gesture on deactivate so control is
            // restored the moment the user comes back.
            Deactivated += (_, _) => { if (_isDraggingAnnot || _isResizingSig) FinishStuckGesture(); };
            // v1.19.36: an extension popup is an owned window, so a minimized app
            // merely HIDES it - the reader would come back to a bubble that should
            // have died with its click-away. Whatever actually minimizes the
            // window (the caption button, Win+D, a shell that skips WM_SYSCOMMAND),
            // the popup's life ends here. The taskbar-toggle route is vetoed in
            // WndProc while the popup is up, so this fires only for a minimize the
            // reader really asked for.
            StateChanged += (_, _) =>
            {
                if (WindowState != WindowState.Minimized) return;
                // v1.19.45: the trace log's spine - every arrival at
                // minimized the WPF surface reports, whatever cut it.
                MinimizeRecorder.Log("main.statechanged", "minimized");
                WebPane.CloseExtActionWindowExt();
            };
            // v1.19.43: the ledger's other half - the main window taking the
            // foreground hands the app back to itself, so the next taskbar
            // click minimizes the old way (Shell/FloatFocusLedger.cs).
            Activated += (_, _) => FloatFocusLedger.NoteMainActivated();
            // v1.19.45: the flight recorder rides with the main window - one
            // install, three WinEvent channels, one log. The main window's
            // own deactivation is recorded too: the click-away's first foot
            //step is the foreground leaving this window.
            MinimizeRecorder.Install();
            Deactivated += (_, _) => MinimizeRecorder.Log("main.deactivated", "");
            // These three live inside the PdfViewer control, and a UserControl is its own
            // namescope - FindName would return NULL SILENTLY rather than throw, so the failure
            // would surface much later as an unrelated NullReference. Take them off the control
            // directly; they land in ViewerState through the forwarding properties.
            // Both panes get an Owner and pane A becomes the active one (Shell/SplitPane.cs).
            // Replaces the single `Viewer.Owner = this` - ViewerB exists from startup, collapsed,
            // so its bridge would NullReference the moment anything touched it otherwise.
            InitSplitPanes();
            WirePageListEdgeFades();   // sidebar page-list edge fades (SidebarLayout.cs)
            // A reader scrolling or clicking the page list by hand takes the
            // scroll back from the range glide (NavigateSummaryRangeStart's
            // sidebar animation).
            PageList.PreviewMouseWheel += (_, _) => StopSidebarGlide();
            PageList.PreviewMouseLeftButtonDown += (_, _) => StopSidebarGlide();
            HookExternalLangReload();  // #211: --lang-file live reload rebuilds code-built captions
            _pageContentGrid  = ActiveViewer.PageGrid;
            _pageContentPanel = ActiveViewer.PageHost;
            _continuousPanel  = ActiveViewer.ContinuousHost;
            _toolSelectBtn = (Button)FindName("ToolSelectBtn")!;
            _toolFormFieldBtn = (Button)FindName("ToolFormFieldBtn")!;
            _toolTextBtn = (Button)FindName("ToolTextBtn")!;
            _toolHighlightBtn = (Button)FindName("ToolHighlightBtn")!;
            _toolUnderlineBtn = (Button)FindName("ToolUnderlineBtn")!;
            _toolDrawBtn = (Button)FindName("ToolDrawBtn")!;
            _toolShapeBtn = (Button)FindName("ToolShapeBtn")!;
            _toolSignatureBtn = (Button)FindName("ToolSignatureBtn")!;
            _toolImageBtn = (Button)FindName("ToolImageBtn")!;
            _toolCropBtn = (Button)FindName("ToolCropBtn")!;
            _toolRotateBtn = (Button)FindName("ToolRotateBtn")!;
            _toolMeasureBtn = (Button)FindName("ToolMeasureBtn")!;
            _sidebarToggleBtn = (Button)FindName("SidebarToggleBtn")!;
            _sidebarBorder = (Border)FindName("SidebarBorder")!;
            _sidebarCol = (ColumnDefinition)FindName("SidebarCol")!;
            // BuildPrimaryTile + _activeCanvas were here. Both panes now do it for themselves in
            // InitSplitPanes above (PdfViewer.InitTiles) - calling it here as well would build pane
            // A a SECOND tile.
            _saveAsBtnRef = (Button)FindName("SaveAsBtn")!;
            _closeFileBtnRef = (Button)FindName("CloseFileBtn")!;
            _zoomBox = (ComboBox)FindName("ZoomBox")!;
            // Read-only editable combo: hide its text-selection highlight so the displayed % never
            // looks like selected text after a pick.
            _zoomBox.Loaded += (_, _) =>
            {
                if (_zoomBox.Template?.FindName("PART_EditableTextBox", _zoomBox) is TextBox etb)
                    etb.SelectionBrush = System.Windows.Media.Brushes.Transparent;
            };
            _pageJumpBox = (TextBox)FindName("PageJumpBox")!;
            _pageTotalLabel = (TextBlock)FindName("PageTotalLabel")!;
            _sidebarPages = new Controls.SidebarPageBinding(PageList, _pageTotalLabel);
            // Both panes: each tracks the page under its own viewport. Pane B was never wired, so
            // its page counter, jump box and sidebar selection never moved as it scrolled.
            Viewer.WireScrollChanged();      // handler moved into the control with the render pipeline
            ViewerB.WireScrollChanged();
            PreviewMouseDown += NavHistory_PreviewMouseDown;        // mouse back/forward buttons retrace jumps
            MainContentGrid.SizeChanged += (_, _) => ScheduleFadeRefresh();
            // The sidebar column resizes via the splitter / collapse; track its width so the tab-strip
            // shadow gradient stays clipped to the document column.
            if (FindName("SidebarOuterGrid") is FrameworkElement sidebarOuter)
                sidebarOuter.SizeChanged += (_, _) => ScheduleFadeRefresh();
            // The footer shadow tracks the document pane's actual position; re-anchor when it (or the
            // tab strip, which shifts the document) changes size.
            DocPaneBorder.SizeChanged += (_, _) => ScheduleFadeRefresh();
            Viewer.SizeChanged += (_, _) => ScheduleFadeRefresh();
            ViewerB.SizeChanged += (_, _) => ScheduleFadeRefresh();
            TabStripBorder.SizeChanged += (_, _) => { ScheduleFadeRefresh(); ScheduleTabReflow(); };
            // After a sidebar-splitter drag, snap fully closed if dragged too narrow, else save the width.
            SidebarSplitter.PreviewMouseLeftButtonUp += (_, _) => OnSidebarResized();
            // Grabbing the splitter while collapsed reveals the page list so it can be pulled open.
            SidebarSplitter.PreviewMouseLeftButtonDown += (_, _) => { _sidebarWantClose = false; OnSidebarSplitterPress(); };
            // Pull the splitter well past the minimum mid-drag to close (the column itself can't clip).
            SidebarSplitter.PreviewMouseMove += OnSidebarSplitterMove;
            // If a drag is interrupted (alt-tab, focus loss, taking a screenshot), finalize it so the
            // sidebar can't get stuck half-open with its content hidden.
            SidebarSplitter.LostMouseCapture += (_, _) => OnSidebarResized();
            if (Enum.TryParse<ViewMode>(App.GetSetting("ViewMode"), out var savedVm))
                _viewMode = savedVm;
            InitToolbarStyle();   // two-axis toolbar appearance (+ migration from the old five-way key)
            InitAppScale();   // AppScale.cs: restore the app-wide size (scroll the logo to change it)
            // #135: document dark mode. Per PANE now; the saved setting seeds the primary pane
            // (a split's second pane starts normal - inverting it is a per-pane choice).
            Viewer.DocInvert = App.GetSetting("DocInvert") == "1";
            BitmapHelpers.DocInvertImages = App.GetSetting("DocInvertImages") == "1";   // moon right-click opt-in
            DocInvertBtn.Tag = Viewer.DocInvert ? "on" : null;              // rail moon lit while active
            // #146: the privacy toggle lives in the About window; init once - only its own
            // handler changes it afterwards (change-guarded, so this init is a no-op there).
            NoRecentCheck.IsChecked = App.GetSetting(App.NoRecentFilesSetting) == "1";
            // Same deal for the link-confirm toggle beside it (default off).
            LinkConfirmCheck.IsChecked = App.GetSetting(ConfirmLinksSetting) == "1";
            if (string.Equals(App.GetSetting("SidebarSide"), "Right", StringComparison.OrdinalIgnoreCase))
                _sidebarRight = true;
            RestoreToolSettings();   // Draw + Text tool styles carry across sessions
            Loaded += (_, _) => AdjustZoomBoxWidth();   // fit the zoom box to the longest localized term
            IndexToolbarButtons();
            WireWebPane();
            WireEditorPane();
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
            ApplyGrainTexture();
            ApplyToolNumberTooltips();   // append the 1-9 toolbar positions to the tool tooltips
            BuildShortcutsOverlay();     // generate the shortcuts card from the single-source table (ShortcutsOverlay.cs)
            SourceInitialized += MainWindow_SourceInitialized;
            Closed += (_, _) => { _continuousRenderCts?.Cancel(); _doc?.Close(); CloseEngineDocumentSession(); App.CleanupSessionTemps(); WebPane.ShutdownForExit(); TextPane.ShutdownForExit(); };

            // Open a file passed via command-line / file association (e.g. double-clicking a .pdf)
            // Also show the portable badge when running outside the install location.
            bool contentRevealed = false;
            bool startupSidebarSynced = false;
            ContentRendered += (_, _) =>
            {
                Services.StartupTrace.Mark("MainWindow first ContentRendered");
                Services.ThemeManager.RefreshIcons();
                // Startup can restore pane B, process a relaunch/open-file handoff, and change
                // ActiveViewer several times before the first frame. Each transition is valid on
                // its own, but the shared sidebar ItemsSource can still be the preceding pane's
                // cache (or null) when layout finally wins the race. A click appeared to "bring
                // thumbnails back" because FocusPane performs this same synchronization. Do it
                // once here for the pane whose focus halo actually reaches the screen.
                if (!startupSidebarSynced)
                {
                    startupSidebarSynced = true;
                    RestorePageListForActivePane();
                }
                // Final pass once the layout has real widths. The tab-strip / footer shadow gradients
                // were intermittently blank at startup (their feather mask + margin were computed
                // before the sidebar column had measured), and only a manual sidebar tweak forced a
                // correct re-layout. Re-running it here reproduces that fix automatically.
                UpdateTabStripFade();
                // The content is held invisible (RootClipGrid.Opacity=0 in XAML) until this final
                // positioning pass has run; fade it in once so the brief unpositioned first frame
                // (the "load deform" - shadows/toolbars snapping into place) is never visible.
                if (!contentRevealed)
                {
                    contentRevealed = true;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                    {
                        var reveal = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                            new Duration(TimeSpan.FromMilliseconds(140)));
                        RootClipGrid.BeginAnimation(OpacityProperty, reveal);
                        Services.StartupTrace.Mark("MainWindow ready");
                    }));
                }
            };
            Services.ThemeManager.ThemeChanged += OnThemeChanged;

            Loaded += (_, _) =>
            {
                RestoreWindowSettings();
                ApplySidebarSide();   // place the sidebar on the saved side (default left)
                BuildToolbarMenu();   // right-click appearance picker on the toolbar
                // Unconditional: the XAML default is small icons / no text, but the family default
                // is Large/Under, so a first run needs the apply pass too.
                ApplyToolbarAppearance();

                // The chat rail was open at last close: bring it straight back before
                // the session restore lands, so the incoming document binds into a
                // visible rail through HandleDocumentSwitch like on any other open.
                if (_restoreChatOpen) OpenAiChat();

                FlushPendingExternalOpen();   // a forward that landed before the panes were wired

                var args = Environment.GetCommandLineArgs();
                // #267 follow-up: route every killerpdf: launch to OpenFromExternal, valid or not.
                // Gating on a usable target here swallowed a refused handoff before anything could
                // report it, and restored the last session instead, so a cold launch looked like a
                // handoff that had simply done nothing.
                if (args.Length > 1 && (System.IO.File.Exists(args[1]) ||
                    Services.ProtocolRegistrar.IsHandoffLaunch(args[1])))
                {
                    OpenFromExternal(args[1]);
                }
                else
                {
                    // Reopen every tab from the last session (falls back to the single LastFile for
                    // settings written before multi-tab restore existed).
                    var saved = App.GetSetting("OpenTabs");
                    string[] paths = !string.IsNullOrEmpty(saved)
                        ? saved!.Split('|')
                        : (App.GetSetting("LastFile") is { Length: > 0 } lf ? [lf] : []);
                    // Lazy restore: create a placeholder tab for each saved file but load only the
                    // focused one. The rest materialize (load + render) the first time they're clicked,
                    // so startup cost no longer scales with how many tabs were open last session.
                    // Built as a local list and handed to the pane, rather than mutating _sessions
                    // in place: the session list belongs to a PdfViewer, so the restore has to say
                    // WHICH pane. `OpenTabs` / `ActiveTab` are pane A; pane B is restored from
                    // `OpenTabsB` / `ActiveTabB` by RestorePaneB below, once the split is reopened.
                    var restored = new List<Controls.PdfViewer.DocumentSession>();
                    foreach (var f in paths)
                        if (!string.IsNullOrEmpty(f) && System.IO.File.Exists(f))
                            restored.Add(Controls.PdfViewer.MakeDeferredSession(f));

                    if (restored.Count == 0)
                    {
                        SetRestoredSessions(restored, null);
                        PopulateRecentFilesList();   // empty state: show the recent list
                        EnsureInitialSession();
                        RebuildTabStrip();
                    }
                    else
                    {
                        var wantActive = App.GetSetting("ActiveTab");
                        var activeTarget = (!string.IsNullOrEmpty(wantActive)
                                ? restored.FirstOrDefault(ss => string.Equals(ss.OriginalFile, wantActive, StringComparison.OrdinalIgnoreCase))
                                : null)
                            ?? restored[0];
                        SetRestoredSessions(restored, activeTarget);
                        ApplySessionState(activeTarget);
                        MaterializeDeferred(activeTarget);   // load + render only the focused tab
                        RebuildTabStrip();
                    }
                    RestorePaneB();
                }

                // The pane that led last leads again (v1.19.86): the
                // editor or the browser reclaims the floor a beat after
                // the session settles; anything else means the book was
                // showing, and a file handed on the command line still
                // wins - this rides only the plain-restore path.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var lastPane = App.GetSetting("LastLeadPane");
                    if (lastPane == "editor") ShowEditorPane();
                    else if (lastPane == "browser") ShowWebPane();
                }), System.Windows.Threading.DispatcherPriority.Background);

                // Start with the sidebar collapsed when no PDF is open (nothing to show); a document
                // opened above will have expanded it via FinishOpenFile.
                SyncSidebarToDocState(hasDoc: _doc != null, startup: true);
                Dispatcher.BeginInvoke(new Action(() => About.CheckOnStartup()),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        // ============================================================
        // Maximize-respects-taskbar fix (WindowStyle=None needs WM_GETMINMAXINFO)
        // ============================================================

        // "Avalanche (<short-sha>)" - CI stamps the commit into InformationalVersion
        // (1.8.71+<sha>). Portable exe copies pile up in Downloads looking identical
        // and the UI never showed which build was running, so the hash is surfaced
        // on the title bar and mirrored into ai-highlight.log session headers.
        internal static string BuildDisplayTitle()
        {
            const string baseTitle = "Avalanche";
            try
            {
                var entry = System.Reflection.Assembly.GetEntryAssembly();
                var attr = (System.Reflection.AssemblyInformationalVersionAttribute?)(entry is null
                                ? null
                                : System.Attribute.GetCustomAttribute(
                                    entry,
                                    typeof(System.Reflection.AssemblyInformationalVersionAttribute)));
                var info = attr?.InformationalVersion;
                if (string.IsNullOrWhiteSpace(info)) return baseTitle;
                var plus = info.IndexOf('+');
                var id = plus >= 0 && plus < info.Length - 1 ? info[(plus + 1)..] : info;
                return $"{baseTitle} ({id})";
            }
            catch
            {
                return baseTitle;
            }
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            InitializeTaskbarIcons(hwnd);
            ThemeManager.ApplyDwm(hwnd);
            // Black-client-area-after-minimize/resume guard (Shell/WindowChrome.cs +
            // Shell/SurfaceResurrection.cs): repaint on system resume and display changes, not
            // just on the minimize -> restore transition. SessionSwitch covers unlock, and the
            // registered power notifications cover monitor off/on - which no managed event sees.
            SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
            SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;
            SystemEvents.SessionSwitch += OnSystemSessionSwitch;
            RegisterDisplayWakeNotifications(hwnd);
            // SurfaceHealth (Shell/SurfaceHealth.cs): closed-loop black-surface defense. The
            // event-driven resurrection above cannot see every possible trigger (GPU TDR,
            // HDR toggle, driver quirks), so the window also verifies its own rendered
            // output every 10s and runs an escalation ladder when it detects black.
            StartSurfaceHealth();
            Title = BuildDisplayTitle();
            // Snapping moves the window without changing WindowState, so re-evaluate the rounded vs
            // squared chrome on every move (and once now that the handle exists).
            LocationChanged += OnWindowLocationChanged;
            UpdateWindowChrome();
        }

        // ============================================================
        // Settings persistence (window size, zoom, last file)
        // ============================================================

        private void SaveWindowSettings()
        {
            try
            {
                App.SetSetting("WindowState", WindowState.ToString());
                if (WindowState == WindowState.Normal)
                {
                    App.SetSetting("WindowWidth",  ((int)ActualWidth).ToString());
                    App.SetSetting("WindowHeight", ((int)ActualHeight).ToString());
                    App.SetSetting("WindowTop",  ((int)Top).ToString());
                    App.SetSetting("WindowLeft", ((int)Left).ToString());
                }
                App.SetSetting("FitMode",   _fitMode.ToString());
                App.SetSetting("ZoomLevel", _zoomLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
                // #105: honor the "remember open files" privacy choice. Unset or "1" = remember
                // (default, preserves prior behavior); "0" = forget the session so nothing persists.
                bool rememberFiles = App.GetSetting("RememberOpenFiles") != "0";
                if (rememberFiles)
                {
                    if (_currentFile is not null)
                        App.SetSetting("LastFile", _currentFile);
                    else
                        App.RemoveSetting("LastFile");
                    // Remember every open tab so the whole session restores next launch. Manually-closed
                    // tabs are already gone from _sessions, so they won't come back (Issue #75 still holds).
                    // Saved PER PANE. `OpenTabs` / `ActiveTab` keep their old meaning - pane A - so
                    // settings written before the split existed still restore; pane B gets its own
                    // `OpenTabsB` / `ActiveTabB`, and the split itself gets `SplitOpen` and the
                    // divider position. Without this a split window reopened with everything
                    // stacked in pane A.
                    static List<string> FilesOf(Controls.PdfViewer pane) => [.. pane.SessionsRef
                        .Select(ss => ss.OriginalFile)
                        .Where(f => !string.IsNullOrEmpty(f) && System.IO.File.Exists(f))
                        .Distinct()
                        .Select(f => f!)];

                    static void SavePane(string tabsKey, string activeKey,
                                         List<string> files, Controls.PdfViewer pane)
                    {
                        if (files.Count > 0) App.SetSetting(tabsKey, string.Join("|", files));
                        else                 App.RemoveSetting(tabsKey);
                        if (pane.ActiveSessionRef?.OriginalFile is { Length: > 0 } af
                            && System.IO.File.Exists(af)) App.SetSetting(activeKey, af);
                        else                              App.RemoveSetting(activeKey);
                    }

                    SavePane("OpenTabs",  "ActiveTab",  FilesOf(Viewer),  Viewer);
                    SavePane("OpenTabsB", "ActiveTabB", FilesOf(ViewerB), ViewerB);

                    if (IsSplit)
                    {
                        App.SetSetting("SplitOpen", "1");
                        // The pixel width of pane A - the fixed column; pane B is star-sized and
                        // takes the remainder, so one number describes the divider whatever the
                        // window is resized to. (Used to save pane B's width instead, which is the
                        // derived/remainder side and not what the restore path needs to seed pane A
                        // with before the window has laid out - #161, split pane not remembering
                        // its size across a restart.)
                        double aw = Viewer.ActualWidth;
                        if (aw > 0) App.SetSetting("SplitPaneAWidth",
                            aw.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        App.RemoveSetting("SplitOpen");
                    }
                }
                else
                {
                    // Privacy: drop any remembered session so no file paths linger on disk.
                    App.RemoveSetting("LastFile");
                    App.RemoveSetting("OpenTabs");
                    App.RemoveSetting("ActiveTab");
                    App.RemoveSetting("OpenTabsB");
                    App.RemoveSetting("ActiveTabB");
                    App.RemoveSetting("SplitOpen");
                }
                PersistToolSettings();
                // The active tab may not have been captured yet at exit; persist its view state directly.
                if (_active != null)
                    SaveDocState(_originalFile, _fitMode, _zoomLevel, _viewMode,
                        _active.PageIndex,
                        _active.ScrollH, _active.ScrollV);
            }
            catch { /* best-effort */ }
        }

        // Persist the Draw and Text tool styles so they carry across sessions. The eraser toggle is
        // deliberately NOT saved (it's a transient mode, not a style).
        private void PersistToolSettings()
        {
            try
            {
                App.SetSetting("DrawColor",     ToolColorHex(_drawColor));
                App.SetSetting("DrawWidth",     _drawWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
                App.SetSetting("DrawOpacity",   _drawOpacity.ToString());
                App.SetSetting("TextFontSize",  _textFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                App.SetSetting("TextFontName",  _textFontName);
                App.SetSetting("TextBold",      _textBold ? "1" : "0");
                App.SetSetting("TextItalic",    _textItalic ? "1" : "0");
                App.SetSetting("TextStrike",    _textStrike ? "1" : "0");
                App.SetSetting("TextUnderline", _textUnderline ? "1" : "0");
                App.SetSetting("TextColor",     ToolColorHex(_textColor));
                App.SetSetting("TextOpacity",   _textOpacity.ToString());
                App.SetSetting("TextFillColor", ToolColorHexA(_textFillColor));
            }
            catch { /* best-effort */ }
        }

        private void RestoreToolSettings()
        {
            try
            {
                if (ParseToolColor(App.GetSetting("DrawColor")) is Color dc) _drawColor = dc;
                if (double.TryParse(App.GetSetting("DrawWidth"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double dw) && dw > 0) _drawWidth = dw;
                if (byte.TryParse(App.GetSetting("DrawOpacity"), out byte dop)) _drawOpacity = dop;

                if (double.TryParse(App.GetSetting("TextFontSize"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double tfs) && tfs > 0) _textFontSize = tfs;
                if (App.GetSetting("TextFontName") is { Length: > 0 } tfn) _textFontName = tfn;
                _textBold      = App.GetSetting("TextBold") == "1";
                _textItalic    = App.GetSetting("TextItalic") == "1";
                _textStrike    = App.GetSetting("TextStrike") == "1";
                _textUnderline = App.GetSetting("TextUnderline") == "1";
                if (ParseToolColor(App.GetSetting("TextColor")) is Color tc) _textColor = tc;
                if (byte.TryParse(App.GetSetting("TextOpacity"), out byte top)) _textOpacity = top;
                if (ParseToolColorA(App.GetSetting("TextFillColor")) is Color tfc) _textFillColor = tfc;

                // Keep each color's alpha in sync with its opacity byte (the bars store them coupled).
                _drawColor = Color.FromArgb(_drawOpacity, _drawColor.R, _drawColor.G, _drawColor.B);
                _textColor = Color.FromArgb(_textOpacity, _textColor.R, _textColor.G, _textColor.B);
            }
            catch { /* best-effort */ }
        }

        private static string ToolColorHex(Color c)  => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        private static string ToolColorHexA(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color? ParseToolColor(string? s)
        {
            if (s is null || s.Length != 7 || s[0] != '#') return null;
            try
            {
                return Color.FromRgb(Convert.ToByte(s.Substring(1, 2), 16),
                                     Convert.ToByte(s.Substring(3, 2), 16),
                                     Convert.ToByte(s.Substring(5, 2), 16));
            }
            catch { return null; }
        }

        private static Color? ParseToolColorA(string? s)
        {
            if (s is null || s.Length != 9 || s[0] != '#') return null;
            try
            {
                return Color.FromArgb(Convert.ToByte(s.Substring(1, 2), 16),
                                      Convert.ToByte(s.Substring(3, 2), 16),
                                      Convert.ToByte(s.Substring(5, 2), 16),
                                      Convert.ToByte(s.Substring(7, 2), 16));
            }
            catch { return null; }
        }

        private void RestoreWindowSettings()
        {
            try
            {
                if (int.TryParse(App.GetSetting("WindowWidth"),  out int w) &&
                    int.TryParse(App.GetSetting("WindowHeight"), out int h) && w > 200 && h > 200)
                {
                    Width  = w;
                    Height = h;
                }
                if (int.TryParse(App.GetSetting("WindowTop"),  out int savedTop) &&
                    int.TryParse(App.GetSetting("WindowLeft"), out int savedLeft))
                {
                    // Verify the saved position is visible on the virtual desktop
                    // (covers all monitors). Falls back to CenterScreen (XAML default)
                    // if the monitor it was on is no longer connected.
                    double vLeft   = SystemParameters.VirtualScreenLeft;
                    double vTop    = SystemParameters.VirtualScreenTop;
                    double vRight  = vLeft + SystemParameters.VirtualScreenWidth;
                    double vBottom = vTop  + SystemParameters.VirtualScreenHeight;
                    bool onScreen  = savedLeft + 100 < vRight  && savedLeft + Width  > vLeft
                                  && savedTop  + 50  < vBottom && savedTop  + Height > vTop;
                    if (onScreen)
                    {
                        Left = savedLeft;
                        Top  = savedTop;
                    }
                }
                if (Enum.TryParse<WindowState>(App.GetSetting("WindowState"), out var ws) &&
                    ws == WindowState.Maximized)
                {
                    WindowState = WindowState.Maximized;
                }
                if (double.TryParse(App.GetSetting("ZoomLevel"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double z) && z > 0)
                {
                    _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, z));
                }
                if (Enum.TryParse<FitMode>(App.GetSetting("FitMode"), out var fm))
                    _fitMode = fm;
            }
            catch { /* best-effort */ }
        }

        // ============================================================
        // Core helpers (localization, status, PDF object refs)
        // ============================================================

        /// <summary>Look up a localized string. Falls back to the key name if missing.</summary>
        private static string Loc(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        // A "held" status message briefly wins over routine updates: scrolling the logo to
        // resize the app must show "App size N%", but the chrome resize immediately re-runs
        // the fit pipeline, whose "Page x of y - Fit Page" status stomped it the same frame.
        // While the hold is active, plain SetStatus calls are ignored; the hold refreshes on
        // every wheel notch and expires on its own, after which normal statuses flow again.
        private DateTime _statusHoldUntil = DateTime.MinValue;

        private void SetStatus(string text)
        {
            if (DateTime.UtcNow < _statusHoldUntil) return;   // a held message is showing
            StatusText.Text = text;
            CrashReporter.PushStatusMessage(text);
        }

        private void SetStatusHeld(string text, int holdMs = 1200)
        {
            _statusHoldUntil = DateTime.UtcNow.AddMilliseconds(holdMs);
            StatusText.Text = text;
            CrashReporter.PushStatusMessage(text);
        }

        // Clicking the status line flashes the open document's file size for a beat, then puts
        // back whatever was showing (requested on Reddit). Held so page-change chatter can't
        // overwrite it mid-read; the restore stands down if a newer held message took over.
        private void StatusText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => ShowCurrentFileSize();

        private void ShowCurrentFileSize()
        {
            string? path = _originalFile ?? _currentFile;
            if (path is null || !System.IO.File.Exists(path)) return;
            string prior = StatusText.Text;
            long bytes = new System.IO.FileInfo(path).Length;
            string size = bytes >= 1L << 20 ? $"{bytes / (double)(1 << 20):0.##} MB"
                        : bytes >= 1L << 10 ? $"{bytes / (double)(1 << 10):0.#} KB"
                        : $"{bytes} B";
            SetStatusHeld($"{System.IO.Path.GetFileName(path)} - {size}", 2500);
            var restore = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(2550) };
            restore.Tick += (_, _) =>
            {
                restore.Stop();
                if (DateTime.UtcNow >= _statusHoldUntil) SetStatus(prior);
            };
            restore.Start();
        }

        // ============================================================
        // AI Chat Assistant
        // ============================================================

        private Features.AI.AiChatViewModel? _aiChatViewModel;
        private Features.AI.AiSettingsViewModel? _aiSettingsViewModel;
        private Features.Summary.SummaryWindow? _summaryWindow;
        private Features.Summary.WebSummaryWindow? _webSummaryWindow;
        private Features.AI.AiTestWindow? _aiTestWindow;

        // Companion-window memory: the chat rail reopens with the app when it
        // was open at last close (snapshot in OnClosing). The summary navigator
        // never does - it follows the PDF, not the app.
        private bool _restoreChatOpen;

        // The summary navigator's visibility belongs to the PDF, not to the app:
        // leave a book whose window was open and the window closes with the
        // switch; come back to that book and it opens again, straight into the
        // digest it saved. A fresh PDF - a new tab, a first open, a relaunch -
        // always starts closed. The map lives only as long as the app does, and
        // a closed tab drops its book's entry (DiscardSummaryVisibility, the
        // notes history's own rule).
        // v1.19.34: where the reader had scrolled the digest when the browser
        // parked the navigator - replayed when the book welcomes it back.
        private double _summaryParkedScrollOffset;

        private readonly System.Collections.Generic.Dictionary<string, bool> _summaryOpenByDoc =
            new(StringComparer.OrdinalIgnoreCase);

        // The navigator a document switch is closing: its Closed hook must read
        // that as a switch-away - the book keeps its flag - and never as the
        // reader putting the window down, which is what clears the flag.
        private Features.Summary.SummaryWindow? _summarySwitchAway;
        // v1.19.26: the book whose navigator closed so a search could ride the
        // browser. One-shot: HideWebPane consumes it and brings the navigator
        // back - and only the search ever writes it.
        private string? _summaryHiddenForSearchPath;

        // v1.19.32: a recap companion was showing when the browser took the
        // floor. One-shot the same way: HideWebPane consumes the flag and
        // summons the companion back over the stretch on screen.
        private bool _recapHiddenForBrowser;

        private void AiChatBtn_Click(object sender, RoutedEventArgs e)
        {
            ToggleAiChat();
        }

        private void AiChatCloseBtn_Click(object sender, RoutedEventArgs e)
        {
            CloseAiChat();
        }

        private void AiChatNewChatBtn_Click(object sender, RoutedEventArgs e)
        {
            // Fresh conversation: sweep bubbles, any deferred reply and the
            // citation highlight. The document index is deliberately KEPT so
            // the next question answers immediately instead of re-indexing.
            ClearAiSourceHighlight();
            _aiChatViewModel?.StartNewChat();
        }

        private void ToggleAiChat()
        {
            if (AiChatOverlay is null) return;

            if (AiChatOverlay.Visibility == Visibility.Visible)
            {
                CloseAiChat();
            }
            else
            {
                OpenAiChat();
            }
        }

        /// <summary>Releases the AI chat's DB connection, HTTP clients and
        /// the static citation-click subscription when the window closes.</summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
            SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged;
            SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
            UnregisterDisplayWakeNotifications();
            ShutdownSurfaceHealth();
            // The reader is leaving: clear the Discord presence now (a
            // bounded wait - the write is tiny, but the exit must not hang
            // on a stalled pipe) and tear the client down with the window.
            Features.Discord.DiscordRpcController.Shutdown();
            _summaryWindow?.Close();
            _summaryWindow = null;
            _aiTestWindow?.Close();
            _aiTestWindow = null;
            _aiChatViewModel?.Dispose();
            _aiChatViewModel = null;
        }

        // ============================================================
        // Page Summary window (Features/Summary): exhaustive page-range digests in
        // a floating companion window. Single instance; switching documents recycles it.
        // ============================================================

        // The toolbar button is a switch, not a launcher: it opens the navigator and
        // closes it again (right-click is the position reset - see below).
        private void SummarizeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_summaryWindow is { } open)
            {
                open.Close();       // the Closed hook inside OpenSummaryWindow clears the field
                return;
            }

            OpenSummaryWindow();
        }

        // Right-click on the toolbar button: the summary window returns to its default
        // spot (centered on the main window). Position ONLY - size, and every reading
        // parameter, stay exactly as the reader left them. With no window open, the
        // saved coordinates are dropped so the next open centers again.
        private void SummarizeBtn_RightClick(object sender, RoutedEventArgs e)
        {
            if (_summaryWindow is { } open)
            {
                open.ResetPosition();
                return;
            }

            Services.AppDataPaths.SetSetting("summary.win.left", string.Empty);
            Services.AppDataPaths.SetSetting("summary.win.top", string.Empty);
        }

        // ============================================================
        // Web Summary window (Features/Summary): the browser page's digest in
        // the navigator's stripped replica - Start/Reset, the one prompt, the
        // counts. Single instance; the toolbar button toggles it like the
        // book navigator's.
        // ============================================================

        private void WebSumBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_webSummaryWindow is { } open)
            {
                open.Close();
                return;
            }

            OpenWebSummaryWindow();
        }

        private void OpenWebSummaryWindow()
        {
            _aiSettingsViewModel ??= new Features.AI.AiSettingsViewModel();
            _aiSettingsViewModel.Load();
            var window = new Features.Summary.WebSummaryWindow(
                this,
                () => Features.AI.AiSurfaceModels.Configure(_aiSettingsViewModel!.ToGenConfig(), Features.AI.AiSurface.WebSummary),
                () => WebPane.ActiveTabId,
                (tabId, ct) => WebPane.ExtractPageTextAsync(tabId, ct),
                Loc);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_webSummaryWindow, window))
                {
                    _webSummaryWindow = null;
                }
            };
            _webSummaryWindow = window;
            window.Show();
        }

        // ============================================================
        // AI Context Test (Features/AI): the verification window probing what the
        // model actually saw - tokens audited against the estimate, boundary
        // sentences recalled and fuzzy-compared. Single instance; the toolbar
        // button toggles it exactly like the summary navigator's.
        // ============================================================

        private void AiTestBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_aiTestWindow is { } open)
            {
                open.Close();
                return;
            }

            OpenAiTestWindow();
        }

        // Right-click on the toolbar button: the tester returns to its default
        // spot (centered on the main window) - the summary navigator's own reset
        // rule, applied to the aitest.win.* placement. With no window open, the
        // saved coordinates are dropped so the next open centers again.
        private void AiTestBtn_RightClick(object sender, RoutedEventArgs e)
        {
            if (_aiTestWindow is { } open)
            {
                open.ResetPosition();
                return;
            }

            Services.AppDataPaths.SetSetting("aitest.win.left", string.Empty);
            Services.AppDataPaths.SetSetting("aitest.win.top", string.Empty);
        }

        
        private void OpenAiTestWindow()
        {
            var test = CreateAiTestWindow();
            if (test is null)
            {
                return;
            }

            _aiTestWindow = test;
            test.Show();
        }

        // The window factory behind both openers: the toolbar toggle and the
        // summary navigator's test-this-range chip.
        private Features.AI.AiTestWindow? CreateAiTestWindow()
        {
            if (string.IsNullOrEmpty(_currentFile) || _doc is null)
            {
                SetStatusHeld(Loc("Str_AiTest_NoDoc"));
                return null;
            }

            _aiSettingsViewModel ??= new Features.AI.AiSettingsViewModel();
            _aiSettingsViewModel.Load();
            var test = new Features.AI.AiTestWindow(
                this,
                _currentFile,
                _doc.PageCount,
                // v1.19.32: the tester has its own model dial - it no longer
                // borrows whatever the summary's config happens to say.
                () => Features.AI.AiSurfaceModels.Configure(_aiSettingsViewModel!.ToGenConfig(), Features.AI.AiSurface.AiTester),
                Loc);
            test.Closed += (_, _) =>
            {
                if (ReferenceEquals(_aiTestWindow, test))
                {
                    _aiTestWindow = null;
                }
            };
            return test;
        }

        // The summary navigator's beaker chip: open (or reuse) the tester and
        // start a probe over exactly the stretch the navigator is showing -
        // the range's first page through its last.
        private void OpenAiTestForRange(int first, int last)
        {
            if (_aiTestWindow is { } open && !string.IsNullOrEmpty(_currentFile)
                && open.DocumentPathEquals(_currentFile))
            {
                open.StartRangeTest(first, last);
                open.Activate();
                return;
            }

            _aiTestWindow?.Close();
            _aiTestWindow = null;
            var test = CreateAiTestWindow();
            if (test is null)
            {
                return;
            }

            _aiTestWindow = test;
            test.Show();
            test.StartRangeTest(first, last);
        }


        // ---- Summary reading-range highlight in the page list ------------------------------
        // While the navigator is open, the sidebar highlights every page of the reading
        // range (plus the page you are on when you scrolled outside it); closed, the
        // list returns to the default single-page selection. _summaryHighlighting keeps
        // the list's navigation handler asleep while the range is applied.

        private bool _summaryHighlighting;
        private bool _summaryHighlightQueued;

        // The range highlight lives on the item VMs (IsInRange), so selection
        // changes can no longer touch it. This re-assert still runs on every
        // selection change for the two jobs left: re-seating the flags after
        // anything that rebuilt the list's ItemsSource (a re-assign clears the
        // old VMs' paint with the old selection), and keeping the summary
        // window's pages-left counter live as the reader moves. Queued at Normal
        // priority, so it lands in the same dispatcher round and never reaches
        // the screen late. With no navigator open nothing runs.
        private void QueueSummaryHighlightRefresh()
        {
            if (_summaryHighlightQueued || _summaryWindow is null)
            {
                return;
            }

            _summaryHighlightQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
            {
                _summaryHighlightQueued = false;
                RefreshSummaryPageHighlight();
            }));
        }

        private void RefreshSummaryPageHighlight()
        {
            if (PageList is null)
            {
                return;
            }

            // The pages-left readout rides along with every visual refresh: it counts
            // from the viewer's live page, which any of these paths may have moved.
            _summaryWindow?.NotifyViewerPageChanged();

            if (_summaryWindow is { IsVisible: true } summaryWindow
                && summaryWindow.TryGetVisibleRange(out int first, out int last))
            {
                ApplyPageRangeHighlight(first, last);
            }
            else
            {
                ClearPageRangeHighlight();
            }
        }

        // The stretch the Recap companion covers: the summary navigator's WHOLE
        // displayed range - the memory bridge spans everything the reader is
        // reading, not the one page they just left. The navigator answers live
        // while it is open; once closed, its last range is still on file (the
        // per-document start anchor plus the selected span - 20 pages until a
        // chip says otherwise). With no navigator history at all, the bridge
        // falls back to the single page the reader is sitting on.
        private (int First, int Last) GetRecapRange(int fallbackPage)
        {
            if (_doc is null || string.IsNullOrEmpty(_currentFile))
            {
                fallbackPage = Math.Max(1, fallbackPage);
                return (fallbackPage, fallbackPage);
            }

            int pages = _doc.PageCount;
            if (_summaryWindow is { } summary
                && summary.TryGetVisibleRange(out int first, out int last))
            {
                first = Math.Clamp(first, 1, pages);
                return (first, Math.Clamp(last, first, pages));
            }

            try
            {
                int span = 20;
                string? rawSpan = Services.AppDataPaths.GetSetting("summary.range");
                if (rawSpan is not null
                    && int.TryParse(rawSpan, out int savedSpan)
                    && savedSpan >= 1)
                {
                    span = savedSpan;
                }

                string? rawStart = Services.AppDataPaths.GetSetting(
                    "summary.start." + Features.AI.DocumentIndexer.ComputeDocumentId(_currentFile));
                if (rawStart is not null
                    && int.TryParse(rawStart, out int start)
                    && start >= 1)
                {
                    first = Math.Clamp(start, 1, pages);
                    return (first, Math.Min(first + span - 1, pages));
                }
            }
            catch
            {
                // settings are best-effort
            }

            fallbackPage = Math.Clamp(fallbackPage, 1, pages);
            return (fallbackPage, fallbackPage);
        }

        // F - the reader's hand on the companion, pressed where the companion
        // lives: the AI summary windows (the navigator or the recap window
        // itself) forward the key here while one of them holds the focus - the
        // PDF editor never answers F. One press puts the recap window away
        // (range moves keep it put away), the next brings it back over the
        // stretch on screen. The navigator's range moves own the summons;
        // F owns the window itself, Recap mode on or off.
        internal void ToggleRecapCompanion()
        {
            if (_doc is null || string.IsNullOrEmpty(_currentFile))
            {
                return;
            }

            _aiSettingsViewModel ??= new Features.AI.AiSettingsViewModel();
            _aiSettingsViewModel.Load();
            (int first, int last) = GetRecapRange(_currentPage + 1);
            Features.Summary.RecapController.ToggleWindow(
                this,
                _currentFile,
                _doc.PageCount,
                first,
                last,
                () => Features.AI.AiSurfaceModels.Configure(_aiSettingsViewModel.ToGenConfig(), Features.AI.AiSurface.Recaller),
                Loc);
        }

        // The navigator's range MOVED: the recap is told - a showing window
        // follows the new stretch, a closed one opens over it while Recap
        // mode is on and F has not put it away (ShowRecap's own gate). This
        // and F are the companion's only summons: nothing the reader does
        // while merely reading - scrolling, zooming, turning pages,
        // switching books - ever opens one, so closing it sticks.
        private void RefreshRecapForRangeChange()
        {
            if (_doc is null || string.IsNullOrEmpty(_currentFile))
            {
                return;
            }

            _aiSettingsViewModel ??= new Features.AI.AiSettingsViewModel();
            _aiSettingsViewModel.Load();
            (int first, int last) = GetRecapRange(_currentPage + 1);
            Features.Summary.RecapController.ShowRecap(
                this,
                _currentFile,
                _doc.PageCount,
                first,
                last,
                () => Features.AI.AiSurfaceModels.Configure(_aiSettingsViewModel.ToGenConfig(), Features.AI.AiSurface.Recaller),
                Loc);
        }

        // The navigator's arrows ask for a smooth, fast glide to the new range's
        // first page: the viewer's animated navigation lands it at the very top
        // of the viewport, and the sidebar selection rides the page sync.
        private void NavigateSummaryRangeStart(int pageNumber)
        {
            if (_doc is null)
            {
                return;
            }

            // The sidebar leads: its glide parks the range's first page at the
            // TOP of the page list - and owns the list's scroll while it runs,
            // so the page-sync's ScrollIntoView cannot fight the animation.
            if (PageList is { } list && list.Items.Count > 0)
            {
                GlideSidebarToPageTop(Math.Clamp(pageNumber - 1, 0, list.Items.Count - 1));
            }

            ActiveViewer?.NavigateToPageAnimatedExt(Math.Clamp(pageNumber - 1, 0, _doc.PageCount - 1));
        }

        // ── The page list's range glide ─────────────────────────────────────
        // The reading navigator's arrows glide the DOCUMENT to the new stretch's
        // first page; this glides the SIDEBAR the same way, so the range's first
        // page ends up seated at the top of the page list. The selection's own
        // ScrollIntoView only guarantees visibility and would fight the run, so
        // it stands down for the glide's lifetime plus a short hold spanning the
        // document glide's landing (EnsureSidebarPageVisible), a final park then
        // re-seats the row exactly at the top edge, and the reader touching the
        // list takes the scroll back instantly.

        private DispatcherTimer? _sidebarGlideTimer;
        private int _sidebarGlidePage = -1;     // >= 0 while a glide owns the list's scroll

        private void GlideSidebarToPageTop(int pageIndex)
        {
            if (PageList is null || pageIndex < 0 || pageIndex >= PageList.Items.Count)
            {
                return;
            }

            StopSidebarGlide();
            // Virtualization may not have built the row yet: ScrollIntoView once
            // to seat the generator, then measure the row's real offset.
            if (PageList.ItemContainerGenerator.ContainerFromIndex(pageIndex) is not FrameworkElement)
            {
                PageList.ScrollIntoView(PageList.Items[pageIndex]);
                PageList.UpdateLayout();
            }

            if (PageList.ItemContainerGenerator.ContainerFromIndex(pageIndex) is not FrameworkElement row
                || FindSidebarDescendant<ScrollViewer>(PageList) is not { } sv)
            {
                return;
            }

            // The row's Y relative to the viewport: scrolling down by exactly
            // that parks the row's top edge on the viewport's top edge.
            double rowTop = row.TransformToVisual(sv).Transform(new Point(0, 0)).Y;
            double target = Math.Clamp(sv.VerticalOffset + rowTop, 0, Math.Max(0, sv.ScrollableHeight));
            double from = sv.VerticalOffset;
            if (Math.Abs(target - from) < 2)
            {
                return;     // already seated - nothing to glide
            }

            _sidebarGlidePage = pageIndex;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var glide = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _sidebarGlideTimer = glide;
            glide.Tick += (_, _) =>
            {
                if (_sidebarGlideTimer != glide || _doc is null
                    || PageList is null || pageIndex >= PageList.Items.Count)
                {
                    glide.Stop();
                    if (_sidebarGlideTimer == glide)
                    {
                        _sidebarGlideTimer = null;
                        _sidebarGlidePage = -1;
                    }

                    return;
                }

                // Phase 1: the 280ms ease-out glide that parks the row at the top.
                double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / 280.0);
                if (t < 1.0)
                {
                    double eased = 1.0 - (1.0 - t) * (1.0 - t);      // ease-out quad
                    sv.ScrollToVerticalOffset(from + (target - from) * eased);
                    return;
                }

                // Phase 2: hold the list's scroll ownership past the document
                // glide's landing (340ms) - its end-of-run sidebar sync must not
                // unseat the freshly parked row.
                if (clock.Elapsed.TotalMilliseconds < 430.0)
                {
                    return;
                }

                glide.Stop();
                _sidebarGlideTimer = null;
                if (_sidebarGlidePage == pageIndex)
                {
                    // Final park: the row is measured once more and seated at the
                    // viewport's top edge in one step - a thumbnail decoding above
                    // it (or any sync that slipped through) is corrected here -
                    // and only then is the list's scroll handed back.
                    _sidebarGlidePage = -1;
                    if (PageList.ItemContainerGenerator.ContainerFromIndex(pageIndex)
                            is FrameworkElement parkedRow
                        && FindSidebarDescendant<ScrollViewer>(PageList) is { } parkSv)
                    {
                        double rowTopNow = parkedRow.TransformToVisual(parkSv)
                            .Transform(new Point(0, 0)).Y;
                        parkSv.ScrollToVerticalOffset(Math.Clamp(
                            parkSv.VerticalOffset + rowTopNow,
                            0, Math.Max(0, parkSv.ScrollableHeight)));
                    }
                }
            };
            glide.Start();
        }

        private void StopSidebarGlide()
        {
            _sidebarGlideTimer?.Stop();
            _sidebarGlideTimer = null;
            _sidebarGlidePage = -1;
        }

        private void ApplyPageRangeHighlight(int first, int last)
        {
            int count = PageList.Items.Count;
            if (count == 0)
            {
                return;
            }

            int firstIdx = Math.Clamp(first - 1, 0, count - 1);
            int lastIdx = Math.Clamp(last - 1, 0, count - 1);
            _summaryHighlighting = true;
            try
            {
                // Paint the range on the items themselves, not on the list's
                // selection: selection is written by a dozen independent paths
                // (scroll sync, pane restore, paging, the reader's own click) and
                // every one of them wiped a SelectedItems-based highlight back
                // down to a single green page. A VM flag cannot be collapsed by a
                // selection write. The "you are here" marker needs no ride-along
                // either - the selection already follows the reader's page through
                // its own paths, so the whole range shows green with the current
                // page's selection state layered on top.
                for (int i = 0; i < count; i++)
                {
                    if (PageList.Items[i] is PageThumbnailVm vm)
                    {
                        vm.IsInRange = i >= firstIdx && i <= lastIdx;
                    }
                }
            }
            finally
            {
                _summaryHighlighting = false;
            }
        }

        private void ClearPageRangeHighlight()
        {
            if (PageList is null || PageList.Items.Count == 0)
            {
                return;
            }

            _summaryHighlighting = true;
            try
            {
                // Selection stays exactly where the reader is - clearing the
                // range only undoes the paint, it does not move the mark.
                for (int i = 0; i < PageList.Items.Count; i++)
                {
                    if (PageList.Items[i] is PageThumbnailVm vm)
                    {
                        vm.IsInRange = false;
                    }
                }
            }
            finally
            {
                _summaryHighlighting = false;
            }
        }

        private void OpenSummaryWindow()
        {
            if (string.IsNullOrEmpty(_currentFile) || _doc is null)
            {
                SetStatusHeld(Loc("Str_SummaryOpenBook"));
                return;
            }

            if (_summaryWindow != null)
            {
                if (_summaryWindow.DocumentPathEquals(_currentFile))
                {
                    _summaryWindow.Activate();
                    return;
                }

                _summaryWindow.Close();
                _summaryWindow = null;
            }

            _aiSettingsViewModel ??= new Features.AI.AiSettingsViewModel();
            _aiSettingsViewModel.Load();
            string path = _currentFile;
            int pageCount = _doc.PageCount;
            var summary = new Features.Summary.SummaryWindow(
                this,
                path,
                Features.AI.DocumentIndexer.ComputeDocumentId(path),
                pageCount,
                () => _currentPage,
                () => Features.AI.AiSurfaceModels.Configure(_aiSettingsViewModel!.ToGenConfig(), Features.AI.AiSurface.Summary),
                // v1.19.32: the recap's own dial answers the post-digest recap
                // too - one recap choice for the window AND the quiet pass,
                // exactly the dial the settings panel names "Recap".
                () => Features.AI.AiSurfaceModels.Configure(_aiSettingsViewModel!.ToGenConfig(), Features.AI.AiSurface.Recaller),
                Loc);
            // A window closed from its own title bar must not leave a stale reference
            // behind - the next toolbar click would poke a corpse (Activate on a
            // closed window throws).
            summary.RangeVisualChanged += RefreshSummaryPageHighlight;
            // The arrows move the reading range; the document glides along to
            // the new stretch's first page.
            summary.PageNavigationRequested += NavigateSummaryRangeStart;
            // The beaker chip in the navigator's title bar: probe the exact
            // stretch on screen with the AI test.
            summary.TestRangeRequested += OpenAiTestForRange;
            // v1.19.25: the popup's Search opens the query in the app's own
            // browser - the pane comes forward, the tab lands on top.
            summary.WebSearchRequested += OpenSummarySearchInBrowser;
            // The navigator's range MOVED (steppers, keyboard Left/Right or
            // A/D, a retyped start, a span chip): the recap answers to this
            // and to F - a showing window follows the new stretch, a closed
            // one opens over it. Reading itself never summons the companion.
            summary.RangeMoved += RefreshRecapForRangeChange;
            summary.Closed += (_, _) =>
            {
                RefreshSummaryPageHighlight();      // the page list returns to the single page
                if (ReferenceEquals(_summaryWindow, summary))
                {
                    _summaryWindow = null;
                }

                // A switch-away close leaves the book's flag standing so coming
                // back reopens the window; the reader putting it down - the
                // toolbar toggle, the title bar - clears the book's entry.
                if (!ReferenceEquals(_summarySwitchAway, summary))
                {
                    _summaryOpenByDoc.Remove(path);
                }

                _summarySwitchAway = null;
            };
            _summaryWindow = summary;
            summary.Show();
            _summaryOpenByDoc[path] = true;         // this book wears its navigator
            RefreshSummaryPageHighlight();          // open: highlight the reading range now
        }

        private void OpenAiChat()
        {
            if (AiChatOverlay is null) return;

            // Initialize viewmodel if needed. The VM exists even with no
            // document open: previously the panel was dead (send silently
            // returned) until it was closed and reopened after opening a PDF.
            if (_aiChatViewModel is null)
            {
                _aiSettingsViewModel ??= new Features.AI.AiSettingsViewModel();
                _aiSettingsViewModel.Load();
                _aiChatViewModel = new Features.AI.AiChatViewModel(
                    this,
                    () => _aiSettingsViewModel.ToGenConfig(),
                    Loc)
                {
                    // v1.19.22: the browser owns page reading; the chat VM only
                    // asks. Extraction runs in the tab's own renderer, fresh on
                    // every question - nothing of one page ever reaches another
                    // page's conversation.
                    WebPageReader = (tabId, ct) => WebPane.ExtractPageTextAsync(tabId, ct),
                    EditorTextReader = async (tabId, ct) => await TextPane.GetDocumentPlainTextAsync(),
                };
                AiChatOverlay.DataContext = _aiChatViewModel;
            }

            // The editor or browser leads when on screen - the chat binds
            // to the active session. Only with both away does it bind to the book.
            if (EditorPaneHost.Visibility == Visibility.Visible)
            {
                _aiChatViewModel.HandleEditorContextChanged(TextPane.ActiveTabId, TextPane.ActiveTabTitle);
            }
            else if (WebPaneHost.Visibility == Visibility.Visible && WebPane.ActiveTabId is { } webTabId)
            {
                _aiChatViewModel.HandleWebContextChanged(webTabId, WebPane.ActiveTabTitle, WebPane.ActiveTabUrl);
            }
            else if (_currentFile is not null)
            {
                _ = _aiChatViewModel.InitializeForDocumentAsync(_currentFile); // fire-and-forget, UI stays responsive
            }

            AiChatOverlay.Visibility = Visibility.Visible;
            AiChatInput?.Focus();
        }

        // The Discord controller's escape hatch from repaired temp copies:
        // given a temp-like path, hand back the real book the window knows -
        // _originalFile first, the title bar's own display name second. A
        // null answer means "nothing better known" and the controller falls
        // back to a generic title instead of the temp copy's GUID.
        private string? ResolveRealDocumentPath(string candidate)
        {
            string? original = _originalFile;
            if (!string.IsNullOrWhiteSpace(original))
            {
                return original;
            }

            string? labeled = FileNameLabel?.Text;
            return string.IsNullOrWhiteSpace(labeled) ? null : labeled;
        }

        /// <summary>
        /// The active document changed (tab switch, new open, close):
        /// drop the citation highlight, which pointed at the previous
        /// document's page, and re-bind the chat to the new file.
        /// </summary>
        private void ActiveDocumentChanged(string? filePath)
        {
            ClearAiSourceHighlight();
            // The recap bridge belongs to one book: its cache and its open
            // window die with the old document - a switch is window
            // management, not reading, and it never summons the companion.
            Features.Summary.RecapController.NotifyDocumentChanged();
            // The Discord presence follows the book on screen: a live document
            // broadcasts (or re-arms) its reading state, a closed one clears
            // the profile at once. A different book restarts the elapsed
            // timer; page reports never do.
            if (string.IsNullOrEmpty(_currentFile) || _doc is null)
            {
                Features.Discord.DiscordRpcController.OnDocumentClosed();
            }
            else
            {
                // The working file lies after a repair: _currentFile points
                // at %LOCALAPPDATA%\Avalanche\Temp\killerpdf_repaired_
                // {guid}.pdf while the reader's real book stays in
                // _originalFile (it is what FileNameLabel shows). Discord
                // must hear the book, never the temp copy - the original
                // path wins, the reported path is the fallback, and only
                // then the working file itself.
                string? realPath = _originalFile ?? filePath ?? _currentFile;
                Features.Discord.DiscordRpcController.OnDocumentOpened(
                    realPath!, _currentPage + 1, _doc.PageCount);
            }
            // The AI test window probes ONE document; switching documents would
            // leave it auditing a stale file, so it goes with the old one.
            if (_aiTestWindow is { } test &&
                (filePath is null || !test.DocumentPathEquals(filePath)))
            {
                test.Close();
            }

            // The summary navigator reads ONE book too: a tab switch parks it,
            // and its Closed hook saves the digest under the old book's id plus
            // hands the page list back to the single-page highlight. Reopening
            // on a book restores that book's own digest - the new book never
            // sees the old one's paragraph, and the old book keeps everything
            // the reader generated on it. Same-path reactivations keep the
            // window up.
            if (_summaryWindow is { } navigator &&
                (filePath is null || !navigator.DocumentPathEquals(filePath)))
            {
                _summarySwitchAway = navigator;     // a switch, never the reader's hand
                navigator.Close();
                _summarySwitchAway = null;
            }

            // The notes cards audited the previous document; the new one
            // reseeds the From/To pair and empties the panel.
            ResetNotesForDocument(filePath);

            // v1.19.22: while the browser leads, a book switch must not re-bind
            // the chat - the page's conversation stays on screen. The book gets
            // the chat back when the pane hides (HideWebPane re-binds then).
            if (WebPaneHost.Visibility != Visibility.Visible)
            {
                _aiChatViewModel?.HandleDocumentSwitch(
                    filePath,
                    panelVisible: AiChatOverlay?.Visibility == Visibility.Visible);
            }

            // The navigator follows the book: a PDF whose window was open when
            // the reader left gets it straight back on return, into the digest
            // it saved. A fresh PDF - a new tab, a first open - stays closed,
            // and a relaunch starts every book closed.
            if (filePath is not null && _summaryWindow is null &&
                !string.IsNullOrEmpty(_currentFile) && _doc is not null &&
                _summaryOpenByDoc.TryGetValue(_currentFile, out bool wanted) && wanted)
            {
                OpenSummaryWindow();
            }

            // v1.19.5: the toolbar's save button follows the book on screen -
            // it exists only while the active document is a web-downloaded PDF.
            RefreshWebSaveButton(filePath);
        }

        /// <summary>A closed tab takes its navigator wish with it: the book's
        /// per-document summary visibility dies with the tab, so reopening the
        /// file starts with the window closed (null: every tab at once - the
        /// whole map clears). Mirrors the notes history's rule: nothing the
        /// reader parked in a tab outlives the tab it was parked in.</summary>
        private void DiscardSummaryVisibility(string? filePath)
        {
            if (filePath is null)
            {
                _summaryOpenByDoc.Clear();
            }
            else
            {
                _summaryOpenByDoc.Remove(filePath);
            }
        }

        private void CloseAiChat()
        {
            if (AiChatOverlay is null) return;

            AiChatOverlay.Visibility = Visibility.Collapsed;

            // Clear the AI source highlight
            ClearAiSourceHighlight();
        }

        private void AiChatCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            _aiChatViewModel?.CancelReply();
        }

        // ---- the prompt workshop (v1.19.88) --------------------------------
        // The summary prompts stop being hardcoded: the reader edits the
        // built-in voices, writes new ones, aims each at the browser digest
        // or the book window, saves and deletes. The store carries it all;
        // this section is just the chair the workshop sits in.

        private bool _promptWiring;   // the rebuild must not answer its own SelectionChanged
        private Features.Summary.AiPromptDef? _promptEditing;   // null = a new row waiting for Save

        // v1.19.89: ten categories, the dropdown the reader switches before
        // the prompt dropdown - each category holds its own rows, in store
        // order, and the editors below work on whatever it selects.
        private static readonly string[] PromptCatTags =
            { "pdf", "sidechat", "websidechat", "recap", "notes", "tester", "grammar", "rewrite", "editorsidechat", "web" };

        private string SelectedPromptCat()
        {
            int i = AiPromptCatBox.SelectedIndex;
            return i >= 0 && i < PromptCatTags.Length ? PromptCatTags[i] : PromptCatTags[0];
        }

        private void RefreshPromptWorkshop()
        {
            if (AiPromptPick is null) return;
            var rows = Features.Summary.PromptStore.For(SelectedPromptCat());
            string keep = _promptEditing?.Id
                ?? (AiPromptPick.SelectedItem is Features.Summary.AiPromptDef sel ? sel.Id : "");
            Features.Summary.AiPromptDef? pick = null;
            foreach (var p in rows)
            {
                if (!string.IsNullOrEmpty(keep) && p.Id == keep) { pick = p; break; }
            }

            _promptWiring = true;
            AiPromptPick.ItemsSource = rows;
            AiPromptPick.DisplayMemberPath = "DisplayName";
            AiPromptPick.SelectedItem = pick ?? (rows.Count > 0 ? rows[0] : null);
            _promptEditing = AiPromptPick.SelectedItem as Features.Summary.AiPromptDef;
            FillPromptFields();
            _promptWiring = false;
        }

        private void FillPromptFields()
        {
            var p = _promptEditing;
            if (p is null)
            {
                AiPromptTitleBox.Text = "";
                AiPromptBodyBox.Text = "";
                return;
            }

            int catIdx = Array.IndexOf(PromptCatTags, p.Category);
            AiPromptCatBox.SelectedIndex = catIdx < 0 ? 0 : catIdx;   // the caller holds the wiring guard
            AiPromptTitleBox.Text = p.DisplayName;
            AiPromptBodyBox.Text = PromptBodyForEditor(p);
        }

        // An untouched built-in shows the hardcoded voice's own text - the
        // library holds every wording the features speak, placeholders and
        // all - editing and saving turns it into the row's body from then on.
        private static string PromptBodyForEditor(Features.Summary.AiPromptDef p)
            => Features.AI.AiPromptLibrary.EditorBodyFor(p);

        private void AiPromptPick_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_promptWiring) return;
            _promptEditing = AiPromptPick.SelectedItem as Features.Summary.AiPromptDef;
            FillPromptFields();
        }

        private void AiPromptCatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_promptWiring) return;
            _promptEditing = null;
            RefreshPromptWorkshop();
        }

        private void AiPromptNewBtn_Click(object sender, RoutedEventArgs e)
        {
            _promptEditing = null;
            _promptWiring = true;
            AiPromptPick.SelectedItem = null;
            _promptWiring = false;
            AiPromptTitleBox.Text = "";
            AiPromptBodyBox.Text = "";
            AiPromptTitleBox.Focus();
        }

        private void AiPromptSaveBtn_Click(object sender, RoutedEventArgs e)
        {
            string body = AiPromptBodyBox.Text ?? "";
            string title = (AiPromptTitleBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(title)) return;

            var def = _promptEditing ?? new Features.Summary.AiPromptDef
            {
                Id = Features.Summary.PromptStore.NewId()
            };
            if (title.Length > 0) { def.Title = title; def.TitleKey = ""; }
            def.Category = SelectedPromptCat();
            def.Body = body;
            Features.Summary.PromptStore.Upsert(def);
            _promptEditing = def;
            RefreshPromptWorkshop();
        }

        private void AiPromptDeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_promptEditing is null) return;
            string id = _promptEditing.Id;
            _promptEditing = null;
            Features.Summary.PromptStore.Delete(id);
            RefreshPromptWorkshop();
        }

        // ---- AI settings overlay (F1) -------------------------------------

        private void AiChatSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (AiSettingsOverlay is null) return;
            if (AiSettingsOverlay.Visibility == Visibility.Visible)
            {
                CloseAiSettings();
                return;
            }

            if (_aiSettingsViewModel is not null)
            {
                _aiSettingsViewModel.Load();
                AiSettingsOverlay.DataContext = _aiSettingsViewModel;
                AiSettingsOverlay.Visibility = Visibility.Visible;
                RefreshPromptWorkshop();   // v1.19.88: the workshop shows the store as it is right now
            }
        }

        private void AiSettingsCloseBtn_Click(object sender, RoutedEventArgs e) => CloseAiSettings();

        private void CloseAiSettings()
        {
            if (AiSettingsOverlay is null) return;
            AiSettingsOverlay.Visibility = Visibility.Collapsed;
            _aiSettingsViewModel?.Save(); // persist on close
        }

        private void AiTestConnectionBtn_Click(object sender, RoutedEventArgs e)
        {
            _ = _aiSettingsViewModel?.TestConnectionAsync(Loc);
        }

        private void AiOllamaPresetBtn_Click(object sender, RoutedEventArgs e)
        {
            _aiSettingsViewModel?.ApplyOllamaPreset();
        }

        private async void AiChatSendBtn_Click(object sender, RoutedEventArgs e)
        {
            // Guard with CanSend (not just non-empty input): Enter reaches this
            // handler even while the send button is disabled, and previously the
            // captured text was dropped when a reply was still being generated.
            if (_aiChatViewModel is null || !_aiChatViewModel.CanSend)
                return;

            var input = _aiChatViewModel.CurrentInput;
            _aiChatViewModel.CurrentInput = "";
            await _aiChatViewModel.SendMessageAsync(input);

            // Scroll to bottom - fire-and-forget UI update
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                AiChatScrollViewer?.ScrollToBottom();
            });
        }

        // PreviewKeyDown (not KeyDown) is required here: with AcceptsReturn on, the
        // TextBox class handler consumes plain Enter while the event bubbles, so a
        // KeyDown handler never sees it and cannot turn it into a send.
        private void AiChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Ctrl+Enter inserts a newline at the caret (replacing any selection).
                    e.Handled = true;
                    InsertAiChatNewLine();
                }
                else if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    // Plain Enter sends the message instead of starting a new line.
                    e.Handled = true;
                    AiChatSendBtn_Click(sender, e);
                }
                // Other modifier combinations (Shift/Alt) keep the default editing behaviour.
            }
            else if (e.Key == Key.Escape)
            {
                CloseAiChat();
            }
        }

        private void InsertAiChatNewLine()
        {
            if (AiChatInput is null) return;

            // Replaces the current selection (or inserts at the caret when nothing is
            // selected) while preserving the TextBox undo stack, then parks the caret
            // right after the inserted line break.
            var start = Math.Max(AiChatInput.SelectionStart, 0);
            AiChatInput.SelectedText = "\r\n";
            AiChatInput.CaretIndex = start + 2;
        }

        /// <summary>
        /// Navigates to a citation's page and highlights the quoted passage.
        /// The page comes from the citation's LOCATED quote (a quote on a
        /// chunk's second page navigates to the second page), the viewer is
        /// captured at click time (refocusing the other pane mid-wait cannot
        /// redirect the highlight) and a navigation generation token ensures
        /// only the LATEST click may draw (E3/E7/E8).
        /// </summary>
        internal void NavigateToAiSource(Features.AI.DocumentChunk chunk, Features.AI.AiSource source)
        {
            if (_doc is null || chunk == null)
            {
                Avalanche.Services.AiHighlightLog.Log(
                    $"navigate BAIL: no document={_doc is null} or null chunk");
                return;
            }

            int pageIndex = source.PageIndex >= 0 ? source.PageIndex : chunk.PageIndex;
            if (pageIndex < 0 || pageIndex >= _doc.PageCount)
            {
                Avalanche.Services.AiHighlightLog.Log(
                    $"navigate BAIL: pageIndex {pageIndex} out of range (pages {_doc.PageCount}) " +
                    $"chunk#{chunk.ChunkIndex} PageIndices=[{string.Join(',', chunk.PageIndices)}]");
                return;
            }

            // A new click invalidates any still-running wait/draw of the
            // previous one; only the newest may paint.
            int navGeneration = ++_aiNavGeneration;
            var viewer = ActiveViewer;

            Avalanche.Services.AiHighlightLog.Log(
                $"navigate: chunk#{chunk.ChunkIndex} page={pageIndex} gen={navGeneration} " +
                $"source={source.SourceId} location={source.Location} " +
                $"quoteLen={source.Quote?.Length ?? 0}");

            // Clear any existing AI highlight
            ClearAiSourceHighlight();

            // Navigate to the page
            PageList.SelectedIndex = pageIndex;

            // Wait for the page to render, then highlight using exact coordinates
            // Use a retry loop for continuous view and far-away pages
            _ = WaitForCanvasAndHighlightAsync(viewer, chunk, source, pageIndex, navGeneration);
        }

        /// <summary>
        /// A web citation click (v1.19.25): the quoted passage is searched on
        /// the live page of the tab the answer came from - found means
        /// selected plus custom-highlighted and scrolled into view; anything
        /// else surfaces the same "could not locate" status a PDF citation
        /// miss shows. The browser tab answers its own citations.
        /// </summary>
        internal async void NavigateToWebPageCitation(string tabId, string quote)
        {
            if (string.IsNullOrWhiteSpace(quote)) return;
            try
            {
                var result = await WebPane.HighlightTextInTabAsync(tabId, quote);
                if (result is { Ok: true })
                {
                    Avalanche.Services.AiHighlightLog.Log(
                        $"web navigate: tab {tabId} ok painted={result.Painted}");
                    return;
                }

                Avalanche.Services.AiHighlightLog.Log(
                    $"web navigate BAIL: tab {tabId} reason={result?.Reason ?? "no engine"}");
                SetStatus(Loc("Str_AiChatWebNavigateFailed"));
            }
            catch (Exception ex)
            {
                // A failed jump must never take the chat down with it.
                Avalanche.Services.AiHighlightLog.Log(
                    $"web navigate EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private int _aiNavGeneration;

        private async Task WaitForCanvasAndHighlightAsync(Controls.PdfViewer viewer,
            Features.AI.DocumentChunk chunk, Features.AI.AiSource source, int pageIndex, int navGeneration)
        {
            const int maxAttempts = 60; // ~6 seconds with 100ms intervals; big documents
                                        // can take a while to mount and render a far page
            const int delayMs = 100;

            if (viewer is null)
            {
                Avalanche.Services.AiHighlightLog.Log("wait: no active viewer");
                return;
            }

            for (int attempt = 0; attempt <= maxAttempts; attempt++)
            {
                // Another click, a document switch or a closed panel invalidates
                // this wait (E7).
                if (navGeneration != _aiNavGeneration || _doc is null)
                    return;

                var canvas = viewer.GetCanvasForPage(pageIndex);
                if (canvas is null || viewer.GetRenderDimensions(pageIndex) is null)
                {
                    if (attempt == maxAttempts)
                    {
                        Avalanche.Services.AiHighlightLog.Log(
                            $"wait GAVE UP after {maxAttempts} attempts: page {pageIndex} never got canvas + render dims");
                        SetStatus(Loc("Str_AiChatSourceNavigateFailed"));
                        return;
                    }
                    await Task.Delay(delayMs);
                    continue;
                }

                if (attempt > 0)
                    Avalanche.Services.AiHighlightLog.Log($"wait attempt {attempt}: ready, highlighting page {pageIndex}");
                HighlightAiSourceOnPage(viewer, chunk, source, pageIndex);
                return;
            }
        }

        // AI citation highlights carry their own translucent light blue so they can
        // never be mistaken for the user's own Highlight tool marks (yellow by default).
        private static readonly System.Windows.Media.Color AiHighlightColor =
            System.Windows.Media.Color.FromArgb(88, 100, 180, 255);

        // The AI citation highlight currently painted (page + chunk + source), plus the
        // PDF-space line rects it resolved to. The annotation canvas is wiped on every
        // re-render (zoom, scroll, annotation edits); RenderAllAnnotations calls back
        // through ReapplyAiSourceHighlight so the highlight survives exactly like the
        // search highlights and text selection quads do. The rect cache keeps those
        // repaints free of fresh file I/O.
        private (int PageIndex, Features.AI.DocumentChunk Chunk, Features.AI.AiSource? Source)? _aiHighlight;
        private List<(double Left, double Bottom, double Right, double Top)>? _aiHighlightRects;

        private void HighlightAiSourceOnPage(Controls.PdfViewer viewer,
            Features.AI.DocumentChunk chunk, Features.AI.AiSource source, int pageIndex)
        {
            try
            {
                HighlightAiSourceOnPageCore(viewer, chunk, source, pageIndex);
            }
            catch (Exception ex)
            {
                // A highlight must never take the citation click down with it.
                Avalanche.Services.AiHighlightLog.Log(
                    $"highlight EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void HighlightAiSourceOnPageCore(Controls.PdfViewer viewer,
            Features.AI.DocumentChunk chunk, Features.AI.AiSource source, int pageIndex)
        {
            if (_doc is null)
            {
                Avalanche.Services.AiHighlightLog.Log("highlight BAIL: no document");
                return;
            }

            if (viewer is null)
            {
                Avalanche.Services.AiHighlightLog.Log("highlight BAIL: no viewer");
                return;
            }

            var canvas = viewer.GetCanvasForPage(pageIndex);
            if (canvas is null)
            {
                Avalanche.Services.AiHighlightLog.Log($"highlight BAIL: no canvas for page {pageIndex}");
                return;
            }

            _aiHighlight = (pageIndex, chunk, source);
            _aiHighlightRects = null;

            // Remove any previous citation rectangles BEFORE drawing, so two
            // citations can never show simultaneously (E7).
            RemoveAiHighlightRects(canvas);

            // The model's QUOTE is located inside the cited chunk's page slice
            // and only that range is painted (E1). An unlocated quote paints
            // NOTHING - the previous whole-chunk/bbox fallbacks painted entire
            // passages the citation never named.
            if (DrawAiHighlightForPage(viewer, pageIndex, canvas, chunk, source))
            {
                if (source.Location == Features.AI.AiQuoteLocation.Approximate)
                    SetStatus(Loc("Str_AiChatSourceApproximate"));
                return;
            }

            Avalanche.Services.AiHighlightLog.Log(
                $"highlight FAILED: page {pageIndex} - quote not located within the cited chunk (location={source.Location})");
            SetStatus(Loc("Str_AiChatSourceNavigateFailed"));
        }

        /// <summary>
        /// Resolves and paints the quoted passage for one page: the QUOTE is
        /// located strictly within the chunk's page slice (SearchService
        /// .LocateQuoteWithinSlice - one cached PdfPig open), so a phrase that
        /// also occurs elsewhere on the page can never be highlighted by
        /// mistake (E5).
        /// </summary>
        private bool DrawAiHighlightForPage(Controls.PdfViewer viewer, int pageIndex, Canvas canvas,
            Features.AI.DocumentChunk chunk, Features.AI.AiSource? source)
        {
            // Already resolved earlier (re-render repaint): reuse the located rects.
            if (_aiHighlightRects is { Count: > 0 })
                return TryDrawAiHighlightRects(canvas, _aiHighlightRects, viewer, pageIndex, chunk);

            if (string.IsNullOrEmpty(_currentFile))
            {
                Avalanche.Services.AiHighlightLog.Log("locate BAIL: _currentFile empty");
                return false;
            }

            // Unlocated citations never paint - there is nothing trustworthy
            // to highlight (E2/E4).
            if (source is null || source.Location == Features.AI.AiQuoteLocation.Unlocated ||
                string.IsNullOrWhiteSpace(source.Quote))
            {
                Avalanche.Services.AiHighlightLog.Log(
                    $"locate BAIL: location={source?.Location} quoteLen={source?.Quote?.Length ?? 0}");
                return false;
            }

            string? slice = ChunkPageSliceForPage(chunk, pageIndex);
            string quote = source.Quote;

            Avalanche.Services.AiHighlightLog.Log(
                $"locate: page={pageIndex} slice={(slice is null ? "null" : $"{slice.Length}ch")} " +
                $"quote={quote.Length}ch '{TruncLog(quote)}'");

            var match = Services.SearchService.LocateQuoteWithinSlice(_currentFile!, pageIndex, slice, quote);
            if (match is null || match.LineRects.Count == 0)
            {
                Avalanche.Services.AiHighlightLog.Log("locate: QUOTE NOT FOUND within the chunk slice");
                return false;
            }

            Avalanche.Services.AiHighlightLog.Log($"locate: MATCH, {match.LineRects.Count} line rect(s)");
            _aiHighlightRects = match.LineRects;
            return TryDrawAiHighlightRects(canvas, match.LineRects, viewer, pageIndex, chunk);
        }

        /// <summary>
        /// The chunk's text restricted to one page. CreateChunk stores an inclusive
        /// chunk-relative [startWord, endWord] per page next to the word-joined chunk text,
        /// so splitting the text on whitespace recovers the exact words this chunk holds
        /// on the given page. A whole-chunk needle could never match a single page when a
        /// chunk spans pages - the slice is what the citation actually refers to here.
        /// Null for legacy indexes that predate WordRanges.
        /// </summary>
        private static string? ChunkPageSliceForPage(Features.AI.DocumentChunk chunk, int pageIndex)
        {
            int idx = chunk.PageIndices.IndexOf(pageIndex);
            if (idx < 0 || idx >= chunk.WordRanges.Count) return null;
            var range = chunk.WordRanges[idx];
            if (range is null || range.Length < 2 || range[0] < 0) return null;

            var words = chunk.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return null;
            int start = range[0];
            int end = Math.Min(range[1], words.Length - 1);
            if (end < start || start >= words.Length) return null;
            return string.Join(' ', words[start..(end + 1)]);
        }

        /// <summary>
        /// Fills one translucent rect per PDF-space line rect with the AI citation's
        /// own light blue. The conversion is the proven search-highlight one (Shell/Search
        /// AddSearchHighlight): scale by the engine's effective page size against the
        /// canvas render dims, flip Y for PDF's bottom-left origin, and pad 12% of the
        /// band height so the highlight wraps the glyphs without spilling into the next
        /// line (the selection quads use the same padding).
        /// </summary>
        private bool TryDrawAiHighlightRects(Canvas canvas,
            List<(double Left, double Bottom, double Right, double Top)> pdfRects,
            Controls.PdfViewer viewer, int pageIndex, Features.AI.DocumentChunk chunk)
        {
            var rd = viewer.GetRenderDimensions(pageIndex);
            if (rd is null) return false;

            double pdfW = 0, pdfH = 0;
            try
            {
                var pageInfo = EnsureEngineDocumentSession().Pages[pageIndex];
                pdfW = pageInfo.Width;
                pdfH = pageInfo.Height;
            }
            catch { /* engine session not ready yet - fall back to the chunk's cache */ }
            if (pdfW <= 0 || pdfH <= 0)
            {
                // Per-page geometry: a chunk's pages can differ in size and
                // rotation; the legacy single-page fields only described the
                // chunk's FIRST page and mis-scaled every other one.
                int geoIdx = chunk.PageIndices.IndexOf(pageIndex);
                if (geoIdx >= 0 && geoIdx < chunk.PageSizes.Count
                    && chunk.PageSizes[geoIdx] is { } size && size.Length == 2)
                {
                    pdfW = size[0];
                    pdfH = size[1];
                }
                else
                {
                    pdfW = chunk.PageWidth;
                    pdfH = chunk.PageHeight;
                }
            }
            if (pdfW <= 0 || pdfH <= 0) return false;

            var (renderW, renderH) = rd.Value;
            double sx = renderW / pdfW;
            double sy = renderH / pdfH;

            var fill = new SolidColorBrush(AiHighlightColor);
            fill.Freeze();

            // One band per located line: the highlight hugs the words it names,
            // exactly like the user's own Highlight tool - never a box floating
            // over the whole block.
            int added = 0;
            foreach (var (left, bottom, right, top) in pdfRects)
            {
                double cw = (right - left) * sx;
                double ch = (top - bottom) * sy;
                if (cw <= 0 || ch <= 0) continue;
                double pad = ch * 0.12;
                var rect = new Rectangle
                {
                    Fill = fill,
                    Width = cw + pad * 2,
                    Height = ch + pad * 2,
                    IsHitTestVisible = false,
                    Tag = "AiSourceHighlight"
                };
                Canvas.SetLeft(rect, left * sx - pad);
                Canvas.SetTop(rect, renderH - (top * sy) - pad);
                canvas.Children.Add(rect);
                added++;
            }

            Avalanche.Services.AiHighlightLog.Log(
                $"draw: page={pageIndex} pdfDims={pdfW:0.#}x{pdfH:0.#} render={renderW:0.#}x{renderH:0.#} " +
                $"scale={sx:0.###}/{sy:0.###} rectsIn={pdfRects.Count} rectsDrawn={added}");
            // Zero drawn bands means nothing is visible: report failure so the
            // caller can fall back to the chunk bbox instead of faking success.
            return added > 0;
        }

        /// <summary>
        /// Called by the viewer at the tail of every annotation re-render: repaints this
        /// citation's line highlights onto the freshly cleared canvas, or nothing when the
        /// re-rendered page is not the highlighted one.
        /// </summary>
        private void ReapplyAiSourceHighlight(int page, Canvas canvas)
        {
            if (_aiHighlight is not { } hl || hl.PageIndex != page) return;
            // The canvas belongs to the re-rendering viewer (which pane it is
            // need not match ActiveViewer in split view) - resolve the owner
            // instead of assuming (E8).
            var viewer = FindCanvasOwnerViewer(canvas) ?? ActiveViewer;
            if (viewer is null) return;
            DrawAiHighlightForPage(viewer, page, canvas, hl.Chunk, hl.Source);
        }

        /// <summary>Walks up from a canvas to the PdfViewer pane that hosts it.</summary>
        private static Controls.PdfViewer? FindCanvasOwnerViewer(Canvas canvas)
        {
            System.Windows.DependencyObject? current = canvas;
            while (current is not null)
            {
                if (current is Controls.PdfViewer pv) return pv;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>First 60 characters of a needle for the diagnostics log.</summary>
        private static string TruncLog(string s)
            => s.Length <= 60 ? s : s[..60] + "...";

        /// <summary>Removes every citation rectangle from one canvas.</summary>
        private static void RemoveAiHighlightRects(Canvas canvas)
        {
            var toRemove = canvas.Children.OfType<System.Windows.Shapes.Rectangle>()
                .Where(r => r.Tag is string s && s == "AiSourceHighlight").ToList();
            foreach (var r in toRemove)
                canvas.Children.Remove(r);
        }

        private void ClearAiSourceHighlight()
        {
            _aiHighlight = null;
            _aiHighlightRects = null;
            // BOTH panes: the highlight may have been drawn on the pane that
            // was active at click time; a pane switch must not strand it (E8).
            foreach (var viewer in AllViewerPanes())
            {
                foreach (var canvas in viewer.GetAllCanvases() ?? Enumerable.Empty<Canvas>())
                    RemoveAiHighlightRects(canvas);
            }
        }

        /// <summary>Every document pane in the window (A and B when split).</summary>
        private IEnumerable<Controls.PdfViewer> AllViewerPanes()
        {
            if (Viewer is not null) yield return Viewer;
            if (ViewerB is not null) yield return ViewerB;
        }

        // ============================================================
        // Search (Ctrl+F)
        // ============================================================

        /// <summary>
        /// Converts a collection of PdfPig words to a properly ordered string.
        /// Sorts top-to-bottom then left-to-right, groups into lines using a
        /// dynamic threshold (~40% of average word height) so words at slightly
        /// different baselines still land on the correct line.
        /// </summary>
        private static string WordsToText(IEnumerable<UglyToad.PdfPig.Content.Word> source)
        {
            var words = source
                .OrderByDescending(w => w.BoundingBox.Top)
                .ThenBy(w => w.BoundingBox.Left)
                .ToList();
            if (words.Count == 0) return string.Empty;

            // Dynamic threshold: 40% of average word height, minimum 4 PDF units
            double avgH   = words.Average(w => w.BoundingBox.Height);
            double thresh = Math.Max(4.0, avgH * 0.4);

            var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
            double lineY = double.MaxValue;
            foreach (var w in words)
            {
                if (Math.Abs(w.BoundingBox.Top - lineY) > thresh)
                {
                    lines.Add([]);
                    lineY = w.BoundingBox.Top;
                }
                lines[^1].Add(w);
            }

            // Re-sort each line by X in case the top-Y sort caused any grouping
            // to pull words into the wrong order within a line.
            return string.Join("\n", lines.Select(l =>
                string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
        }

        // ── The web browser pane (v1.19.1) ──────────────────────────────────────────────
        // The toolbar globe swaps the document area for the lightweight WebView2 card,
        // hosted in exactly the PDF card's footprint under its own tab band: since
        // v1.19.7 the band carries every open view the way the reader's strip does -
        // a tab per view, its own close chip, the + at the row's end - and closing the
        // last one puts the browser away so the PDF tabs return. Any PDF the web
        // offers - a clicked .pdf link or a finished download - comes back through
        // PdfRequested and opens as an ordinary reader tab, with the pane stepping
        // aside first. The engine is lazy and suspended whenever the pane hides.
        private void WireWebPane()
        {
            WebPane.PdfRequested += path =>
            {
                _summaryHiddenForSearchPath = null;   // a web PDF switches books: the old navigator stays put
                HideWebPane();
                OpenInNewTab(path);
                // v1.19.5: a fresh web download IS the save button's client -
                // belt and braces beside the ActiveDocumentChanged that fires
                // on the tab switch above.
                RefreshWebSaveButton(path);
            };
            // v1.19.7: the strip's tabs carry their own titles now (the control
            // renames the view's card the moment a title arrives), and closing the
            // LAST tab asks the window to put the browser away - what closing the
            // old single tab always did.
            WebPane.CloseRequested += () => HideWebPane();
            // v1.19.22: the sidechat follows the browser. A different tab took
            // the screen: the chat (when open) re-binds to that tab's own
            // session. A tab died: its session dies with it - never leaked
            // into whatever tab or book the window lands on next.
            WebPane.ActiveTabChanged += tabId =>
            {
                if (WebPaneHost.Visibility == Visibility.Visible
                    && AiChatOverlay?.Visibility == Visibility.Visible)
                    _aiChatViewModel?.HandleWebContextChanged(
                        tabId, WebPane.ActiveTabTitle, WebPane.ActiveTabUrl);
                // v1.19.23: a first view landing (or a switch) retargets the
                // PDF button between its chat and save faces.
                RefreshWebSaveButton(_originalFile ?? _currentFile);
            };
            // v1.19.24: the context line follows the page while it loads.
            // The tab's title used to reach the chat only at the switch and
            // stay the "New tab" seed for the page's whole life; every rename
            // now re-announces the same tab, and the view model's same-tab
            // path touches only the header - never the transcript, never
            // another tab's session.
            WebPane.TitleChanged += title =>
            {
                if (WebPaneHost.Visibility == Visibility.Visible
                    && AiChatOverlay?.Visibility == Visibility.Visible
                    && WebPane.ActiveTabId is { } tabId)
                    _aiChatViewModel?.HandleWebContextChanged(
                        tabId, title, WebPane.ActiveTabUrl);
            };
            WebPane.TabClosed += tabId => _aiChatViewModel?.DiscardWebSession(tabId);
        }

        private void WebBrowserBtn_Click(object sender, RoutedEventArgs e) => ToggleWebPane();

        /// <summary>The browser's PDF Editor switch (v1.19.68): both document
        /// faces answer from the browser's toolbar - this one steps the browser
        /// aside and hands the floor back to the reader's PDF.</summary>
        private void PdfEditorBtn_Click(object sender, RoutedEventArgs e)
        {
            if (EditorPaneHost.Visibility == Visibility.Visible) HideEditorPane();
            if (WebPaneHost.Visibility == Visibility.Visible) HideWebPane();
        }

        /// <summary>A strip tab was clicked: the browser switches to that view,
        /// exactly as a gallery card click does. v1.19.13: the click speaks in
        /// cards - a tab's address follows its page now, so the card itself is
        /// the identity.</summary>
        private void WebTabStripTab_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.Border { DataContext: Controls.WebTabCardVm card })
                WebPane.ActivateTab(card);
        }

        /// <summary>A strip tab's ✕ closed that tab: the card leaves the gallery
        /// and the browser moves on - or the window steps aside when the last
        /// view closed.</summary>
        private void WebTabStripClose_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Controls.WebTabCardVm card })
                WebPane.CloseTab(card);
        }

        /// <summary>The band's + asks the browser for a fresh view: home page, seeded
        /// gallery card, and the caret waiting in the omnibox.</summary>
        private void WebNewTabBtn_Click(object sender, RoutedEventArgs e) => WebPane.OpenNewTab();

        /// <summary>The navigator's word-search hands off to the app's own
        /// browser (v1.19.25): the pane comes forward and the query opens as
        /// a tab of its own. The system browser is out of the loop - the
        /// reader never leaves the app to chase a word.</summary>
        private void OpenSummarySearchInBrowser(string url)
        {
            ShowWebPane();
            // v1.19.26: every search rides the one search tab - africa then
            // europe is one tab overwritten, never a strip of one-off tabs.
            WebPane.OpenSearchTab(url);
            // The navigator steps out for the search's duration - strictly
            // the search's doing, no other path here - and HideWebPane
            // brings it back when the browser steps away.
            if (_summaryWindow is { } navigator && _currentFile is not null
                && navigator.DocumentPathEquals(_currentFile))
            {
                _summaryHiddenForSearchPath = _currentFile;
                navigator.Close();   // the Closed hook clears the field and the per-doc wish
            }
        }

        private void ToggleWebPane()
        {
            if (WebPaneHost.Visibility == Visibility.Visible) HideWebPane();
            else ShowWebPane();
        }

        private void ShowWebPane()
        {
            // v1.19.64: one interface leads at a time - the editor steps aside
            // before the browser takes the floor.
            if (EditorPaneHost.Visibility == Visibility.Visible) HideEditorPane();
            // v1.19.32: the browser takes the floor - the navigator and the
            // recap companion step out with it. The navigator's close wears
            // the switch-away face (the book keeps its wish, the reader's
            // hand never touched it), and a showing recap parks one flag that
            // HideWebPane spends to bring it back.
            if (_summaryWindow is { } navigator && _currentFile is not null
                && navigator.DocumentPathEquals(_currentFile))
            {
                // v1.19.34: the digest's scroll is read before the close takes
                // it away - the welcome-back replays it.
                _summaryParkedScrollOffset = navigator.DigestScrollOffset;
                _summarySwitchAway = navigator;     // a switch, never the reader's hand
                navigator.Close();
                _summarySwitchAway = null;
            }
            if (Features.Summary.RecapController.HasOpenWindow)
            {
                _recapHiddenForBrowser = true;
                Features.Summary.RecapController.Dismiss();
            }
            WebPaneHost.Visibility = Visibility.Visible;
            WebSumBtn.Visibility = Visibility.Visible;   // the browser summary chip rides the pane (v1.19.89)
            WebPane.OnPaneShown();
            EnterWebSidebarMode();
            ApplyBrowserToolbarFace(leads: true);
            App.SetSetting("LastLeadPane", "browser");   // the browser led last (v1.19.86)
            // v1.19.22: the browser leads now - an open chat re-binds to the
            // active tab's own session. (A first pane with no tab yet binds
            // when the first view lands, via ActiveTabChanged.)
            if (AiChatOverlay?.Visibility == Visibility.Visible
                && WebPane.ActiveTabId is { } webTab)
                _aiChatViewModel?.HandleWebContextChanged(
                    webTab, WebPane.ActiveTabTitle, WebPane.ActiveTabUrl);
            // v1.19.23: the PDF button becomes the page chat while the pane leads.
            RefreshWebSaveButton(_originalFile ?? _currentFile);
        }

        private void HideWebPane()
        {
            if (WebPaneHost.Visibility != Visibility.Visible) return;
            WebPaneHost.Visibility = Visibility.Collapsed;
            WebSumBtn.Visibility = Visibility.Collapsed;   // back to the book - the globe leaves with the pane
            WebPane.OnPaneHidden();
            ExitWebSidebarMode();
            ApplyBrowserToolbarFace(leads: false);
            App.SetSetting("LastLeadPane", "book");   // the book's floor again (v1.19.86)
            // v1.19.22: the browser stepped aside - park the page's transcript
            // and hand the chat back to the active book.
            if (_aiChatViewModel is { } chat && chat.IsWebContext)
            {
                chat.HandleWebContextCleared();
                if (AiChatOverlay?.Visibility == Visibility.Visible && _currentFile is not null)
                    _ = chat.InitializeForDocumentAsync(_currentFile);
            }
            // v1.19.23: the pane stepped aside - the button is a save button again.
            RefreshWebSaveButton(_originalFile ?? _currentFile);

            // v1.19.26: the browser that a search opened steps away - the
            // navigator that asked for the search returns to its book.
            if (_summaryHiddenForSearchPath is { } searchPath)
            {
                _summaryHiddenForSearchPath = null;
                if (_summaryWindow is null && _doc is not null
                    && !string.IsNullOrEmpty(_currentFile)
                    && string.Equals(searchPath, _currentFile, StringComparison.OrdinalIgnoreCase))
                {
                    OpenSummaryWindow();
                }
            }

            // v1.19.32: the browser stepped aside and the book's windows come
            // back - the recap companion the browser dismissed, then the
            // navigator the browser parked (its per-book wish survived the
            // switch-away close, so only the wish check is needed here).
            if (_recapHiddenForBrowser)
            {
                _recapHiddenForBrowser = false;
                if (_doc is not null && !string.IsNullOrEmpty(_currentFile))
                {
                    ToggleRecapCompanion();
                }
            }
            if (_summaryWindow is null && _doc is not null
                && !string.IsNullOrEmpty(_currentFile)
                && _summaryOpenByDoc.TryGetValue(_currentFile, out bool browserWanted) && browserWanted)
            {
                OpenSummaryWindow();
                // v1.19.34: the navigator returns where the reader left it -
                // the digest repaints, then the scroll climbs back.
                if (_summaryWindow is { } returned)
                {
                    returned.RestoreDigestScroll(_summaryParkedScrollOffset);
                }
            }
            _summaryParkedScrollOffset = 0.0;
        }

        // ── The text editor pane (v1.19.64) ─────────────────────────────────────
        // The document area's third face, built the browser pane's way: a host
        // that overlays the viewers, a pencil on the toolbar that toggles it, a
        // sidebar rail that mirrors the editor's own pages, and one interface
        // leading at a time - opening one face steps the others aside without
        // disturbing their state, so every return lands where the reader left.

        private bool _editorSidebarWasNotes;   // the sidebar mode the reader left behind
        private readonly List<System.Windows.Controls.Button> _editorPageCards = [];

        private void TextEditorBtn_Click(object sender, RoutedEventArgs e) => ToggleEditorPane();

        // The ribbon's editor file operations (v1.19.81): new tab, open and
        // save live beside the New button, before the Browser button. A
        // hidden editor wakes first - the operations never open a bare pane.
        private void EditorRibbonNewTab_Click(object sender, RoutedEventArgs e)
        {
            if (EditorPaneHost.Visibility != Visibility.Visible) ShowEditorPane();
            TextPane.OpenNewTab();
        }
        private void EditorRibbonOpen_Click(object sender, RoutedEventArgs e)
        {
            if (EditorPaneHost.Visibility != Visibility.Visible) ShowEditorPane();
            TextPane.OpenEditorDocument();
        }
        private void EditorRibbonSave_Click(object sender, RoutedEventArgs e)
        {
            if (EditorPaneHost.Visibility != Visibility.Visible) ShowEditorPane();
            TextPane.SaveEditorDocument();
        }

        private int _editorActivePage;

        private void ToggleEditorPane()
        {
            if (EditorPaneHost.Visibility == Visibility.Visible) HideEditorPane();
            else ShowEditorPane();
        }

        private void ShowEditorPane()
        {
            // One interface leads at a time: the browser steps aside first - its
            // hide may bring the navigator back, so the navigator parking below
            // runs after it and closes it with the switch-away face preserved.
            if (WebPaneHost.Visibility == Visibility.Visible) HideWebPane();
            // The editor takes the floor - the navigator and the recap companion
            // step out with it, exactly the bargain the browser struck in
            // v1.19.32, and a showing recap parks the same flag HideEditorPane
            // spends to bring it back.
            if (_summaryWindow is { } navigator && _currentFile is not null
                && navigator.DocumentPathEquals(_currentFile))
            {
                _summaryParkedScrollOffset = navigator.DigestScrollOffset;
                _summarySwitchAway = navigator;     // a switch, never the reader's hand
                navigator.Close();
                _summarySwitchAway = null;
            }
            if (Features.Summary.RecapController.HasOpenWindow)
            {
                _recapHiddenForBrowser = true;
                Features.Summary.RecapController.Dismiss();
            }
            EditorPaneHost.Visibility = Visibility.Visible;
            DocInvertBtn.Tag = TextPane.DocInverted ? "on" : null;   // the moon wears the sheet's face (v1.19.81)
            TextPane.OnPaneShown();
            EnterEditorSidebarMode();
            RefreshWebSaveButton(_originalFile ?? _currentFile);
            ApplyEditorToolbarFace(leads: true);
            App.SetSetting("LastLeadPane", "editor");   // the sheet led last (v1.19.86)
        }

        private void HideEditorPane()
        {
            if (EditorPaneHost.Visibility != Visibility.Visible) return;
            EditorPaneHost.Visibility = Visibility.Collapsed;
            DocInvertBtn.Tag = ActiveViewer.DocInvert ? "on" : null;   // back to the book's own night (v1.19.81)
            TextPane.OnPaneHidden();
            ExitEditorSidebarMode();
            ApplyEditorToolbarFace(leads: false);
            App.SetSetting("LastLeadPane", "book");   // the book's floor again (v1.19.86)
            _aiChatViewModel?.HandleEditorContextCleared();
            // The editor stepped aside and the book's windows come back - the
            // same welcome the browser's hide offers (recap first, then the
            // navigator whose per-book wish survived the switch-away close).
            if (_recapHiddenForBrowser)
            {
                _recapHiddenForBrowser = false;
                if (_doc is not null && !string.IsNullOrEmpty(_currentFile))
                {
                    ToggleRecapCompanion();
                }
            }
            if (_summaryWindow is null && _doc is not null
                && !string.IsNullOrEmpty(_currentFile)
                && _summaryOpenByDoc.TryGetValue(_currentFile, out bool editorWanted) && editorWanted)
            {
                OpenSummaryWindow();
                if (_summaryWindow is { } returned)
                {
                    returned.RestoreDigestScroll(_summaryParkedScrollOffset);
                }
            }
            _summaryParkedScrollOffset = 0.0;
        }

        // ── The sidebar's editor-page rail ──────────────────────────────────────

        private void EnterEditorSidebarMode()
        {
            _editorSidebarWasNotes = _sidebarShowingNotes;
            PageList.Visibility = Visibility.Collapsed;
            NotesPanel.Visibility = Visibility.Collapsed;
            PageControlsRow.Visibility = Visibility.Collapsed;
            WebTabsPanel.Visibility = Visibility.Collapsed;
            WebPane.TabCardsVisible = false;   // nobody is watching the web previews
            EditorPagesPanel.Visibility = Visibility.Visible;
            SidebarPagesTab.Tag = null;
            SidebarNotesTab.Tag = null;
            RebuildEditorPageCards();
        }

        private void ExitEditorSidebarMode()
        {
            if (EditorPagesPanel.Visibility != Visibility.Visible) return;
            EditorPagesPanel.Visibility = Visibility.Collapsed;
            if (_editorSidebarWasNotes) SwitchSidebarToNotesTab();
            else SwitchSidebarToPagesTab();
        }

        private string[]? _editorThumbs;   // one PNG data URL per editor page

        private void WireEditorPane()
        {
            TextPane.PageCountChanged += _ => RebuildEditorPageCards();
            TextPane.ActivePageChanged += HighlightEditorPage;
            TextPane.LinkOpenRequested += OpenEditorLink;
            TextPane.BrowserRequested += () => { HideEditorPane(); ShowWebPane(); };
            TextPane.PdfRequested += () => HideEditorPane();
            // The moon under the split-pane button wears the sheet's dark face
            // while the editor leads (v1.19.81): the page's own state lights it.
            TextPane.DocInvertChanged += on => DocInvertBtn.Tag = on ? "on" : null;
            TextPane.ActiveTabChanged += () =>
            {
                if (AiChatOverlay?.Visibility == Visibility.Visible)
                    _aiChatViewModel?.HandleEditorContextChanged(TextPane.ActiveTabId, TextPane.ActiveTabTitle);
            };
            // The page's own raster of each document page: in-place card image
            // update avoids destroying/rebuilding UI controls and eliminates flicker.
            TextPane.ThumbsChanged += thumbs =>
            {
                _editorThumbs = thumbs;
                if (EditorPagesPanel.Visibility == Visibility.Visible)
                    UpdateOrRebuildEditorPageCards();
            };
        }

        /// <summary>Updates existing page thumbnail images in place to avoid UI flashing,
        /// only rebuilding controls when the page count changes.</summary>
        private void UpdateOrRebuildEditorPageCards()
        {
            int n = TextPane.PageCount;
            if (_editorPageCards.Count != n)
            {
                RebuildEditorPageCards();
                return;
            }

            for (int i = 0; i < n; i++)
            {
                string? thumb = (_editorThumbs != null && i < _editorThumbs.Length)
                    ? _editorThumbs[i] : null;
                if (string.IsNullOrEmpty(thumb)) continue;

                var preview = EditorThumbImage(thumb);
                if (preview == null) continue;

                var card = _editorPageCards[i];
                if (card.Content is System.Windows.Controls.StackPanel stack)
                {
                    var face = stack.Children.OfType<System.Windows.Controls.Border>().FirstOrDefault();
                    if (face?.Child is System.Windows.Controls.Image img)
                    {
                        img.Source = preview;
                    }
                    else
                    {
                        var newImg = new System.Windows.Controls.Image
                        {
                            Source = preview,
                            Stretch = System.Windows.Media.Stretch.Uniform,
                            MaxWidth = 142,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        };
                        System.Windows.Media.RenderOptions.SetBitmapScalingMode(newImg,
                            System.Windows.Media.BitmapScalingMode.HighQuality);
                        var newFace = new System.Windows.Controls.Border
                        {
                            Background = System.Windows.Media.Brushes.White,
                            BorderThickness = new Thickness(1),
                            MaxWidth = 200,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Child = newImg,
                        };
                        newFace.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "SurfaceBrush");
                        stack.Children.Insert(0, newFace);
                    }
                }
            }
        }

        /// <summary>The editor's page count moved: the rail mirrors it - one card
        /// per page, the way the PDF list wears one thumbnail per page. Cards are
        /// built in code because their count is the editor's own fact, not the
        /// document's; the active page wears a bold face and a click scrolls.</summary>
        private void RebuildEditorPageCards()
        {
            EditorPagesList.Children.Clear();
            _editorPageCards.Clear();
            int n = TextPane.PageCount;
            EditorPagesHost.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 1; i <= n; i++)
            {
                string? thumb = (_editorThumbs != null && i - 1 < _editorThumbs.Length)
                    ? _editorThumbs[i - 1] : null;
                var card = BuildEditorPageCard(i, thumb);
                _editorPageCards.Add(card);
                EditorPagesList.Children.Add(card);
            }
            HighlightEditorPage(_editorActivePage);
        }

        private System.Windows.Controls.Button BuildEditorPageCard(int index, string? thumb)
        {
            // The PDF list's own item anatomy (v1.19.70), read off its
            // DataTemplate line for line: a StackPanel wearing the item's
            // 8,2,8,2 margins, the page's white face in a one-pixel Surface
            // frame no wider than 200, and the muted label under it. No icon,
            // no rounded chip, no ring: where the list collapses its frame for
            // a page it has not rendered, so does this row.
            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(8, 2, 8, 2) };
            var preview = string.IsNullOrEmpty(thumb) ? null : EditorThumbImage(thumb);
            if (preview is not null)
            {
                var img = new System.Windows.Controls.Image
                {
                    Source = preview,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    MaxWidth = 142,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(img,
                    System.Windows.Media.BitmapScalingMode.HighQuality);
                var face = new System.Windows.Controls.Border
                {
                    Background = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(1),
                    MaxWidth = 200,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = img,
                };
                face.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "SurfaceBrush");
                stack.Children.Add(face);
            }
            var label = new System.Windows.Controls.TextBlock
            {
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
                Text = string.Format(TryFindResource("Str_Editor_PageCard") as string ?? "Page {0}", index),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            // The label's own face is the list's muted one; the active row's
            // SelectionFg is handed it by HighlightEditorPage, the way the
            // list's DataTrigger does - never through a bold face.
            label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedTextBrush");
            label.SetResourceReference(System.Windows.Controls.TextBlock.FontFamilyProperty, "UiFont");
            stack.Children.Add(label);

            var card = new System.Windows.Controls.Button
            {
                Content = stack,
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            card.Template = EditorCardTemplate();
            card.Click += (_, _) => TextPane.ScrollToPage(index);
            return card;
        }

        /// <summary>Decode one of the editor's PNG data URLs. A thumb that will
        /// not decode keeps the card's icon face - the rail never blocks on it.</summary>
        private static System.Windows.Media.ImageSource? EditorThumbImage(string? dataUrl)
        {
            const string mark = "base64,";
            int at = dataUrl?.IndexOf(mark, System.StringComparison.Ordinal) ?? -1;
            if (at < 0) return null;
            try
            {
                byte[] bytes = System.Convert.FromBase64String(dataUrl![(at + mark.Length)..]);
                var img = new System.Windows.Media.Imaging.BitmapImage();
                using var ms = new System.IO.MemoryStream(bytes);
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        /// <summary>The card's face: the PDF list's own ListBoxItem template,
        /// factory for factory - a plain border over a ContentPresenter, hover
        /// painting RowHoverBrush, the active row painting SelectionBg - read
        /// off the card's Tag exactly as the list reads IsSelected. The row
        /// wears no chrome of its own; Foreground rides the row so the label
        /// under the page recolors with it, the list's own bargain.</summary>
        private static System.Windows.Controls.ControlTemplate EditorCardTemplate()
        {
            var bd = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border), "Bd");
            bd.SetValue(System.Windows.Controls.Border.BackgroundProperty,
                System.Windows.Media.Brushes.Transparent);
            bd.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(8, 6, 8, 6));
            bd.AppendChild(new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter)));
            var tpl = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button))
            {
                VisualTree = bd,
            };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new System.Windows.Setter(
                System.Windows.Controls.Border.BackgroundProperty,
                new System.Windows.DynamicResourceExtension("RowHoverBrush")) { TargetName = "Bd" });
            tpl.Triggers.Add(hover);
            var active = new Trigger { Property = System.Windows.Controls.Control.TagProperty, Value = "Active" };
            active.Setters.Add(new System.Windows.Setter(
                System.Windows.Controls.Border.BackgroundProperty,
                new System.Windows.DynamicResourceExtension("SelectionBg")) { TargetName = "Bd" });
            active.Setters.Add(new System.Windows.Setter(
                System.Windows.Controls.Control.ForegroundProperty,
                new System.Windows.DynamicResourceExtension("SelectionFg")));
            tpl.Triggers.Add(active);
            return tpl;
        }

        private void HighlightEditorPage(int page)
        {
            _editorActivePage = page;
            for (int i = 0; i < _editorPageCards.Count; i++)
            {
                // The active row wears the list's own paint (v1.19.70): the
                // template reads the Tag for its SelectionBg, and the label
                // takes SelectionFg the way the list's DataTrigger hands it -
                // no bold, no ring, exactly the PDF list's manners.
                _editorPageCards[i].Tag = (i + 1 == page) ? "Active" : null;
                if (_editorPageCards[i].Content is System.Windows.Controls.StackPanel sp)
                {
                    foreach (var child in sp.Children)
                    {
                        if (child is System.Windows.Controls.TextBlock tb)
                        {
                            tb.SetResourceReference(
                                System.Windows.Controls.TextBlock.ForegroundProperty,
                                (i + 1 == page) ? "SelectionFg" : "MutedTextBrush");
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>A link the editor handed over (Ctrl+click, or a page that asked
        /// for a window): it leaves the document for the system browser, the way
        /// every other outward link in the app travels.</summary>
        private void OpenEditorLink(string url)
        {
            // The app's own browser answers a link (v1.19.67), not the system's:
            // the pane comes forward and the link rides a tab of its own - the
            // same bargain the navigator's word search struck in v1.19.25. A
            // non-web scheme (mailto:, file:) still goes to the system, which
            // alone knows how to keep such promises.
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? u)
                && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            {
                ShowWebPane();
                WebPane.OpenLinkInNewTab(url);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // A link that refuses to open never takes the editor down.
            }
        }


        // ── The sidebar's web-tabs gallery (v1.19.5) ────────────────────────────────────
        // While the browser is up, the left rail shows its open views instead of the
        // page thumbnails of a book that is not on screen. The rail's own tab row
        // keeps working: PAGES belongs to the reader and closes the browser; NOTES
        // keeps the browser and trades the gallery for the notes cards.

        private bool _webSidebarWasNotes;   // the sidebar mode the reader left behind

        private void EnterWebSidebarMode()
        {
            _webSidebarWasNotes = _sidebarShowingNotes;
            PageList.Visibility = Visibility.Collapsed;
            NotesPanel.Visibility = Visibility.Collapsed;
            PageControlsRow.Visibility = Visibility.Collapsed;
            WebTabsPanel.Visibility = Visibility.Visible;
            WebPane.TabCardsVisible = true;   // v1.19.43: previews are watched again - the pulse may spend captures
            SidebarPagesTab.Tag = null;
            SidebarNotesTab.Tag = null;
        }

        private void ExitWebSidebarMode()
        {
            if (WebTabsPanel.Visibility != Visibility.Visible) return;
            WebTabsPanel.Visibility = Visibility.Collapsed;
            WebPane.TabCardsVisible = false;   // v1.19.43: nobody is watching the previews - the pulse stands down
            if (_webSidebarWasNotes) SwitchSidebarToNotesTab();
            else SwitchSidebarToPagesTab();
        }

        /// <summary>A gallery card was clicked: the browser switches to that
        /// view, matching the tab strip's behavior. A click raised by a button
        /// NESTED inside the card (the card's own ✕) never counts - it arrives
        /// here wearing the card as its sender, and acting on it would reopen
        /// the very tab the ✕ just closed.</summary>
        private void WebTabCard_Click(object sender, RoutedEventArgs e)
        {
            if (_webTabDragJustEnded) return;   // a drag's release is not a click
            if (!ReferenceEquals(e.OriginalSource, sender)) return;   // a nested button's click, not the card's own
            if (sender is Button { DataContext: Controls.WebTabCardVm card })
                WebPane.ActivateTab(card);
        }

        /// <summary>A gallery card's close button (v1.19.11) closed that tab: the
        /// same hand the strip's ✕ offers, now also where the cards live - the
        /// card leaves the gallery and the browser moves on, or the window steps
        /// aside when the last view closed.</summary>
        private void WebSidebarTabClose_Click(object sender, RoutedEventArgs e)
        {
            // v1.19.12: the ✕ lives INSIDE the card button. Click bubbles - an
            // unhandled one reaches the card and fires ActivateTab on the very
            // address the ✕ just closed, so the tab came back and landed at the
            // top of the rail. Handled here, the click dies with the close.
            // v1.19.13: the card, not the address, is what closes.
            e.Handled = true;
            if (sender is Button { DataContext: Controls.WebTabCardVm card })
                WebPane.CloseTab(card);
        }

        // ── Grab and move (v1.19.13) ────────────────────────────────────────────────
        // The strip's tabs and the rail's cards are one list wearing two faces, so a
        // drag on either reorders the same collection and both faces follow. A press
        // arms the drag, the drag threshold captures the mouse, the grabbed tab rides
        // a TranslateTransform under the pointer, a neighbor whose midpoint the
        // advancing edge crossed swaps with it, and the release settles the tab into
        // its slot with a short glide - the same dialect the reader's own PDF strip
        // speaks (PdfViewer.TabStrip.cs). The ✕ never starts a drag: its press dies
        // with the close.

        private Controls.WebTabCardVm? _webTabDragCard;
        private System.Windows.Controls.ItemsControl? _webTabDragFace;
        private System.Windows.FrameworkElement? _webTabDragElement;
        private Point _webTabDragStart;
        private double _webTabGrabOffset;
        private bool _webTabDragging;
        private bool _webTabDragJustEnded;

        private static System.Windows.FrameworkElement? WebTabContainer(
            System.Windows.Controls.ItemsControl face, object item)
            => face.ItemContainerGenerator.ContainerFromItem(item) as System.Windows.FrameworkElement;

        /// <summary>Midpoint X of a tab's LAYOUT slot (ignores any in-flight drag transform).</summary>
        private static double WebTabSlotMidX(System.Windows.FrameworkElement fe)
        {
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(fe);
            return slot.X + slot.Width / 2;
        }

        /// <summary>Midpoint Y of a card's LAYOUT slot (ignores any in-flight drag transform).</summary>
        private static double WebTabSlotMidY(System.Windows.FrameworkElement fe)
        {
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(fe);
            return slot.Y + slot.Height / 2;
        }

        private static void SetWebTabDragOffset(System.Windows.FrameworkElement tab, double x, double y)
        {
            if (tab.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(TranslateTransform.XProperty, null);   // drop any prior animation so the set sticks
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.X = x;
            tt.Y = y;
        }

        /// <summary>Did the press land on a button (the card's own ✕) rather than on
        /// the tab face? The ✕ keeps its press; the drag never starts over it.</summary>
        private static bool WebTabPressOnNestedButton(object source, System.Windows.DependencyObject stop)
        {
            System.Windows.DependencyObject? d = source as System.Windows.DependencyObject;
            while (d is not null && !ReferenceEquals(d, stop))
            {
                if (d is Button) return true;
                d = d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : System.Windows.LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private void WebStripTabDragDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.Border bd
                || bd.DataContext is not Controls.WebTabCardVm card) return;
            if (WebTabPressOnNestedButton(e.OriginalSource, bd)) return;   // the ✕ keeps its press
            _webTabDragCard = card;
            _webTabDragFace = WebTabStrip;
            _webTabDragElement = bd;
            _webTabDragStart = e.GetPosition(WebTabStrip);
            _webTabGrabOffset = WebTabContainer(WebTabStrip, card) is { } cont
                ? _webTabDragStart.X - System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(cont).X
                : 0;
            _webTabDragging = false;
        }

        private void WebStripTabDragMove(object sender, MouseEventArgs e)
        {
            if (_webTabDragCard is null || _webTabDragElement is null) return;
            double x = e.GetPosition(_webTabDragFace).X;
            if (!_webTabDragging)
            {
                if (Math.Abs(x - _webTabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
                _webTabDragging = true;
                try { _webTabDragElement.CaptureMouse(); } catch { /* the drag can live without it */ }
            }
            DragWebTab(x, horizontal: true);
        }

        private void WebStripTabDragUp(object sender, MouseButtonEventArgs e)
        {
            if (_webTabDragCard is null) return;
            EndWebTabDrag();
        }

        private void WebTabCardDragDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Button bd || bd.DataContext is not Controls.WebTabCardVm card) return;
            if (WebTabPressOnNestedButton(e.OriginalSource, bd)) return;   // the card's ✕ keeps its press
            // v1.19.14: the press itself routes. A click's own machinery has too many
            // quiet ways to lose a release - the drag threshold reading a firm hand
            // as a drag, the capture swallowing the up that would raise Click - so
            // the card answers the press, the way the strip's tabs and every browser
            // the reader has used always have. A release Click then lands on an
            // already-active tab and changes nothing.
            WebPane.ActivateTab(card);
            _webTabDragCard = card;
            _webTabDragFace = WebTabsList;
            _webTabDragElement = bd;
            _webTabDragStart = e.GetPosition(WebTabsList);
            _webTabGrabOffset = WebTabContainer(WebTabsList, card) is { } cont
                ? _webTabDragStart.Y - System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(cont).Y
                : 0;
            _webTabDragging = false;
        }

        private void WebTabCardDragMove(object sender, MouseEventArgs e)
        {
            if (_webTabDragCard is null || _webTabDragElement is null) return;
            double y = e.GetPosition(_webTabDragFace).Y;
            if (!_webTabDragging)
            {
                if (Math.Abs(y - _webTabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                _webTabDragging = true;
                try { _webTabDragElement.CaptureMouse(); } catch { /* the drag can live without it */ }
            }
            DragWebTab(y, horizontal: false);
        }

        private void WebTabCardDragUp(object sender, MouseButtonEventArgs e)
        {
            if (_webTabDragCard is null) return;
            bool wasDragging = _webTabDragging;
            EndWebTabDrag();
            if (wasDragging)
            {
                e.Handled = true;   // a drag's release never clicks the card
                _webTabDragJustEnded = true;   // belt and braces beside the swallowed up event
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    (Action)(() => _webTabDragJustEnded = false));
            }
        }

        /// <summary>The grab follows the pointer; a neighbor whose midpoint the
        /// dragged tab's advancing edge crossed swaps places with it - edges against
        /// midpoints, the PDF strip's own hysteresis, so a tab parked on a boundary
        /// does not bounce. The same collection underlies strip and rail, so one
        /// drag dresses both faces.</summary>
        private void DragWebTab(double coord, bool horizontal)
        {
            var face = _webTabDragFace;
            var card = _webTabDragCard;
            if (face is null || card is null) return;
            System.Windows.FrameworkElement? cont = WebTabContainer(face, card);
            if (cont is null) return;
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(cont);
            double rawLeft = coord - _webTabGrabOffset;
            System.Collections.ObjectModel.ObservableCollection<Controls.WebTabCardVm> tabs = WebPane.Tabs;
            int cur = tabs.IndexOf(card);
            if (horizontal)
            {
                double leftEdge = rawLeft;
                double rightEdge = rawLeft + cont.ActualWidth;
                if (cur + 1 < tabs.Count && WebTabContainer(face, tabs[cur + 1]) is { } right
                    && rightEdge > WebTabSlotMidX(right))
                {
                    tabs.Move(cur, cur + 1);
                    face.UpdateLayout();
                }
                else if (cur - 1 >= 0 && WebTabContainer(face, tabs[cur - 1]) is { } left
                    && leftEdge < WebTabSlotMidX(left))
                {
                    tabs.Move(cur, cur - 1);
                    face.UpdateLayout();
                }
            }
            else
            {
                double topEdge = rawLeft;
                double bottomEdge = rawLeft + cont.ActualHeight;
                if (cur + 1 < tabs.Count && WebTabContainer(face, tabs[cur + 1]) is { } down
                    && bottomEdge > WebTabSlotMidY(down))
                {
                    tabs.Move(cur, cur + 1);
                    face.UpdateLayout();
                }
                else if (cur - 1 >= 0 && WebTabContainer(face, tabs[cur - 1]) is { } up
                    && topEdge < WebTabSlotMidY(up))
                {
                    tabs.Move(cur, cur - 1);
                    face.UpdateLayout();
                }
            }
            cont = WebTabContainer(face, card);   // the slot moved with the swap
            if (cont is null) return;
            slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(cont);
            double maxLeft = Math.Max(0, horizontal ? face.ActualWidth - cont.ActualWidth
                                                    : face.ActualHeight - cont.ActualHeight);
            double renderLeft = Math.Min(Math.Max(0, rawLeft), maxLeft);
            SetWebTabDragOffset(cont, horizontal ? renderLeft - slot.X : 0,
                                       horizontal ? 0 : renderLeft - slot.Y);
        }

        /// <summary>The grab ends: the capture goes back, and the dragged tab
        /// settles from its dragged offset into its final slot with a short glide
        /// - the release should read as a landing, not a teleport.</summary>
        private void EndWebTabDrag()
        {
            var card = _webTabDragCard;
            var face = _webTabDragFace;
            var element = _webTabDragElement;
            bool wasDragging = _webTabDragging;
            _webTabDragCard = null;
            _webTabDragFace = null;
            _webTabDragElement = null;
            _webTabDragging = false;
            try { element?.ReleaseMouseCapture(); } catch { /* the capture was never taken */ }
            if (!wasDragging || card is null || face is null) return;
            if (WebTabContainer(face, card) is { } cont
                && cont.RenderTransform is TranslateTransform tt
                && (Math.Abs(tt.X) > 0.5 || Math.Abs(tt.Y) > 0.5))
            {
                var settle = new System.Windows.Media.Animation.DoubleAnimation(0,
                    new Duration(TimeSpan.FromMilliseconds(120)))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                };
                settle.Completed += (_, _) => CleanupWebTabTransforms(face);
                if (Math.Abs(tt.X) > 0.5) tt.BeginAnimation(TranslateTransform.XProperty, settle);
                if (Math.Abs(tt.Y) > 0.5) tt.BeginAnimation(TranslateTransform.YProperty, settle);
            }
            else CleanupWebTabTransforms(face);
        }

        private static void CleanupWebTabTransforms(System.Windows.Controls.ItemsControl face)
        {
            foreach (object item in face.Items)
                if (WebTabContainer(face, item) is { } c)
                    c.RenderTransform = null;
        }

        /// <summary>The toolbar's PDF button follows the screen (v1.19.23):
        /// while the browser leads with a live tab, the button the reader
        /// reaches for on a web page IS the page's chat - it opens the
        /// sidechat bound to that tab. With the browser away it keeps its
        /// old job: saving the web-downloaded PDF, and it exists only while
        /// the active document is one. Tooltip and caption follow the
        /// function, so the button always says what it does.</summary>
        private void RefreshWebSaveButton(string? path)
        {
            // While the browser leads, the toolbar keeps exactly three buttons -
            // New, Text Editor, PDF Editor - so the save/chat button never
            // resurrects itself onto it (v1.19.68).
            if (_browserLeads)
            {
                WebSavePdfBtn.Visibility = Visibility.Collapsed;
                return;
            }
            if (WebPaneHost.Visibility == Visibility.Visible && WebPane.ActiveTabId is not null)
            {
                WebSavePdfBtn.Visibility = Visibility.Visible;
                WebSavePdfBtn.SetResourceReference(FrameworkElement.ToolTipProperty, "Str_TT_WebPageChat");
                RetargetToolbarCaption(WebSavePdfBtn, "Str_Lbl_WebPageChat");
                return;
            }
            WebSavePdfBtn.SetResourceReference(FrameworkElement.ToolTipProperty, "Str_TT_SaveWebPdf");
            RetargetToolbarCaption(WebSavePdfBtn, "Str_Lbl_WebSavePdf");
            WebSavePdfBtn.Visibility = Controls.WebBrowserControl.IsWebDownloadsPath(path)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>One toolbar button's caption retargets in place
        /// (v1.19.23): the caption engine's recorded key swaps and the button
        /// re-dresses for the current label mode, so a button whose function
        /// follows the screen keeps saying what it does in every label mode.
        /// A key that already matches does nothing - the refresh runs on
        /// every pane and document change.</summary>
        private void RetargetToolbarCaption(Button btn, string labelKey)
        {
            for (int i = 0; i < _toolbarButtons.Count; i++)
            {
                if (!ReferenceEquals(_toolbarButtons[i].btn, btn)) continue;
                if (string.Equals(_toolbarButtons[i].labelKey, labelKey, StringComparison.Ordinal)) return;
                var glyph = _toolbarButtons[i].glyph;
                _toolbarButtons[i] = (btn, glyph, labelKey);
                SetToolbarButton(btn, glyph, labelKey, withLabel: true);
                InvalidateToolbarReflow();
                ReflowToolbar();
                return;
            }
        }

        /// <summary>Keep this web PDF: the reader's own Save As dialog, seeded
        /// for the temp-backed document (FileOperations aims it at Downloads),
        /// then the button re-reads the active tab - a saved copy is a real
        /// file now and the button steps aside.</summary>
        private void WebSavePdfBtn_Click(object sender, RoutedEventArgs e)
        {
            // v1.19.23: while the browser leads, this button opens the page's
            // chat - OpenAiChat binds web-first when the pane is on screen,
            // so the click lands in the active tab's own conversation. Save
            // stays the button's job whenever the browser is away.
            if (WebPaneHost.Visibility == Visibility.Visible && WebPane.ActiveTabId is not null)
            {
                OpenAiChat();
                return;
            }
            if (_doc is null) return;
            SaveAs_Click(sender, e);
            RefreshWebSaveButton(_originalFile ?? _currentFile);
        }

        /// <summary>Build (or cancel) the semantic research index on demand -
        /// the AI chat's embedding pass is opt-in since v1.19.5.</summary>
        private void AiSemanticBtn_Click(object sender, RoutedEventArgs e)
            => _aiChatViewModel?.ToggleSemanticResearch();
    }
}
