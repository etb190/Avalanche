using System;
using System.Text;

namespace Avalanche.Features.AI
{
    /// <summary>
    /// The rich text editor sidechat logic (v1.19.83): session keys, context framing,
    /// and document-grounded system prompt. Kept free of WPF so it is pure and auditable.
    /// </summary>
    public static class EditorChat
    {
        public const string SessionPrefix = "ed_";

        public static bool IsEditorSessionKey(string key)
            => !string.IsNullOrEmpty(key) && key.StartsWith(SessionPrefix, StringComparison.Ordinal);

        public static string SessionKey(string tabId)
            => string.IsNullOrEmpty(tabId) ? "" :
               tabId.StartsWith(SessionPrefix, StringComparison.Ordinal) ? tabId : SessionPrefix + tabId;

        public static string BuildSystemPrompt(string title, string text)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an AI writing, editing, and research assistant embedded in Avalanche's rich text editor.");
            sb.AppendLine("The user is currently writing and editing a document. The live document content is provided below.");
            sb.AppendLine();
            sb.AppendLine($"DOCUMENT TITLE: {(string.IsNullOrWhiteSpace(title) ? "Untitled Document" : title)}");
            sb.AppendLine("DOCUMENT CONTENT:");
            sb.AppendLine(string.IsNullOrWhiteSpace(text) ? "(The document is currently empty.)" : text);
            sb.AppendLine();
            sb.AppendLine("GUIDELINES:");
            sb.AppendLine("- Answer questions about the document, brainstorm, critique prose, rewrite passages, summarize, or draft new sections.");
            sb.AppendLine("- When asked to generate, expand, or rewrite text, output the actual prose directly in clean Markdown.");
            sb.AppendLine("- NEVER wrap your answer in JSON wrappers like {\"rewritten_text\": ...} unless the user explicitly requested a JSON format.");
            sb.AppendLine("- When citing or referring to parts of the document, quote them directly.");
            sb.AppendLine("- Be concise, clear, and natural.");
            return sb.ToString();
        }
    }
}
