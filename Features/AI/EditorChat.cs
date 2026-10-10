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
            // v1.19.89: the instruction set rides the prompt workshop -
            // the DOCUMENT framing stays hardcoded after it.
            sb.AppendLine(AiPromptLibrary.EditorSidechatHead());
            sb.AppendLine();
            sb.AppendLine($"DOCUMENT TITLE: {(string.IsNullOrWhiteSpace(title) ? "Untitled Document" : title)}");
            sb.AppendLine("DOCUMENT CONTENT:");
            sb.AppendLine(string.IsNullOrWhiteSpace(text) ? "(The document is currently empty.)" : text);
            return sb.ToString();
        }
    }
}
