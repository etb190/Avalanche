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
            // The toolbar's default view vs the extended tool set (Tools switch).
            _toolsMode = Services.AppDataPaths.GetSetting("toolbar.tools") == "1";
            ApplyToolsMode();
            // Companion-window memory: which AI surfaces were open at last close.
            // Same store the session restore reads (OpenTabs), so a "close my tabs"
            // answer and these flags always agree about what a launch shows.
            _restoreChatOpen = App.GetSetting("ui.chat.open") == "1";
            _summaryRestorePending = App.GetSetting("ui.summary.open") == "1";
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
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
            ApplyGrainTexture();
            ApplyToolNumberTooltips();   // append the 1-9 toolbar positions to the tool tooltips
            BuildShortcutsOverlay();     // generate the shortcuts card from the single-source table (ShortcutsOverlay.cs)
            SourceInitialized += MainWindow_SourceInitialized;
            Closed += (_, _) => { _continuousRenderCts?.Cancel(); _doc?.Close(); CloseEngineDocumentSession(); App.CleanupSessionTemps(); };

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
        private Features.AI.AiTestWindow? _aiTestWindow;
        // Companion-window memory: the chat rail and the summary navigator reopen
        // with the app when they were open at last close (snapshot in OnClosing;
        // the summary, which needs a document, reopens on the first one that lands).
        private bool _restoreChatOpen;
        private bool _summaryRestorePending;

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
            var test = new Features.AI.AiTestWindow(
                this,
                _currentFile,
                _doc.PageCount,
                () => _aiSettingsViewModel!.ToGenConfig(),
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
            string path = _currentFile;
            int pageCount = _doc.PageCount;
            var summary = new Features.Summary.SummaryWindow(
                this,
                path,
                Features.AI.DocumentIndexer.ComputeDocumentId(path),
                pageCount,
                () => _currentPage,
                () => _aiSettingsViewModel!.ToGenConfig(),
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
            summary.Closed += (_, _) =>
            {
                RefreshSummaryPageHighlight();      // the page list returns to the single page
                if (ReferenceEquals(_summaryWindow, summary))
                {
                    _summaryWindow = null;
                }
            };
            _summaryWindow = summary;
            summary.Show();
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
                _aiChatViewModel = new Features.AI.AiChatViewModel(
                    this,
                    () => _aiSettingsViewModel.ToGenConfig(),
                    Loc);
                AiChatOverlay.DataContext = _aiChatViewModel;
            }

            // Initialize for current document (no-op when none is open)
            if (_currentFile is not null)
            {
                _ = _aiChatViewModel.InitializeForDocumentAsync(_currentFile); // fire-and-forget, UI stays responsive
            }

            AiChatOverlay.Visibility = Visibility.Visible;
            AiChatInput?.Focus();
        }

        /// <summary>
        /// The active document changed (tab switch, new open, close):
        /// drop the citation highlight, which pointed at the previous
        /// document's page, and re-bind the chat to the new file.
        /// </summary>
        private void ActiveDocumentChanged(string? filePath)
        {
            ClearAiSourceHighlight();
            // The AI test window probes ONE document; switching documents would
            // leave it auditing a stale file, so it goes with the old one.
            if (_aiTestWindow is { } test &&
                (filePath is null || !test.DocumentPathEquals(filePath)))
            {
                test.Close();
            }

            // The notes cards audited the previous document; the new one
            // reseeds the From/To pair and empties the panel.
            ResetNotesForDocument(filePath);

            _aiChatViewModel?.HandleDocumentSwitch(
                filePath,
                panelVisible: AiChatOverlay?.Visibility == Visibility.Visible);

            // Session restore: the summary navigator was open at last close. It needs
            // a document, so it reopens on the first one that comes up - by then
            // _currentFile/_doc are live and the window boots straight into the file
            // the reader left off in. One-shot: closing it afterwards stays closed.
            if (_summaryRestorePending && filePath is not null &&
                _summaryWindow is null && !string.IsNullOrEmpty(_currentFile) && _doc is not null)
            {
                _summaryRestorePending = false;
                OpenSummaryWindow();
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
    }
}
