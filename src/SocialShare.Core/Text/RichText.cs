using System.Text;
using System.Text.RegularExpressions;

namespace SocialShare.Core.Text;

/// <summary>A span of the post body that should render as a link or a mention.</summary>
public sealed record TextSpan(int ByteStart, int ByteEnd, string Text, TextSpanKind Kind);

public enum TextSpanKind
{
    Link = 0,
    Mention = 1
}

/// <summary>
/// Finds links and mentions in a body and reports them with UTF-8 byte offsets, which is what
/// the AT Protocol facet format wants. Doing it here keeps it server side and unit testable.
/// Docs: https://docs.bsky.app/docs/advanced-guides/post-richtext
/// </summary>
public static partial class RichText
{
    [GeneratedRegex(@"(?<=^|\s|\()(https?://[^\s\)\]]*[^\s\.,;:!\?\)\]'""])", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    // Handles look like @name.example.com. The trailing part must contain a dot so plain
    // @words are left alone.
    [GeneratedRegex(@"(?<=^|\s|\()@((?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,})")]
    private static partial Regex MentionPattern();

    public static IReadOnlyList<TextSpan> FindSpans(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return [];
        }

        var spans = new List<TextSpan>();

        foreach (Match m in LinkPattern().Matches(body))
        {
            var (start, end) = ByteRange(body, m.Index, m.Length);
            spans.Add(new TextSpan(start, end, m.Value, TextSpanKind.Link));
        }

        foreach (Match m in MentionPattern().Matches(body))
        {
            var (start, end) = ByteRange(body, m.Index, m.Length);

            // Skip anything already covered by a link, for example a URL containing an at sign.
            if (spans.Any(s => start < s.ByteEnd && end > s.ByteStart))
            {
                continue;
            }

            spans.Add(new TextSpan(start, end, m.Groups[1].Value, TextSpanKind.Mention));
        }

        return spans.OrderBy(s => s.ByteStart).ToList();
    }

    private static (int Start, int End) ByteRange(string body, int charIndex, int charLength)
    {
        var start = Encoding.UTF8.GetByteCount(body.AsSpan(0, charIndex));
        var length = Encoding.UTF8.GetByteCount(body.AsSpan(charIndex, charLength));
        return (start, start + length);
    }

    /// <summary>
    /// Counts a body the way the platforms do. All six count Unicode characters rather than
    /// UTF-16 code units, so a single emoji or an accented letter counts once.
    /// </summary>
    public static int CountCharacters(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return 0;
        }

        var count = 0;
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(body);
        while (e.MoveNext())
        {
            count++;
        }

        return count;
    }
}
