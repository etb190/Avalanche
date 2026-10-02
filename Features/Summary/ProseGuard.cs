// Features/Summary/ProseGuard.cs — the deterministic prose guarantee for page digests.
//
// Prompt rules ("no bullet points - a total failure") are necessary but not sufficient:
// a stubborn or reasoning-heavy model can still answer with a bullet list, and older
// builds even dumped hidden reasoning (bullet-shaped thinking notes) onto the screen
// when a model produced no visible answer. The digest style is prose paragraphs under
// the book's own headings - so the guard is mechanical and model-independent:
//
//  * LooksLikeBulletList classifies a finished answer as a bullet dump (enough lines
//    starting with -, *, +, •, ·, –, — or "N."/"N)" markers).
//  * ConvertBulletsToProse flattens such an answer: markers stripped, each run of
//    bullet lines fused into one flowing paragraph (missing terminal punctuation
//    repaired, sentence starts capitalized, continuation clauses joined with a
//    comma so the sentence flows instead of stacking staccato fragments),
//    markdown headings and ordinary prose preserved as-is.
//
// Deliberately dependency-free (no logging, no WPF, no I/O) so the test project
// compiles it directly - see SummaryProseGuardTests.

namespace Avalanche.Features.Summary
{
    using System.Text;

    internal static class ProseGuard
    {
        /// <summary>Fraction of non-empty lines that must be bullet-marked before an
        /// answer counts as a bullet dump. 30% sits far above any real prose digest
        /// (a stray dash or two) and far below any actual list (which runs ~100%).</summary>
        private const double DominantRatio = 0.30;

        /// <summary>Absolute floor of bullet lines before classification fires at all.</summary>
        private const int MinBulletLines = 3;

        /// <summary>True when the text reads as a bullet dump: at least
        /// <see cref="MinBulletLines"/> bullet-marked lines and at least
        /// <see cref="DominantRatio"/> of all non-empty lines marked.</summary>
        public static bool LooksLikeBulletList(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            int nonEmpty = 0;
            int bullets = 0;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                nonEmpty++;
                if (IsBulletLine(line))
                {
                    bullets++;
                }
            }

            return bullets >= MinBulletLines && nonEmpty > 0 &&
                   bullets >= nonEmpty * DominantRatio;
        }

        /// <summary>True for "- item", "* item", "+ item", "• item", "· item",
        /// "– item", "— item", "7. item" and "7) item" (indent and quote prefixes
        /// tolerated). Deliberately false for "---" rules, "**bold**" openers and
        /// negative numbers like "-3 shekels".</summary>
        public static bool IsBulletLine(string line)
        {
            int i = SkipPrefixes(line);
            if (i >= line.Length)
            {
                return false;
            }

            char first = line[i];
            if (first is '-' or '*' or '+' or '•' or '·' or '–' or '—')
            {
                // The next character must be whitespace (or line end) so horizontal
                // rules ("---"), bold openers ("**x") and signed numbers don't count.
                return i + 1 >= line.Length || line[i + 1] == ' ' || line[i + 1] == '\t';
            }

            if (char.IsDigit(first))
            {
                int j = i;
                while (j < line.Length && char.IsDigit(line[j]))
                {
                    j++;
                }

                return j + 1 < line.Length &&
                       (line[j] == '.' || line[j] == ')') &&
                       (line[j + 1] == ' ' || line[j + 1] == '\t');
            }

            return false;
        }

        /// <summary>Flattens a bullet dump into flowing prose: each run of bullet lines
        /// becomes one paragraph (fragments joined with repaired punctuation), wrapped
        /// prose lines merge into their paragraph, markdown headings and blank-line
        /// paragraph breaks survive. Prose already free of bullets passes through
        /// structurally unchanged.</summary>
        public static string ConvertBulletsToProse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text ?? string.Empty;
            }

            var blocks = new List<string>();
            var fragments = new List<string>();   // bullet items of the current paragraph
            var plain = new List<string>();       // consecutive wrapped prose lines

            void FlushBullets()
            {
                if (fragments.Count == 0)
                {
                    return;
                }

                var sentence = new StringBuilder();
                foreach (string raw in fragments)
                {
                    string fragment = raw.Trim();
                    if (fragment.Length == 0)
                    {
                        continue;
                    }

                    if (sentence.Length == 0)
                    {
                        sentence.Append(CapitalizeFirst(fragment));
                        continue;
                    }

                    char last = sentence[sentence.Length - 1];
                    if (last is '.' or '!' or '?')
                    {
                        sentence.Append(' ').Append(CapitalizeFirst(fragment));
                    }
                    else if (last is ':' or ';' or ',')
                    {
                        sentence.Append(' ').Append(fragment);
                    }
                    else if (StartsContinuation(fragment))
                    {
                        // "because silver never ran short" continues the previous
                        // clause - a comma keeps the sentence flowing where the old
                        // build slammed a period down and capitalized the fragment.
                        sentence.Append(", ").Append(fragment);
                    }
                    else
                    {
                        sentence.Append(". ").Append(CapitalizeFirst(fragment));
                    }
                }

                if (sentence.Length > 0)
                {
                    blocks.Add(sentence.ToString());
                }

                fragments.Clear();
            }

            void FlushPlain()
            {
                if (plain.Count == 0)
                {
                    return;
                }

                blocks.Add(string.Join(" ", plain));
                plain.Clear();
            }

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0)
                {
                    FlushBullets();
                    FlushPlain();
                    continue;
                }

                if (IsBulletLine(line))
                {
                    FlushPlain();
                    fragments.Add(StripMarker(line));
                    continue;
                }

                FlushBullets();
                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    blocks.Add(line);
                    continue;
                }

                plain.Add(line);
            }

            FlushBullets();
            FlushPlain();
            return string.Join("\n\n", blocks);
        }

        // Continuation openers: a lowercase fragment starting with one of these is
        // a CONTINUING clause, not a new sentence - joined with a comma it turns a
        // flattened bullet stack into one flowing sentence. Uppercase starts are
        // read as the sentence breaks the model wrote and keep the period join.
        private static readonly HashSet<string> ContinuationOpeners = new(StringComparer.Ordinal)
        {
            "and", "but", "or", "nor", "so", "yet",
            "because", "since", "while", "whereas", "although", "though",
            "which", "who", "whom", "whose", "where", "until", "unless",
            "besides", "moreover", "furthermore", "meanwhile", "thus", "hence",
            "then", "also", "plus", "with", "without"
        };

        private static bool StartsContinuation(string fragment)
        {
            if (fragment.Length == 0 || !char.IsLower(fragment[0]))
            {
                return false;
            }

            int i = 1;
            while (i < fragment.Length && char.IsLetter(fragment[i]))
            {
                i++;
            }

            return ContinuationOpeners.Contains(fragment[..i]);
        }

        private static int SkipPrefixes(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t' || line[i] == '>'))
            {
                i++;
            }

            return i;
        }

        private static string StripMarker(string line)
        {
            int i = SkipPrefixes(line);
            if (i < line.Length && char.IsDigit(line[i]))
            {
                int j = i;
                while (j < line.Length && char.IsDigit(line[j]))
                {
                    j++;
                }

                if (j < line.Length && (line[j] == '.' || line[j] == ')'))
                {
                    i = j + 1;
                }
            }
            else if (i < line.Length)
            {
                i++;   // the single marker character (- * + • · – —)
            }

            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            {
                i++;
            }

            return line[i..];
        }

        private static string CapitalizeFirst(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsLetter(s[i]))
                {
                    return s[..i] + char.ToUpperInvariant(s[i]) + s[(i + 1)..];
                }
            }

            return s;
        }
    }
}
