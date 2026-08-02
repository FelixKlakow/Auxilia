namespace Auxilia.AdminConsole.Support;

/// <summary>Human-friendly time formatting shared by the console pages.</summary>
public static class TimeText
{
    public static string Relative(DateTimeOffset utc)
    {
        var delta = DateTimeOffset.UtcNow - utc;
        return delta switch
        {
            { TotalSeconds: < 5 } => "just now",
            { TotalMinutes: < 1 } => $"{(int)delta.TotalSeconds} s ago",
            { TotalHours: < 1 } => $"{(int)delta.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)delta.TotalHours} h ago",
            { TotalDays: < 30 } => $"{(int)delta.TotalDays} d ago",
            _ => utc.ToString("yyyy-MM-dd")
        };
    }

    public static string Duration(DateTimeOffset start, DateTimeOffset? end)
        => end is null ? "—" : Span(end.Value - start);

    public static string Interval(int seconds) => Span(TimeSpan.FromSeconds(seconds));

    private static string Span(TimeSpan span) => span switch
    {
        { TotalSeconds: < 1 } => "< 1 s",
        { TotalMinutes: < 1 } => $"{(int)span.TotalSeconds} s",
        { TotalHours: < 1 } when span.Seconds == 0 => $"{(int)span.TotalMinutes} min",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min {span.Seconds} s",
        _ when span.Minutes == 0 => $"{(int)span.TotalHours} h",
        _ => $"{(int)span.TotalHours} h {span.Minutes} min"
    };
}
