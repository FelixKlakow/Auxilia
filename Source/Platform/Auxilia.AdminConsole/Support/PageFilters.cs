using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace Auxilia.AdminConsole.Support;

/// <summary>
/// Reads and writes a page's filter state as query-string parameters, so what an operator is
/// looking at is a shareable, bookmarkable, refresh-surviving URL (e.g.
/// <c>/events?sourceRun=…</c> from a run's event strip). Values are read once on initialize and
/// pushed back with <see cref="Apply"/> without adding history entries.
/// </summary>
public static class PageFilters
{
    /// <summary>The parameter's value, or "" when absent — filters are always nullable strings.</summary>
    public static string Read(NavigationManager navigation, string key)
        => QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query).TryGetValue(key, out var values)
            ? values.FirstOrDefault() ?? ""
            : "";

    public static DateTime? ReadDate(NavigationManager navigation, string key)
        => DateTime.TryParse(Read(navigation, key), out var parsed) ? parsed : null;

    public static Guid? ReadGuid(NavigationManager navigation, string key)
        => Guid.TryParse(Read(navigation, key), out var parsed) ? parsed : null;

    /// <summary>
    /// Replaces the URL with the non-empty subset of <paramref name="values"/>. Uses replace so a
    /// filter change is not a back-button step, and never forces a reload — the page has already
    /// applied the filter itself.
    /// </summary>
    public static void Apply(NavigationManager navigation, IReadOnlyDictionary<string, string?> values)
    {
        var kept = values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);

        var path = new Uri(navigation.Uri).GetLeftPart(UriPartial.Path);
        var target = kept.Count == 0 ? path : QueryHelpers.AddQueryString(path, kept!);
        if (!string.Equals(target, navigation.Uri, StringComparison.Ordinal))
            navigation.NavigateTo(target, forceLoad: false, replace: true);
    }
}
