using System.Diagnostics;
using System.IO;
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
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using Avalanche.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace Avalanche
{
    public partial class MainWindow
    {
        private void RotatePages_Click(int delta)
        {
            if (_doc is null) return;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) return;
            try
            {
                UndoEntry? documentUndo = CaptureDocumentUndo();
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                // #169: rotation must not destroy the overlay annotations - the reload's default
                // keepAnnotations:false cleared them all, losing committed unsaved work on the
                // second rotation after placing it. Remap each rotated page's annotations through
                // the turn (render dims are still the pre-turn frame here; the reload clears them)
                // and keep everything through the reload. A page with no cached render dims keeps
                // its annotations unmapped - recoverable beats deleted.
                foreach (var idx in indices)
                    if (_annotations.TryGetValue(idx, out var anns) && _renderDims.TryGetValue(idx, out var dims))
                        Services.AnnotationRotate.Remap(anns, delta, dims.w, dims.h);
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload(
                    keepAnnotations: true,
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageTurns(
                            rotations, indices, delta),
                    documentUndo: documentUndo);
                PageList.SelectedIndex = Math.Min(restoreIdx, PageList.Items.Count - 1);
                // After a rotation the page aspect ratio changes; always fit-to-page so the
                // full rotated page is visible regardless of the previous zoom level.
                FitToPage();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() => FitToPage()));
                SetStatus(string.Format(Loc("Str_Rotated"), indices.Count));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_RotateFailed"), ex.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            var currentFile = _currentFile;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, Loc("Str_Dlg_SelectExtract")); return; }
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
                          { Filter = Loc("Str_Filter_Pdf") + "|*.pdf", Title = Loc("Str_Dlg_SaveExtractedAs"),
                            CheckFileExists = false, CheckPathExists = true };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                int[] ordered = [.. indices.OrderBy(index => index)];
                PdfEngineIntegration.ExtractPages(
                    currentFile, dlg.FileName, ordered, _pageRotations);
                SetStatus(string.Format(Loc("Str_Extracted"), indices.Count, System.IO.Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_SplitFailed") + "\n" + ex.Message, "Avalanche", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, Loc("Str_Dlg_SelectDelete")); return; }
            var result = KillerDialog.Show(this, selected.Count == 1 ? Loc("Str_Dlg_DeletePage1") : string.Format(Loc("Str_Dlg_DeletePagesN"), selected.Count), "Avalanche",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                UndoEntry? documentUndo = CaptureDocumentUndo();
                var annotationBackup = _annotations.ToDictionary(
                    pair => pair.Key, pair => pair.Value);
                try
                {
                    PageAnnotationInsertion.RemovePages(_annotations, indices);
                    SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: path => PdfEngineIntegration.RemovePages(path, indices),
                        remapRotations: rotations =>
                            PdfEngineIntegration.RemapRotationsAfterPageRemoval(rotations, indices),
                        documentUndo: documentUndo);
                }
                catch
                {
                    _annotations.Clear();
                    foreach (var pair in annotationBackup)
                    {
                        foreach (PageAnnotation annotation in pair.Value)
                            annotation.PageIndex = pair.Key;
                        _annotations[pair.Key] = pair.Value;
                    }
                    throw;
                }
                SetStatus(string.Format(Loc("Str_Deleted"), indices.Count, _doc?.PageCount));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_DeleteFailed") + "\n" + ex.Message, "Avalanche", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            int insertAfter = PageList.SelectedIndex >= 0
                ? PageList.SelectedIndex : _doc.PageCount - 1;
            int insertIndex = insertAfter + 1;
            var (pageWidth, pageHeight) = EnsureEngineDocumentSession()
                .VisualPageSize(Math.Max(0, insertAfter), _pageRotations);
            var sizeDialog = new BlankPageDialog(this, pageWidth, pageHeight);
            if (sizeDialog.ShowDialog() != true) return;
            try
            {
                UndoEntry? documentUndo = CaptureDocumentUndo();
                var annotationBackup = _annotations.ToDictionary(
                    pair => pair.Key, pair => pair.Value);
                try
                {
                    PageAnnotationInsertion.Shift(_annotations, insertIndex, 1);
                    SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: path =>
                            PdfEngineIntegration.InsertBlankPage(path, insertIndex,
                                sizeDialog.WidthPoints, sizeDialog.HeightPoints),
                        remapRotations: rotations =>
                            PdfEngineIntegration.RemapRotationsAfterPageInsertion(
                                rotations, insertIndex),
                        documentUndo: documentUndo);
                }
                catch
                {
                    _annotations.Clear();
                    foreach (var pair in annotationBackup)
                    {
                        foreach (PageAnnotation annotation in pair.Value)
                            annotation.PageIndex = pair.Key;
                        _annotations[pair.Key] = pair.Value;
                    }
                    throw;
                }
                PageList.SelectedIndex = insertIndex;
                SetStatus(string.Format(Loc("Str_St_InsertedBlank"), insertAfter + 2));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_InsertFailed") + "\n" + ex.Message, "Avalanche", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Appends a blank page to the END of the document. Used by the page-agnostic context menu
        // (sidebar empty area / outside the page), where there's no specific page to insert relative to.
        private void AddBlankPageAtEnd()
        {
            if (_doc is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            int referencePage = Math.Clamp(PageList.SelectedIndex >= 0
                ? PageList.SelectedIndex : _currentPage, 0, _doc.PageCount - 1);
            var (pageWidth, pageHeight) = EnsureEngineDocumentSession()
                .VisualPageSize(referencePage, _pageRotations);
            var sizeDialog = new BlankPageDialog(this, pageWidth, pageHeight);
            if (sizeDialog.ShowDialog() != true) return;
            try
            {
                int insertIndex = _doc.PageCount;
                SaveTempAndReload(
                    keepAnnotations: true,
                    finalizeSavedFile: path =>
                        PdfEngineIntegration.InsertBlankPage(path, insertIndex,
                            sizeDialog.WidthPoints, sizeDialog.HeightPoints),
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageInsertion(
                            rotations, insertIndex));
                if (PageList.Items.Count > 0) PageList.SelectedIndex = PageList.Items.Count - 1;
                SetStatus(string.Format(Loc("Str_St_AddedBlank"), _doc?.PageCount));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Err_AddPageFailed") + "\n" + ex.Message, "Avalanche", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex <= 0) return;
            int idx = PageList.SelectedIndex;
            SaveTempAndReload(
                finalizeSavedFile: path => PdfEngineIntegration.MovePage(path, idx, idx - 1),
                remapRotations: rotations =>
                    PdfEngineIntegration.RemapRotationsAfterPageMove(rotations, idx, idx - 1));
            PageList.SelectedIndex = idx - 1;
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex < 0 || PageList.SelectedIndex >= _doc.PageCount - 1) return;
            int idx = PageList.SelectedIndex;
            SaveTempAndReload(
                finalizeSavedFile: path => PdfEngineIntegration.MovePage(path, idx, idx + 1),
                remapRotations: rotations =>
                    PdfEngineIntegration.RemapRotationsAfterPageMove(rotations, idx, idx + 1));
            PageList.SelectedIndex = idx + 1;
        }

        // Cancels the previous thumbnail background load when the file changes.
        // The FOCUSED pane's thumbnail loader token. The panes keep their own (PdfViewer.ThumbCts):
        // one window-wide token had each pane canceling the other's decode.
        private System.Threading.CancellationTokenSource? _thumbCts
        {
            get => ActiveViewer.ThumbCts;
            set => ActiveViewer.ThumbCts = value;
        }

        /// <summary>Re-seat the newly focused pane's thumbnails instead of rebuilding them.
        ///
        /// Not folded into RefreshPageList as a general cache: every other caller is calling it
        /// BECAUSE the pages changed, and a cache keyed on the file path would make those no-op and
        /// leave stale thumbnails on screen. Only a focus switch knows nothing changed.</summary>
        internal void RestorePageListForActivePane() => RestorePageListCore(ActiveViewer);

        /// <summary>v1.19.37: the TAB-switch route into the same re-seat. A tab flip reaches the
        /// sidebar through BootstrapDocumentView's RefreshPageList, and rebuilding there
        /// re-decoded every page thumbnail from the file on every flip - a second full pdfium
        /// parse of the book per switch, which read exactly like a first open. The bridge
        /// diverts that one call here (inside the arriving viewer's context), the session's own
        /// array is shown as-is, and any unusable case falls back to the full refresh.</summary>
        internal void RestorePageListForTabSwitch() => RestorePageListCore(ActiveViewer);

        /// <summary>The shared re-seat body. `viewer` owns the cache being offered: within
        /// RunWithViewerContext the ActiveViewer IS the arriving tab, so both routes read the
        /// session whose list belongs on screen.</summary>
        private void RestorePageListCore(Controls.PdfViewer viewer)
        {
            var cached = viewer.ThumbCache;
            int preservedPage = viewer.CurrentPageIndex;

            // v1.19.28: a cache is only usable while the file it decoded is the
            // file on disk - a tab edited while it sat behind (a save, an OCR
            // pass, a temp reload) must fall back to a fresh decode, not paint
            // the look it wore when it left the screen.
            bool fileCurrent = viewer.ThumbCacheStamp is { } stamp
                       && _currentFile != null
                       && System.IO.File.Exists(_currentFile)
                       && System.Math.Abs(
                           (System.IO.File.GetLastWriteTimeUtc(_currentFile) - stamp).TotalSeconds) < 2.0;

            bool usable = cached != null
                       && _doc != null
                       && _currentFile != null
                       && fileCurrent
                       && viewer.ThumbCacheComplete
                       && cached.Length == _doc.PageCount
                       && string.Equals(viewer.ThumbCacheFile, _currentFile,
                                        System.StringComparison.OrdinalIgnoreCase);

            if (!usable) { RefreshPageList(); return; }

            // The reading-range paint rides the item VMs, so a cached array can carry the range
            // a navigator brushed onto it before the tab was switched away. The range belongs to
            // the navigator, not to the list: with no live navigator on screen the re-seat strips
            // the flags - a returning navigator repaints its own range on arrival anyway.
            if (_summaryWindow is not { IsVisible: true }
                && cached is { } liveList && liveList.Any(vm => vm.IsInRange))
            {
                foreach (var vm in liveList) vm.IsInRange = false;
            }

            // No cancel here. The panes own their thumbnail lists and their loader tokens
            // separately, so the other pane's decode is writing into ITS array and should be left
            // to finish - canceling it was what left a pane showing page labels with no pictures
            // after any focus change.
            _sidebarPages.Show(cached);
            viewer.SyncPageListSelection(preservedPage);
        }

        internal void RefreshPageList()
        {
            var thumbnailOwner = ActiveViewer;
            // Cancel any in-flight thumbnail load for the previous file.
            _thumbCts?.Cancel();
            // Hold the token locally rather than reading it back off _thumbCts. That property
            // forwards to the ACTIVE TAB (PdfViewer.ThumbCts), and both sides are guarded on
            // _active, so with no tab open the write is dropped and the read comes back null.
            // A killerpdf: launch reaches here in exactly that state: it goes to
            // OpenFromExternal, the one startup path that never calls EnsureInitialSession.
            var freshCts = new System.Threading.CancellationTokenSource();
            _thumbCts = freshCts;
            var ct = freshCts.Token;

            if (_doc is null || _currentFile is null)
            {
                _sidebarPages.Show(null);
                return;
            }

            int    pageCount = EnsureEngineDocumentSession().Pages.Count;
            string filePath  = _currentFile;
            int preservedPage = ActiveViewer.CurrentPageIndex;

            // Snapshot rotations on the UI thread before going to background.
            var rotSnap = new Dictionary<int, int>(_pageRotations);

            // Carry forward any existing thumbnails so the list never flashes blank
            // during reload (e.g. after a rotation).  New thumbnails replace them as
            // the background loader finishes each page.
            var oldItems = PageList.ItemsSource is PageThumbnailVm[] oi ? oi : null;

            var items = new PageThumbnailVm[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                rotSnap.TryGetValue(i, out int rot);
                items[i] = new PageThumbnailVm(i, filePath, rot);
                // Seed with stale thumbnail - better than blank while reloading
                if (oldItems != null && i < oldItems.Length)
                {
                    var prev = oldItems[i].Thumbnail;
                    if (prev != null) items[i].SetThumbnailDirect(prev);
                }
            }
            _sidebarPages.Show(items);
            ActiveViewer.SyncPageListSelection(preservedPage);

            // Hand the array to the pane it belongs to, so focusing away and back can re-seat it
            // rather than decode the document again. RestorePageListForActivePane is the only reader.
            ActiveViewer.ThumbCache     = items;
            ActiveViewer.ThumbCacheFile = filePath;
            ActiveViewer.ThumbCacheStamp = System.IO.File.GetLastWriteTimeUtc(filePath);   // v1.19.28
            ActiveViewer.ThumbCacheComplete = false;

            // Load thumbnails sequentially on a background thread via a single doc reader.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(128, 256));
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            using var pr  = docReader.GetPageReader(i);
                            int tw  = pr.GetPageWidth();
                            int th  = pr.GetPageHeight();
                            var raw = Services.PdfiumInterop.RenderPageWithAnnotations(filePath, i, tw, th)
                                ?? pr.GetImage();   // #141
                            if (tw <= 0 || th <= 0 || raw == null || raw.Length < tw * th * 4)
                                continue;
                            rotSnap.TryGetValue(i, out int rot);
                            if (rot != 0)
                                (raw, tw, th) = BitmapHelpers.RotateBitmap(raw, tw, th, rot);
                            var src = PageThumbnailVm.BuildThumbFromRaw(raw, tw, th);
                            if (src != null && !ct.IsCancellationRequested)
                                items[i].SetThumbnail(src);
                        }
                        catch { /* skip failed thumbnail; item shows label-only */ }
                    }
                    if (!ct.IsCancellationRequested)
                        thumbnailOwner.Dispatcher.Invoke(() =>
                            thumbnailOwner.MarkThumbnailCacheComplete(items));
                }
                catch { /* docReader open failed; all items remain label-only */ }
            }, ct);
        }

        // ── v1.19.28: the visited page's thumbnail follows the file ─────────────
        // The list decodes once per build; a page the reader revisits showed
        // the look the file had when the list was built. Routing to a page now
        // re-renders that one thumbnail from the current working file, so a
        // rotation, a save or an edit surfaces the moment the reader arrives.
        // A debounce keeps a fast scroll or a page-run from rendering every
        // page it crosses: only the page the reader lands on is decoded.

        private System.Windows.Threading.DispatcherTimer? _pageThumbTimer;
        private List<(int Page, System.Windows.Rect? Crop)> _pageThumbPending = new();
        private System.Threading.CancellationTokenSource? _pageThumbCts;

        // v1.19.31: a pending SET - a TwoPage route covers a whole spread, so
        // one arrival can refresh two pages at once. The debounce is unchanged:
        // the batch is whatever the last arrival left behind.
        internal void RefreshPageThumbnailsSoon(
            IReadOnlyList<(int Page, System.Windows.Rect? Crop)> targets)
        {
            if (_doc is null || _currentFile is null) return;
            if (targets is null || targets.Count == 0) return;
            _pageThumbPending = targets.ToList();
            if (_pageThumbTimer is null)
            {
                _pageThumbTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = System.TimeSpan.FromMilliseconds(160)
                };
                _pageThumbTimer.Tick += (_, _) =>
                {
                    _pageThumbTimer!.Stop();
                    var batch = _pageThumbPending;
                    _pageThumbPending = new List<(int, System.Windows.Rect?)>();
                    if (batch.Count == 0) return;
                    RefreshPageThumbnails(batch);
                };
            }
            _pageThumbTimer.Stop();
            _pageThumbTimer.Start();
        }

        private void RefreshPageThumbnails(
            List<(int Page, System.Windows.Rect? Crop)> batch)
        {
            if (_doc is null || _currentFile is null) return;
            if (PageList.ItemsSource is not PageThumbnailVm[] items) return;
            string filePath = _currentFile;

            _pageThumbCts?.Cancel();
            _pageThumbCts?.Dispose();
            _pageThumbCts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken ct = _pageThumbCts.Token;

            // Capture the VMs and rotations on the UI thread, then render the
            // batch in one worker: a spread's two pages land one after the
            // other instead of the second cancelling the first.
            var jobs = new List<(PageThumbnailVm Vm, int Page, int Rot, System.Windows.Rect? Crop)>();
            foreach ((int page, System.Windows.Rect? crop) in batch)
            {
                if (page < 0 || page >= items.Length) continue;
                int rot = _pageRotations.TryGetValue(page, out int r) ? r : 0;
                jobs.Add((items[page], page, rot, crop));
            }
            if (jobs.Count == 0) return;

            _ = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var job in jobs)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var src = PageThumbnailVm.BuildThumb(filePath, job.Page, job.Rot, job.Crop);
                        if (src != null && !ct.IsCancellationRequested) job.Vm.SetThumbnail(src);
                    }
                    catch { /* a thumbnail that will not render keeps the old one */ }
                }
            }, ct);
        }
    }
}
