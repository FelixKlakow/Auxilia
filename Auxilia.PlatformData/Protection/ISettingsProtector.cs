namespace Auxilia.PlatformData.Protection;

/// <summary>Protects secret strings before they are persisted inside platform entities.</summary>
public interface ISettingsProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
