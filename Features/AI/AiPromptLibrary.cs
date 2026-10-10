// Features/AI/AiPromptLibrary.cs - the one door every feature knocks on (v1.19.90).
//
// No prompt text lives here any more. Every voice rides the prompt deck:
// the store's row first (the reader's saved wording), the shipped deck's
// factory body when the row is untouched or gone. The features ask through
// these accessors and never see where the words came from.

namespace Avalanche.Features.AI
{
    internal static class AiPromptLibrary
    {
        // -- PDF/book side chat: the instruction head. The evidence framing
        // (EVIDENCE FORMAT / RETRIEVED EVIDENCE / the JSON contract) stays
        // wired after it - the citation resolver parses that dialect.
        // v1.19.94: the deck's sidechat bodies carry the CRITICAL CITATION
        // MANDATE - inline [SOURCE_n] immediately after the claim, never
        // clustered at the end - so every head riding through here enforces
        // immediate placement in the reader's own dialect.
        public static string SidechatHead() =>
            Features.Summary.PromptStore.FirstBody(Features.Summary.PromptStore.CatSidechat) ?? "";

        // -- Browser side chat: the instruction head; the PAGE framing and
        // the numbered page segments ride after it.
        public static string WebSidechatHead() =>
            Features.Summary.PromptStore.FirstBody(Features.Summary.PromptStore.CatWebSidechat) ?? "";

        // -- Editor side chat: the whole instruction set; the DOCUMENT
        // title/content framing rides after it.
        public static string EditorSidechatHead() =>
            Features.Summary.PromptStore.FirstBody(Features.Summary.PromptStore.CatEditorSidechat) ?? "";

        // -- Recap: one short paragraph, the reader's language. A body may
        // carry the {language} placeholder; it is fed the run's language here.
        public static string Condense(string language)
        {
            string template = Features.Summary.PromptStore.FirstBody(Features.Summary.PromptStore.CatRecap)
                ?? "Respond in {language} only.";
            return template.Replace("{language}", language);
        }

        // -- Notes: the review-card generator's whole contract.
        public static string Notes() =>
            Features.Summary.PromptStore.FirstBody(Features.Summary.PromptStore.CatNotes) ?? "";

        // -- AI tester: the verification probe's whole contract.
        public static string Tester() =>
            Features.Summary.PromptStore.FirstBody(Features.Summary.PromptStore.CatTester) ?? "";

        // -- Grammar: the copyeditor's scan. A body may carry the {ignored}
        // placeholder; the reader's own ignored list rides in either way.
        public static string Grammar(string ignoredList)
        {
            string template = Features.Summary.PromptStore.FirstBody(Features.Summary.PromptStore.CatGrammar)
                ?? "DO NOT flag words in this ignored list: [{ignored}].";
            return template.Replace("{ignored}", ignoredList);
        }

        // -- Rewrite: the seven styles, each addressed as rewrite_<style> in
        // the deck, the humanize voice answering for anything unknown.
        public static string Rewrite(string style)
        {
            string clean = (style ?? "").Trim().ToLowerInvariant();
            if (clean.Length == 0) clean = "humanize";
            return Features.Summary.PromptStore.StoredMandate("rewrite_" + clean)
                ?? Features.Summary.PromptStore.DefaultBody("rewrite_humanize")
                ?? "";
        }

        // -- The workshop's editor: a row's own body when it has one, or its
        // shipped deck body when the row is still untouched. The placeholders
        // ({words}, {language}, {ignored}) show as themselves so the reader
        // can see where the run's values land.
        public static string EditorBodyFor(Features.Summary.AiPromptDef p)
        {
            if (!string.IsNullOrWhiteSpace(p.Body)) return p.Body;
            string? body = Features.Summary.PromptStore.DefaultBody(p.Id);
            if (body is null && p.Id.StartsWith("web_", StringComparison.Ordinal))
                body = Features.Summary.PromptStore.DefaultBody(p.Id[4..]);
            return body ?? "";
        }
    }
}
