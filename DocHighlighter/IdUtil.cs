using System;

namespace DocHighlighter;

internal static class IdUtil
{
    /// <summary>Canonical form for unit ids coming from the public API and from Studio Pro internals.</summary>
    public static string? Normalize(object? id)
    {
        if (id is null) return null;
        if (id is Guid guid) return guid.ToString("D");
        var text = id.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Guid.TryParse(text, out guid) ? guid.ToString("D") : text.Trim().ToLowerInvariant();
    }
}
