using System.Text;
using System.Text.RegularExpressions;

namespace StrideShaderLanguageServer.Services;

/// <summary>
/// Scans shader source code to extract documentation comments.
/// Since Stride's parser strips comments during tokenization, we need a separate
/// scanner to capture /// and // comments and associate them with code elements.
/// </summary>
public class CommentScanner
{
    /// <summary>
    /// Scan source for documentation comments and associate them with the line
    /// of code that follows. Returns a dictionary mapping line numbers (1-indexed)
    /// to their associated documentation.
    /// </summary>
    /// <param name="source">The shader source code</param>
    /// <returns>Dictionary of line number -> documentation text</returns>
    public Dictionary<int, string> ScanDocComments(string source)
    {
        var comments = new Dictionary<int, string>();
        var lines = source.Split('\n');
        var docBuffer = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed.StartsWith("///"))
            {
                // XML-style doc comment - accumulate
                var commentText = trimmed.Substring(3).Trim();

                // Strip XML tags for cleaner display
                commentText = StripXmlTags(commentText);

                if (!string.IsNullOrEmpty(commentText))
                    docBuffer.AppendLine(commentText);
            }
            else if (trimmed.StartsWith("//") && !trimmed.StartsWith("///"))
            {
                // Regular comment - could be description
                var commentText = trimmed.Substring(2).Trim();

                // Skip obvious non-doc comments (section markers, TODOs, etc.)
                if (!IsNonDocComment(commentText) && !string.IsNullOrEmpty(commentText))
                    docBuffer.AppendLine(commentText);
            }
            else if (!string.IsNullOrWhiteSpace(trimmed))
            {
                // Non-empty, non-comment line - associate accumulated comments with this line
                if (docBuffer.Length > 0)
                {
                    // Line numbers are 1-indexed in Stride's SourceSpan
                    comments[i + 1] = docBuffer.ToString().Trim();
                    docBuffer.Clear();
                }
            }
            // Blank lines between comments are preserved in the buffer
        }

        return comments;
    }

    /// <summary>
    /// Get documentation for a specific line number.
    /// </summary>
    public string? GetDocumentation(Dictionary<int, string> comments, int lineNumber)
    {
        return comments.TryGetValue(lineNumber, out var doc) ? doc : null;
    }

    /// <summary>
    /// Strip common XML documentation tags for cleaner display.
    /// </summary>
    private static string StripXmlTags(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Remove common XML tags: <summary>, </summary>, <param>, <returns>, etc.
        text = Regex.Replace(text, @"<summary>|</summary>|<remarks>|</remarks>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<param\s+name=""[^""]*"">", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</param>|<returns>|</returns>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<see\s+cref=""([^""]*)""\s*/>", "$1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", ""); // Remove any remaining tags

        return text.Trim();
    }

    /// <summary>
    /// Check if a comment looks like a non-documentation comment.
    /// </summary>
    private static bool IsNonDocComment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var lower = text.ToLowerInvariant();

        // Skip section markers
        if (text.StartsWith("---") || text.StartsWith("===") || text.StartsWith("***"))
            return true;

        // Skip TODOs, FIXMEs, HACKs
        if (lower.StartsWith("todo") || lower.StartsWith("fixme") || lower.StartsWith("hack") ||
            lower.StartsWith("note:") || lower.StartsWith("warning:"))
            return true;

        // Skip region markers
        if (lower.StartsWith("#region") || lower.StartsWith("#endregion"))
            return true;

        // Skip copyright/license headers
        if (lower.Contains("copyright") || lower.Contains("license") || lower.Contains("all rights reserved"))
            return true;

        return false;
    }
}
