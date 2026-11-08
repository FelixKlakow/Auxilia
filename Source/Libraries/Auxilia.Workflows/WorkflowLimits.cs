using System.Globalization;

namespace Auxilia.Workflows;

/// <summary>Limits the platform sets on every run, read from the launch environment.</summary>
public static class WorkflowLimits
{
    /// <summary>The idle cap when the platform set none (a run outside a runner).</summary>
    public static readonly TimeSpan DefaultMaxIdle = TimeSpan.FromMinutes(60);

    /// <summary>
    /// How long a run may wait for its operator at most (<see cref="WorkflowEnvironmentVariables.MaxIdleMinutes"/>):
    /// a session's own idle timeout never exceeds it, and a session without one uses it — no run
    /// holds its container forever.
    /// </summary>
    public static TimeSpan MaxIdle
        => ParseMinutes(System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.MaxIdleMinutes))
           ?? DefaultMaxIdle;

    /// <summary>
    /// A duration given in minutes (<c>"2"</c>, <c>"0.5"</c>); null when absent. Anything else —
    /// not a number, zero, negative — throws: a run never goes on with a limit it was not given.
    /// </summary>
    public static TimeSpan? ParseMinutes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            || !double.IsFinite(minutes) || minutes <= 0)
            throw new InvalidOperationException(
                $"'{value}' is not a duration in minutes (a positive number such as 2 or 0.5).");
        return TimeSpan.FromMinutes(minutes);
    }
}
