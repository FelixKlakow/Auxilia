namespace Auxilia.Workflows.Mcp;

public static class SlotMcpPrefix
{
    /// <summary>Produces the prefixed tool name <c>{slotName}/{toolName}</c>.</summary>
    /// <exception cref="ArgumentException">
    ///   Thrown when <paramref name="slotName"/> or <paramref name="toolName"/> is null or whitespace,
    ///   or when <paramref name="toolName"/> contains a <c>/</c> character.
    /// </exception>
    public static string Format(string slotName, string toolName)
    {
        if (string.IsNullOrWhiteSpace(slotName))
            throw new ArgumentException("Slot name must not be null or whitespace.", nameof(slotName));
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("Tool name must not be null or whitespace.", nameof(toolName));
        if (toolName.Contains('/'))
            throw new ArgumentException("Tool name must not contain '/' (prefix injection is not allowed).", nameof(toolName));

        return $"{slotName}/{toolName}";
    }
}
