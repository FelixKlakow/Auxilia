namespace Auxilia.PlatformData.Protection;

/// <summary>Protects secret settings at rest, and binary content (e.g. a run's input files).</summary>
public interface ISettingsProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);

    /// <summary>Protects binary content; the result is what goes to disk.</summary>
    byte[] ProtectBytes(ReadOnlySpan<byte> plaintext);

    /// <summary>Restores what <see cref="ProtectBytes"/> produced; throws when it was altered.</summary>
    byte[] UnprotectBytes(ReadOnlySpan<byte> protectedValue);
}
