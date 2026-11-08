using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Auxilia.Core.Api.Data;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Where a client's run stream stands, sent as every frame's SSE <c>id</c> (and
/// <see cref="Auxilia.Core.Contracts.RunStreamEvent.Cursor"/>): the last view sequence it received
/// and a mark of the last status it received. Text form <c>"{sequence}.{mark}"</c>, or a bare
/// <c>"{sequence}"</c> when the client holds no status (it then gets the current one first).
/// </summary>
public readonly record struct RunStreamCursor(long Sequence, string? StatusMark)
{
    private const int MarkLength = 8;
    private const int MaxTextLength = 64;

    public override string ToString()
        => StatusMark is null
            ? Sequence.ToString(CultureInfo.InvariantCulture)
            : $"{Sequence.ToString(CultureInfo.InvariantCulture)}.{StatusMark}";

    /// <summary>Parses a cursor a client sent back; false for anything this Core never issued.</summary>
    public static bool TryParse(string? text, out RunStreamCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            return false;
        var dot = text.IndexOf('.');
        var sequencePart = dot < 0 ? text : text[..dot];
        if (!long.TryParse(sequencePart, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            return false;
        if (dot < 0)
        {
            cursor = new RunStreamCursor(sequence, null);
            return true;
        }
        var mark = text[(dot + 1)..];
        if (mark.Length != MarkLength || !mark.All(char.IsAsciiHexDigitLower))
            return false;
        cursor = new RunStreamCursor(sequence, mark);
        return true;
    }

    /// <summary>
    /// Identifies a run status as the Core holds it: equal marks are the same status, so a resuming
    /// client gets the status again only when it changed meanwhile. Millisecond precision — what
    /// every store keeps of a timestamp.
    /// </summary>
    public static string MarkOf(CoreRunRecord record)
        => MarkOf(record.State, record.ErrorMessage, record.UpdatedUtc);

    public static string MarkOf(string state, string? error, DateTimeOffset updatedUtc)
    {
        var identity =
            $"{state}\n{error}\n{updatedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..MarkLength];
    }
}
