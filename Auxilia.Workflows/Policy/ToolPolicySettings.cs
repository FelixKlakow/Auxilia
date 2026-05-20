namespace Auxilia.Workflows.Policy;

/// <summary>
/// Builds an <see cref="IToolPolicy"/> from a <c>SlotConfiguration.Settings</c> dictionary.
/// </summary>
public static class ToolPolicySettings
{
    /// <summary>
    /// Builds an <see cref="IToolPolicy"/> from the given settings dictionary.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>"allow"</c> → the operation is permitted.</item>
    ///   <item><c>"deny"</c> → the operation is denied.</item>
    ///   <item>Absent key → default-deny.</item>
    ///   <item>Any other value → <see cref="InvalidOperationException"/> at build time (fail-fast).</item>
    /// </list>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any value in <paramref name="settings"/> is not <c>"allow"</c> or <c>"deny"</c>.
    /// </exception>
    public static IToolPolicy Build(IReadOnlyDictionary<string, string> settings)
    {
        foreach (var (key, value) in settings)
        {
            if (value is not ("allow" or "deny"))
                throw new InvalidOperationException(
                    $"Invalid tool policy value '{value}' for key '{key}'. Expected 'allow' or 'deny'.");
        }

        return new DictionaryToolPolicy(settings);
    }

    private sealed class DictionaryToolPolicy : IToolPolicy
    {
        private readonly IReadOnlyDictionary<string, string> _settings;

        public DictionaryToolPolicy(IReadOnlyDictionary<string, string> settings)
            => _settings = settings;

        public bool IsAllowed(string capabilityOperation)
            => _settings.TryGetValue(capabilityOperation, out var value) && value == "allow";
    }
}
