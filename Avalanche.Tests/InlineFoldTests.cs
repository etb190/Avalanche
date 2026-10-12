// Avalanche.Tests/InlineFoldTests.cs - the soft-break fold's law, pinned.
//
// The digest's models hard-wrap their markdown, and a **bold** straddling
// the wrap used to render both its marker pairs as literal asterisks: a
// bullet carried its opener alone, the closer stranded on the next line,
// and neither paragraph could see the other's half. The fold reunites the
// split span before the parser ever sees it - and these facts pin where it
// may and may not reach.

namespace Avalanche.Tests
{
    using Avalanche.Features.AI;
    using Xunit;

    public class InlineFoldTests
    {
        [Fact]
        public void Bullet_with_wrapped_bold_folds_the_continuation_in()
        {
            string[] lines =
            {
                "- **Canon 2 (norma normata, \"normed",
                "norm\"):** Designates a fixed, bounded catalogue.",
            };

            string[] folded = InlineFold.Fold(lines);

            Assert.Single(folded);
            Assert.Equal(
                "- **Canon 2 (norma normata, \"normed norm\"):** Designates a fixed, bounded catalogue.",
                folded[0]);
        }

        [Fact]
        public void Plain_prose_with_wrapped_bold_folds_too()
        {
            string[] lines =
            {
                "Intro **Canon 2 (norma",
                "normata):** rest of the sentence.",
            };

            string[] folded = InlineFold.Fold(lines);

            Assert.Single(folded);
            Assert.Equal("Intro **Canon 2 (norma normata):** rest of the sentence.", folded[0]);
        }

        [Fact]
        public void Balanced_bullet_is_left_alone()
        {
            string[] lines =
            {
                "- **A:** B",
                "plain prose beneath the list",
            };

            string[] folded = InlineFold.Fold(lines);

            Assert.Equal(2, folded.Length);
            Assert.Equal(lines[0], folded[0]);
            Assert.Equal(lines[1], folded[1]);
        }

        [Fact]
        public void Blank_line_stops_the_fold()
        {
            string[] lines =
            {
                "- **Canon 2:",
                string.Empty,
                "more prose",
            };

            Assert.Equal(3, InlineFold.Fold(lines).Length);
        }

        [Fact]
        public void Second_bullet_stops_the_fold()
        {
            string[] lines =
            {
                "- **first item:",
                "- second item",
            };

            Assert.Equal(2, InlineFold.Fold(lines).Length);
        }

        [Fact]
        public void Heading_stops_the_fold()
        {
            string[] lines =
            {
                "- **first item:",
                "## A heading",
            };

            Assert.Equal(2, InlineFold.Fold(lines).Length);
        }

        [Fact]
        public void Fenced_code_is_never_touched()
        {
            string[] lines =
            {
                "```",
                "x ** y",
                "z ** w",
                "```",
            };

            Assert.Equal(lines, InlineFold.Fold(lines));
        }

        [Fact]
        public void Cjk_boundaries_abut_without_a_space()
        {
            string[] lines =
            {
                "**\u7528\u8a9e",
                "\u306e\u5b9a\u7fa9:** \u672c\u6587",
            };

            string[] folded = InlineFold.Fold(lines);

            Assert.Single(folded);
            Assert.Equal("**\u7528\u8a9e\u306e\u5b9a\u7fa9:** \u672c\u6587", folded[0]);
        }

        [Fact]
        public void Multiple_continuation_lines_fold_until_balanced()
        {
            string[] lines =
            {
                "- **a",
                "b",
                "c** d",
            };

            string[] folded = InlineFold.Fold(lines);

            Assert.Single(folded);
            Assert.Equal("- **a b c** d", folded[0]);
        }
    }
}
