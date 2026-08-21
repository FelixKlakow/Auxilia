using System.Globalization;

namespace Auxilia.Workflows;

/// <summary>
/// Rendering kinds of a <see cref="WorkflowInputDescriptor"/> — the SAME wire vocabulary the
/// Core contracts expose to editors (<c>InputKinds</c> there); an unknown kind renders as
/// <see cref="Text"/>, so old editors stay usable with newer schemas.
/// </summary>
public static class WorkflowInputKinds
{
    public const string Text = "Text";
    public const string Multiline = "Multiline";
    public const string Boolean = "Boolean";
    /// <summary>One of <see cref="WorkflowInputDescriptor.Choices"/>.</summary>
    public const string Choice = "Choice";
    public const string Number = "Number";
}

/// <summary>
/// The typed accessor for the run's DECLARED inputs, resolved from the dispatch context the
/// platform injected. Names are validated against the declarations (a typo throws instead of
/// silently reading nothing), declared defaults apply, and values are kind-checked — a
/// present-but-unparseable value fails the run visibly rather than running with wrong
/// parameters. Undeclared context keys stay reachable via <see cref="GetRaw"/>.
/// </summary>
public interface IRunInputs
{
    /// <summary>The declared input's value — context value, else its declared default, else null.</summary>
    string? Get(string name);

    /// <summary>Like <see cref="Get"/> but throws when no value was delivered and no default exists.</summary>
    string Require(string name);

    bool GetBoolean(string name, bool fallback = false);

    int GetInt32(string name, int fallback = 0);

    double GetNumber(string name, double fallback = 0);

    /// <summary>Raw dispatch-context value of ANY key, declared or not — the escape hatch.</summary>
    string? GetRaw(string key);
}

/// <summary>Environment-backed <see cref="IRunInputs"/> over <c>WORKFLOW_CONTEXT__*</c>.</summary>
internal sealed class EnvironmentRunInputs(
    IReadOnlyList<WorkflowInputDescriptor> declared,
    Func<string, string?>? environment = null) : IRunInputs
{
    private readonly Func<string, string?> _environment =
        environment ?? System.Environment.GetEnvironmentVariable;

    public string? Get(string name)
    {
        var descriptor = declared.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"'{name}' is not a declared input of this workflow "
                + $"(declared: {string.Join(", ", declared.Select(i => i.Name).Order(StringComparer.Ordinal))})",
                nameof(name));
        var value = GetRaw(name) ?? descriptor.DefaultValue;
        if (value is not null
            && descriptor.Kind == WorkflowInputKinds.Choice
            && descriptor.Choices is { Count: > 0 } choices
            && !choices.Contains(value, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"input '{name}' must be one of [{string.Join(", ", choices)}], not '{value}'");
        return value;
    }

    public string Require(string name)
        => Get(name) ?? throw new InvalidOperationException(
            $"required input '{name}' was not delivered and declares no default");

    public bool GetBoolean(string name, bool fallback = false)
    {
        if (Get(name) is not { } value)
            return fallback;
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"input '{name}' must be true/false, not '{value}'");
    }

    public int GetInt32(string name, int fallback = 0)
    {
        if (Get(name) is not { } value)
            return fallback;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"input '{name}' must be an integer, not '{value}'");
    }

    public double GetNumber(string name, double fallback = 0)
    {
        if (Get(name) is not { } value)
            return fallback;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"input '{name}' must be a number, not '{value}'");
    }

    public string? GetRaw(string key)
        => _environment($"WORKFLOW_CONTEXT__{key.ToUpperInvariant()}");
}
