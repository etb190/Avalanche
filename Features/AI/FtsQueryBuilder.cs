using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// Builds FTS5 MATCH queries from natural-language questions.
    /// The retriever ORs the remaining terms so evidence containing any of them
    /// becomes a candidate, and BM25 ranks chunks matching more / rarer terms
    /// higher. Stopwords are stripped first: a query like "Why does he think
    /// that?" previously ORed every token, so rows matching only "does" or
    /// "that" competed with real evidence and good hits were dropped after
    /// normalization. Returns null when nothing searchable remains.
    /// </summary>
    public static class FtsQueryBuilder
    {
        private const int MaxTerms = 24;

        // Compact English function-word list. FTS5 indexes with the porter
        // stemmer, so only true stopwords belong here; content words must pass.
        private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
        {
            "a","an","the","and","or","but","if","then","than","so","because","as",
            "of","to","in","on","at","by","for","with","from","about","into","over",
            "after","before","between","out","off","up","down","again","further",
            "once","here","there","all","any","both","each","few","more","most",
            "other","some","such","no","nor","not","only","own","same","too","very",
            "can","cannot","could","should","shall","will","would","may","might",
            "must","do","does","did","doing","done","have","has","had","having",
            "be","is","are","was","were","been","being","am","it","its","this",
            "that","these","those","i","me","my","we","our","you","your","he",
            "him","his","she","her","they","them","their","what","which","who",
            "whom","whose","when","where","why","how"
        };

        public static string? Build(string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;

            var terms = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Every non-alphanumeric character is a token boundary: apostrophes
            // ("don't" -> "don","t"), hyphens ("well-known" -> "well","known")
            // and FTS5 syntax characters all split, matching how the unicode61
            // tokenizer segmented the indexed chunk text. Operators can never
            // leak through.
            var sb = new StringBuilder();
            void FlushTerm()
            {
                var term = sb.ToString();
                sb.Clear();
                if (term.Length < 2 || Stopwords.Contains(term) || !seen.Add(term))
                    return;
                terms.Add(term);
            }

            foreach (var ch in query.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (terms.Count < MaxTerms) sb.Append(ch);
                }
                else if (sb.Length > 0)
                    FlushTerm();
            }
            if (sb.Length > 0) FlushTerm();

            if (terms.Count == 0) return null;

            // Quote-wrap every term: FTS5 syntax characters in user text can
            // never leak through, and terms stay literal tokens.
            return string.Join(" OR ", terms.Select(t => "\"" + t + "\""));
        }
    }
}
