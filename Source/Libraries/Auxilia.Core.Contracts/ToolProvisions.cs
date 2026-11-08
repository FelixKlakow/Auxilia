namespace Auxilia.Core.Contracts;

/// <summary>
/// The tool vocabulary shared by a workflow image's provided tools (<c>ProvidesTools</c>), a slot
/// provider's required tools, and a run's required tools: <c>name</c> or <c>name@version</c>, an
/// open vocabulary. A requirement without a version is met by the tool in any version (or none);
/// one with a version only by a provision of exactly that version — a run that needs a specific
/// runtime never starts on another one.
/// </summary>
public static class ToolProvisions
{
    /// <summary>Splits <c>name@version</c>; the version is null for a bare name.</summary>
    public static (string Name, string? Version) Parse(string tool)
    {
        if (!TryParse(tool, out var name, out var version))
            throw new ArgumentException($"'{tool}' is not a tool (name or name@version).", nameof(tool));
        return (name, version);
    }

    /// <summary>False for anything but a non-empty name with an optional non-empty version.</summary>
    public static bool TryParse(string? tool, out string name, out string? version)
    {
        name = string.Empty;
        version = null;
        if (string.IsNullOrWhiteSpace(tool) || tool.Any(char.IsWhiteSpace))
            return false;
        var at = tool.IndexOf('@');
        if (at < 0)
        {
            name = tool;
            return true;
        }
        if (at == 0 || at == tool.Length - 1 || tool.IndexOf('@', at + 1) >= 0)
            return false;
        name = tool[..at];
        version = tool[(at + 1)..];
        return true;
    }

    /// <summary>True when <paramref name="provided"/> meets <paramref name="required"/>.</summary>
    public static bool IsMet(string required, IEnumerable<string> provided)
    {
        if (!TryParse(required, out var name, out var version))
            return false;
        foreach (var candidate in provided)
        {
            if (!TryParse(candidate, out var providedName, out var providedVersion)
                || !string.Equals(providedName, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (version is null || string.Equals(providedVersion, version, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>The requirements <paramref name="provided"/> does not meet, in their order.</summary>
    public static IReadOnlyList<string> Unmet(IEnumerable<string> required, IEnumerable<string> provided)
    {
        var provisions = provided as IReadOnlyCollection<string> ?? provided.ToList();
        return required.Where(r => !IsMet(r, provisions)).ToList();
    }
}
