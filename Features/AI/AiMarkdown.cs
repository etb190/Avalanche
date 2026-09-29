using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Bridges ChatMessage.Content into a read-only RichTextBox document so the
    /// chat text is selectable and copyable, and assistant answers render
    /// markdown (bold, italic, bold-italic, inline code, fenced code blocks,
    /// headings, bullet/numbered lists, block quotes, links, rules) instead of
    /// showing literal asterisks and hashtags. User messages are inserted
    /// verbatim - their typed characters are never reinterpreted.
    /// Attach as:  ai:AiMarkdown.Text="{Binding Content}"
    /// The document is rebuilt whenever Content changes (ChatMessage raises
    /// PropertyChanged, so the reply can arrive after the bubble appears).
    /// </summary>
    internal static class AiMarkdown
    {
        // ---------- attached property: raw message text ----------

        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(AiMarkdown),
            new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

        public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);

        public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);

        // Marker used to attach the hyperlink navigation handler exactly once
        // per RichTextBox (documents are rebuilt on every Content change, but
        // AddHandler must not be called repeatedly).
        private static readonly DependencyProperty LinkHookProperty = DependencyProperty.RegisterAttached(
            "LinkHook", typeof(bool), typeof(AiMarkdown), new PropertyMetadata(false));

        /// <summary>
        /// Raised when a numbered citation footnote rendered inside an answer
        /// bubble is clicked. Carries the bubble's message (whose Sources list
        /// resolves the marker) and the cited AiSource. The chat view model
        /// subscribes once and navigates the PDF viewer to the cited passage.
        /// </summary>
        public static event Action<ChatMessage, AiSource>? CitationClicked;

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not RichTextBox rtb)
                return;

            EnsureHandlers(rtb);

            var text = e.NewValue as string ?? string.Empty;

            // User messages render verbatim; assistant/system messages render
            // as markdown. The DataContext is the ChatMessage from the template.
            bool parse = true;
            if (rtb.DataContext is ChatMessage msg)
                parse = msg.MessageRole != ChatMessage.Role.User;

            var doc = BuildDocument(text, parse, rtb);
            rtb.Document = doc;

            // A RichTextBox always stretches to the full available width, which
            // would turn the right-aligned user bubbles into full-width rows.
            // Cap the control at the document's natural (unwrapped) width so
            // bubbles hug their content exactly like the old TextBlock did;
            // longer content still wraps because the cap exceeds the column.
            rtb.MaxWidth = Math.Ceiling(MeasureNaturalWidth(doc, rtb) * 1.04) + 10.0;
        }

        // ---------- markdown patterns ----------

        private static readonly Regex HeadingRx = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex RuleRx = new(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);
        private static readonly Regex BulletRx = new(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex NumberedRx = new(@"^\s*(\d{1,3})[.)]\s+(.*)$", RegexOptions.Compiled);
        private static readonly Regex QuoteRx = new(@"^\s*>\s?(.*)$", RegexOptions.Compiled);

        // Inline styling, ordered so the longest marker wins at a position:
        // ***bold italic***, **bold**, __bold__, *italic*, _italic_ (the two
        // underscore rules use word-boundary guards so snake_case words stay
        // literal), `code`, [label](url).
        private static readonly Regex InlineRx = new(
            @"\*\*\*(?<bi>[^*\s][^*]*?)\*\*\*"
            + @"|\*\*(?<b>[^*]+?)\*\*"
            + @"|__(?<u>[^\s_][^_]*?)__"
            + @"|(?<![\w*])\*(?<i>[^\s*][^*]*?)\*(?![\w*])"
            + @"|(?<![\w_])_(?<em>[^\s_][^_]*?)_(?![\w_])"
            + @"|`(?<c>[^`]+)`"
            + @"|\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)\)",
            RegexOptions.Compiled);

        // ---------- hyperlink navigation ----------

        private static void EnsureHandlers(RichTextBox rtb)
        {
            if ((bool)rtb.GetValue(LinkHookProperty))
                return;
            rtb.SetValue(LinkHookProperty, true);
            // RequestNavigate bubbles from the Hyperlink inlines up to the box.
            rtb.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnLinkNavigate));
            // Citation footnotes are Buttons embedded via InlineUIContainer;
            // their Click bubbles up here where the box's DataContext (the
            // ChatMessage) resolves the clicked SOURCE_n back to its AiSource.
            rtb.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnCitationClick));
        }

        private static void OnCitationClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not Button { Tag: string sourceId })
                return;
            if (sender is not RichTextBox rtb || rtb.DataContext is not ChatMessage msg)
                return;

            var source = FindSource(msg.Sources, AiCitations.ParseSourceId(sourceId));
            if (source is null)
                return; // unknown citation id: nothing trustworthy to show

            CitationClicked?.Invoke(msg, source);
            e.Handled = true;
        }

        private static void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                if (e.Uri is not null && e.Uri.IsAbsoluteUri)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.ToString())
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // Browser launch failed; leaving the link inert is better than
                // crashing the chat over an external-app issue.
            }
            e.Handled = true;
        }

        // ---------- document building ----------

        internal static FlowDocument BuildDocument(string text, bool parseMarkdown, RichTextBox? rtb)
        {
            var doc = new FlowDocument { PagePadding = new Thickness(0) };
            // Footnote display numbers are handed out in order of first appearance
            // as the circles are created (skipping fences/quotes/headings, which
            // never render citations). See AiCitationNumberer.
            var numberer = new AiCitationNumberer();
            // Theme-aware defaults so bubbles follow Dark/Light/98SE/... live.
            doc.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            doc.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
            doc.FontSize = rtb is not null && rtb.FontSize > 0 ? rtb.FontSize : 12.0;

            // Citation footnotes resolve against the bubble's own message so
            // each circle knows its page/quote (tooltip) and its click target.
            IReadOnlyList<AiSource> sources = (rtb?.DataContext as ChatMessage)?.Sources ?? new List<AiSource>();

            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            bool inFence = false;
            var fence = new List<string>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd();

                if (parseMarkdown && line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (inFence)
                    {
                        AddCodeBlock(doc, fence);
                        fence.Clear();
                    }
                    inFence = !inFence;
                    continue;
                }
                if (inFence)
                {
                    fence.Add(rawLine);
                    continue;
                }

                if (line.Length == 0)
                {
                    AddSpacer(doc);
                    continue;
                }

                if (parseMarkdown)
                {
                    var h = HeadingRx.Match(line);
                    if (h.Success)
                    {
                        AddHeading(doc, h.Groups[1].Value.Length, h.Groups[2].Value);
                        continue;
                    }

                    if (RuleRx.IsMatch(line))
                    {
                        AddRule(doc);
                        continue;
                    }

                    var bullet = BulletRx.Match(line);
                    if (bullet.Success)
                    {
                        AddListParagraph(doc, "\u2022\u00A0", bullet.Groups[1].Value, parseMarkdown, sources, numberer);
                        continue;
                    }

                    var numbered = NumberedRx.Match(line);
                    if (numbered.Success)
                    {
                        AddListParagraph(doc, numbered.Groups[1].Value + ".\u00A0", numbered.Groups[2].Value, parseMarkdown, sources, numberer);
                        continue;
                    }

                    var quote = QuoteRx.Match(line);
                    if (quote.Success)
                    {
                        AddQuote(doc, quote.Groups[1].Value);
                        continue;
                    }
                }

                AddBody(doc, line, parseMarkdown, sources, numberer);
            }

            if (inFence && fence.Count > 0)
                AddCodeBlock(doc, fence); // unterminated fence: keep the content

            if (doc.Blocks.Count == 0)
                doc.Blocks.Add(new Paragraph(new Run(text)));

            return doc;
        }

        private static void AddBody(FlowDocument doc, string line, bool parse, IReadOnlyList<AiSource> sources, AiCitationNumberer numberer)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
            if (parse)
                AddInlines(p.Inlines, line, sources, numberer);
            else
                p.Inlines.Add(new Run(line));
            doc.Blocks.Add(p);
        }

        private static void AddHeading(FlowDocument doc, int level, string text)
        {
            var p = new Paragraph { Margin = new Thickness(0, 4, 0, 3) };
            var run = new Run(text)
            {
                FontWeight = FontWeights.SemiBold,
                FontSize = level switch { 1 => 15.0, 2 => 13.5, _ => 12.5 }
            };
            run.SetResourceReference(TextElement.ForegroundProperty, "PrimaryBrush");
            p.Inlines.Add(run);
            doc.Blocks.Add(p);
        }

        private static void AddListParagraph(FlowDocument doc, string marker, string content, bool parse, IReadOnlyList<AiSource> sources, AiCitationNumberer numberer)
        {
            // Hanging indent: marker hangs into the 12px left margin so wrapped
            // lines align under the text, not under the marker.
            var p = new Paragraph { Margin = new Thickness(12, 0, 0, 2), TextIndent = -12 };
            var mark = new Run(marker);
            mark.SetResourceReference(TextElement.ForegroundProperty, "MutedTextBrush");
            p.Inlines.Add(mark);
            if (parse)
                AddInlines(p.Inlines, content, sources, numberer);
            else
                p.Inlines.Add(new Run(content));
            doc.Blocks.Add(p);
        }

        private static void AddQuote(FlowDocument doc, string text)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(2, 0, 0, 2),
                Padding = new Thickness(8, 2, 4, 2),
                BorderThickness = new Thickness(2, 0, 0, 0)
            };
            p.SetResourceReference(Block.BorderBrushProperty, "CardBorderBrush");
            var run = new Run(text) { FontStyle = FontStyles.Italic };
            run.SetResourceReference(TextElement.ForegroundProperty, "MutedTextBrush");
            p.Inlines.Add(run);
            doc.Blocks.Add(p);
        }

        private static void AddRule(FlowDocument doc)
        {
            var p = new Paragraph { Margin = new Thickness(0, 3, 0, 4), BorderThickness = new Thickness(0, 1, 0, 0) };
            p.SetResourceReference(Block.BorderBrushProperty, "CardBorderBrush");
            doc.Blocks.Add(p);
        }

        private static void AddCodeBlock(FlowDocument doc, List<string> lines)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(0, 2, 0, 4),
                Padding = new Thickness(6, 4, 6, 4),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 11.5
            };
            p.SetResourceReference(TextElement.BackgroundProperty, "RowHoverBrush");
            p.SetResourceReference(Block.BorderBrushProperty, "CardBorderBrush");
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                    p.Inlines.Add(new LineBreak());
                p.Inlines.Add(new Run(lines[i]));
            }
            doc.Blocks.Add(p);
        }

        private static void AddSpacer(FlowDocument doc)
        {
            // Leading spacers just push the first line down; skip them.
            if (doc.Blocks.Count == 0)
                return;
            doc.Blocks.Add(new Paragraph
            {
                Margin = new Thickness(0),
                LineHeight = 5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            });
        }

        /// <summary>
        /// Splits the line into markdown-styled runs and citation footnotes.
        /// Citation markers ([SOURCE_3], \u3010SOURCE_3\u3011, [3], bare SOURCE_3)
        /// become small numbered circle buttons that navigate the PDF viewer to
        /// the cited passage when clicked.
        /// </summary>
        private static void AddInlines(InlineCollection inlines, string text, IReadOnlyList<AiSource> sources, AiCitationNumberer numberer)
        {
            int pos = 0;
            foreach (Match m in AiCitations.InlineRx.Matches(text))
            {
                int number = AiCitations.MatchToNumber(m);
                if (number < 0)
                    continue; // degenerate match: keep the raw text instead
                if (m.Index > pos)
                    AddMarkdownInlines(inlines, text.Substring(pos, m.Index - pos));
                // Circle shows the appearance-order display number; the Tag keeps
                // the original SOURCE_n id so the click still finds the evidence.
                inlines.Add(MakeCitation(number, numberer.Register(number), sources));
                pos = m.Index + m.Length;
            }
            if (pos < text.Length)
                AddMarkdownInlines(inlines, text.Substring(pos));
        }

        private static void AddMarkdownInlines(InlineCollection inlines, string text)
        {
            int pos = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > pos)
                    inlines.Add(new Run(text.Substring(pos, m.Index - pos)));

                if (m.Groups["bi"].Success)
                    inlines.Add(new Bold(new Italic(new Run(m.Groups["bi"].Value))));
                else if (m.Groups["b"].Success)
                    inlines.Add(new Bold(new Run(m.Groups["b"].Value)));
                else if (m.Groups["u"].Success)
                    inlines.Add(new Bold(new Run(m.Groups["u"].Value)));
                else if (m.Groups["i"].Success)
                    inlines.Add(new Italic(new Run(m.Groups["i"].Value)));
                else if (m.Groups["em"].Success)
                    inlines.Add(new Italic(new Run(m.Groups["em"].Value)));
                else if (m.Groups["c"].Success)
                    inlines.Add(MakeCodeSpan(m.Groups["c"].Value));
                else if (m.Groups["lt"].Success)
                    inlines.Add(MakeLink(m.Groups["lt"].Value, m.Groups["lu"].Value));

                pos = m.Index + m.Length;
            }
            if (pos < text.Length)
                inlines.Add(new Run(text.Substring(pos)));
        }

        private static Inline MakeCodeSpan(string code)
        {
            var span = new Span(new Run(code))
            {
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 11.5
            };
            // RowHoverBrush is a translucent wash present in every theme, so
            // the code highlight follows theme switches live.
            span.SetResourceReference(TextElement.BackgroundProperty, "RowHoverBrush");
            return span;
        }

        private static Inline MakeLink(string label, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                var link = new Hyperlink(new Run(label)) { NavigateUri = uri, ToolTip = url };
                link.SetResourceReference(TextElement.ForegroundProperty, "PrimaryBrush");
                link.TextDecorations = TextDecorations.Underline;
                return link;
            }
            // Not a navigable URL: keep the raw markdown so nothing is lost.
            return new Run(label + " (" + url + ")");
        }

        // ---------- natural width measuring (bubble shrink-wrap) ----------

        /// <summary>
        /// Measures the document's widest unwrapped line so the RichTextBox can
        /// be capped at it (RichTextBox otherwise always fills the available
        /// width). Approximate by design: measured with the control's live font
        /// via FormattedText, with a few percent of slack applied by the caller.
        /// </summary>
        private static double MeasureNaturalWidth(FlowDocument doc, RichTextBox rtb)
        {
            var family = rtb.FontFamily ?? SystemFonts.MessageFontFamily;
            double size = rtb.FontSize > 0 ? rtb.FontSize : 12.0;

            double max = 0;
            foreach (Block block in doc.Blocks)
            {
                if (block is Paragraph p)
                {
                    double w = MeasureInlines(p.Inlines, family, size, FontWeights.Normal, FontStyles.Normal);
                    w += p.Padding.Left + p.Padding.Right + p.Margin.Left + p.Margin.Right;
                    if (w > max)
                        max = w;
                }
            }
            return max;
        }

        private static double MeasureInlines(InlineCollection inlines, FontFamily family, double size, FontWeight weight, FontStyle style)
        {
            double lineW = 0, maxW = 0;
            foreach (Inline inline in inlines)
            {
                double w;
                switch (inline)
                {
                    case Run r:
                        w = MeasureText(r.Text,
                            r.FontFamily ?? family,
                            r.FontSize > 0 ? r.FontSize : size,
                            r.FontWeight == FontWeights.Normal ? weight : r.FontWeight,
                            r.FontStyle == FontStyles.Normal ? style : r.FontStyle);
                        break;
                    case Bold b:
                        w = MeasureInlines(b.Inlines, family, size, FontWeights.Bold, style);
                        break;
                    case Italic i:
                        w = MeasureInlines(i.Inlines, family, size, weight, FontStyles.Italic);
                        break;
                    case Hyperlink h:
                        w = MeasureInlines(h.Inlines, family, size, weight, style);
                        break;
                    case Span s:
                        w = MeasureInlines(s.Inlines,
                            s.FontFamily ?? family,
                            s.FontSize > 0 ? s.FontSize : size,
                            weight, style);
                        break;
                    case InlineUIContainer c when c.Child is FrameworkElement fe:
                        // Embedded citation footnotes contribute their fixed box.
                        w = (fe.Width > 0 ? fe.Width : 0) + fe.Margin.Left + fe.Margin.Right;
                        break;
                    case LineBreak:
                        // A block measures as its longest line, not the sum.
                        if (lineW > maxW)
                            maxW = lineW;
                        w = 0;
                        lineW = -1; // handled below
                        break;
                    default:
                        w = 0;
                        break;
                }
                if (lineW < 0)
                    lineW = 0; // just after a line break
                else
                    lineW += w;
            }
            return Math.Max(lineW, maxW);
        }

        private static double MeasureText(string text, FontFamily family, double size, FontWeight weight, FontStyle style)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            try
            {
                var tf = new Typeface(family, style, weight, FontStretches.Normal);
                var ft = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    tf,
                    size,
                    Brushes.Black,
                    1.0);
                return ft.Width;
            }
            catch
            {
                // Font metrics unavailable (exotic family, fallback chain):
                // rough estimate is fine, the caller adds slack.
                return text.Length * size * 0.6;
            }
        }

        // ---------- citation footnotes ----------

        /// <summary>
        /// Builds the small numbered circle that replaces an inline citation
        /// marker. Known citations are live buttons; a citation whose SOURCE_n
        /// the model never backed up in 'sources' renders as a visibly muted,
        /// inert circle so the raw marker never leaks into the chat again.
        /// </summary>
        private static Inline MakeCitation(int sourceNumber, int displayNumber, IReadOnlyList<AiSource> sources)
        {
            var source = FindSource(sources, sourceNumber);
            var btn = new Button
            {
                // Footnote face = order of appearance (1, 2, ... through the text);
                // identity = the original SOURCE_n the model cited.
                Content = displayNumber.ToString(CultureInfo.InvariantCulture),
                Tag = AiCitations.FormatId(sourceNumber),
                // Two-digit footnotes ("10" +) need a touch more room than "1".
                Width = displayNumber > 9 ? 22 : 16,
                Focusable = false,
                IsTabStop = false,
                Cursor = Cursors.Hand,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Height = 16,
                Padding = new Thickness(0),
                Margin = new Thickness(3, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Template = CreateCitationTemplate()
            };
            btn.SetValue(AutomationProperties.NameProperty, $"Source {displayNumber}");
            if (source is not null)
                btn.ToolTip = BuildCitationTooltip(source);
            else
                // The model invented this SOURCE_n (nothing backs it): the disabled
                // trigger renders it visibly muted and the click handler would
                // ignore it anyway - this just makes the inertness visible.
                btn.IsEnabled = false;

            return new InlineUIContainer(btn) { BaselineAlignment = BaselineAlignment.TextBottom };
        }

        /// <summary>
        /// Circle skin for citation footnotes. Uses a frozen neutral tint of
        /// TextBrush (same recipe as the bubbles/avatars) instead of the accent
        /// brush: accent overlays swap PrimaryBrush per theme family but not
        /// OnPrimaryBrush, so accent-filled chips could end up with unreadable
        /// text. Foreground is intentionally unset so it inherits the live
        /// TextBrush through the document.
        /// </summary>
        private static ControlTemplate CreateCitationTemplate()
        {
            var template = new ControlTemplate(typeof(Button));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "bd";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty,
                AiBrushHelpers.ThemedTint("TextBrush", 0.14,
                    new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88))));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            // Neutral ring, not CardBorderBrush: that key is a saturated accent in
            // several palettes (teal in Ectoplasm/Cyanotic), which made every circle
            // read as a "green button". MutedTextBrush stays a quiet text-family
            // gray in every theme and follows live theme switches for free.
            border.SetResourceReference(Border.BorderBrushProperty, "MutedTextBrush");

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.75, "bd"));
            template.Triggers.Add(hover);

            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "bd"));
            template.Triggers.Add(pressed);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.38, "bd"));
            template.Triggers.Add(disabled);

            return template;
        }

        /// <summary>Localization lookup wired by MainWindow at startup; the
        /// tooltip previously hardcoded "Page {n}".</summary>
        internal static Func<string, string>? Localizer;

        private static string? BuildCitationTooltip(AiSource source)
        {
            var quote = !string.IsNullOrWhiteSpace(source.Quote) ? source.Quote : source.Reason;
            var pageText = Localizer is not null && source.PageNumber > 0
                ? string.Format(Localizer("Str_AiChatSourceTip"), source.PageNumber)
                : source.PageNumber > 0 ? $"Page {source.PageNumber}" : null;

            if (string.IsNullOrWhiteSpace(quote))
                return pageText;

            quote = quote.Trim();
            if (quote.Length > 180)
                quote = quote.Substring(0, 180) + "\u2026";
            return pageText is not null ? $"{pageText}\n{quote}" : quote;
        }

        private static AiSource? FindSource(IReadOnlyList<AiSource> sources, int number)
        {
            if (number < 0)
                return null;
            var id = AiCitations.FormatId(number);
            for (int i = 0; i < sources.Count; i++)
            {
                if (string.Equals(sources[i].SourceId, id, StringComparison.OrdinalIgnoreCase))
                    return sources[i];
            }
            return null;
        }
    }
}
