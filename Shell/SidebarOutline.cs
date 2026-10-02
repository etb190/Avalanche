// Shell/SidebarOutline.cs - the sidebar's PAGES tab: its click handler and
// the tab switch. The outline/bookmark panel that shared this file went away
// with the Outlines tab (replaced by the Notes panel in Shell/SidebarNotes.cs);
// the toolbar tool-button handlers still live here beside the tab switch.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Avalanche
{    public partial class MainWindow
    {
        // ============================================================
        // Sidebar tab strip: PAGES | NOTES (the notes side lives in
        // Shell/SidebarNotes.cs)
        // ============================================================

        private void SidebarPagesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToPagesTab();

        private const double SidebarMaxPages = 234;   // stops when the 200px-capped thumbnail fills (200 + margins + scrollbar)
        private const double SidebarMinOpen = 120;   // narrowest readable width before labels/header clip

        private void SwitchSidebarToPagesTab()
        {
            _sidebarShowingNotes = false;
            PageList.Visibility = Visibility.Visible;
            NotesPanel.Visibility = Visibility.Collapsed;
            PageControlsRow.Visibility = _doc != null ? Visibility.Visible : Visibility.Collapsed;   // no empty box when nothing is open
            SidebarPagesTab.Foreground = (Brush)FindResource("PrimaryBrush");
            SidebarNotesTab.Foreground = (Brush)FindResource("MutedTextBrush");
            // Save current notes width before snapping back to pages.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedNotesWidth = Math.Min(_sidebarCol.ActualWidth, SbPx(SidebarMaxNotes));

            SidebarSplitter.IsEnabled = true;   // pages are resizable too now (drag the splitter)
            _sidebarCol.MaxWidth = SbPx(SidebarMaxPages);
            if (!_sidebarCollapsed)
            {
                double target = _savedPagesWidth;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    (Action)(() => _sidebarCol.Width = new GridLength(target)));
            }
        }

        private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Select);
        private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Text);
        private void ToolFormField_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.FormField);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolLine_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Line);
        private void ToolDraw_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
        private void ToolShape_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Shape);
        private void ToolImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Image);
        private void ToolCrop_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Crop);
        private void ToolMeasure_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Measure);
        private void ToolSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_signaturePopup is not null)
            {
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null)
                    SetTool(EditTool.Select);
                return;
            }
            SetTool(EditTool.Signature);
            ShowSignaturePopup();
        }
    }
}
