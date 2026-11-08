namespace Auxilia.PlatformData.Protection;

/// <summary>Pass-through protector for trusted-operator dev setups without a configured key.</summary>
public sealed class NullSettingsProtector : ISettingsProtector
{
    public string Protect(string plaintext) => plaintext;

    public string Unprotect(string protectedValue) => protectedValue;

    public byte[] ProtectBytes(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

    public byte[] UnprotectBytes(ReadOnlySpan<byte> protectedValue) => protectedValue.ToArray();
}
