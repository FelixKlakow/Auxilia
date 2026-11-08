using System.Text;

namespace Auxilia.EgressGateway;

/// <summary>
/// Reads the server name a TLS client asks for from its ClientHello — the handshake's first
/// message, sent in the clear before anything is encrypted (RFC 8446 §4.1.2, RFC 6066 §3). The
/// gateway uses it to hold a CONNECT tunnel to the destination it admitted: a client that
/// tunnels to an allowed host but names another one in its handshake (domain fronting over a
/// shared front end) is refused before a byte of the handshake leaves the gateway.
/// </summary>
public static class TlsClientHello
{
    /// <summary>Upper bound for a ClientHello (post-quantum key shares keep real ones far below).</summary>
    public const int MaxHelloBytes = 64 * 1024;

    private const byte HandshakeRecord = 22;
    private const byte ClientHelloMessage = 1;
    private const ushort ServerNameExtension = 0;
    private const byte HostNameType = 0;

    public enum Outcome
    {
        /// <summary>The bytes are not a TLS handshake record — an opaque (non-TLS) tunnel.</summary>
        NotTls,

        /// <summary>A TLS handshake has started but its ClientHello is not complete yet.</summary>
        Incomplete,

        /// <summary>The bytes claim to be a TLS handshake but cannot carry a valid ClientHello.</summary>
        Malformed,

        /// <summary>The ClientHello was read; its server name (if any) is reported.</summary>
        Parsed,
    }

    /// <summary>
    /// Reads the ClientHello from the bytes a client sent so far. The handshake message may span
    /// several records and arrive in arbitrary pieces; <see cref="Outcome.Incomplete"/> asks for
    /// more. <paramref name="serverName"/> is the requested host name, null when the client named
    /// none (e.g. a connection to an address literal).
    /// </summary>
    public static Outcome TryReadServerName(ReadOnlySpan<byte> data, out string? serverName)
    {
        serverName = null;
        if (data.Length == 0)
            return Outcome.Incomplete;
        if (data[0] != HandshakeRecord)
            return Outcome.NotTls;

        // Gather the handshake fragments of consecutive records until the whole message is there.
        var handshake = new List<byte>();
        var offset = 0;
        while (true)
        {
            // (A complete message returns below as soon as its last fragment arrives.)
            if (data.Length - offset < 5)
                return Outcome.Incomplete;
            if (data[offset] != HandshakeRecord || data[offset + 1] != 3)
                return Outcome.Malformed;
            var recordLength = (data[offset + 3] << 8) | data[offset + 4];
            if (recordLength == 0 || recordLength > 16384)
                return Outcome.Malformed;
            if (data.Length - offset - 5 < recordLength)
                return Outcome.Incomplete;
            foreach (var b in data.Slice(offset + 5, recordLength))
                handshake.Add(b);
            offset += 5 + recordLength;

            if (handshake.Count >= 4)
            {
                if (handshake[0] != ClientHelloMessage)
                    return Outcome.Malformed;
                var messageLength = (handshake[1] << 16) | (handshake[2] << 8) | handshake[3];
                if (messageLength > MaxHelloBytes)
                    return Outcome.Malformed;
                if (handshake.Count >= 4 + messageLength)
                    return Parse(handshake, out serverName);
            }
            if (handshake.Count > MaxHelloBytes + 4)
                return Outcome.Malformed;
        }
    }

    /// <summary>A bounds-checked cursor over a handshake message.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public readonly bool AtEnd => _position == _data.Length;
        public readonly int Remaining => _data.Length - _position;

        public bool Skip(int count)
        {
            if (count < 0 || Remaining < count)
                return false;
            _position += count;
            return true;
        }

        public bool ReadU8(out int value)
        {
            value = 0;
            if (Remaining < 1)
                return false;
            value = _data[_position++];
            return true;
        }

        public bool ReadU16(out int value)
        {
            value = 0;
            if (Remaining < 2)
                return false;
            value = (_data[_position] << 8) | _data[_position + 1];
            _position += 2;
            return true;
        }

        public bool Read(int count, out ReadOnlySpan<byte> slice)
        {
            slice = default;
            if (count < 0 || Remaining < count)
                return false;
            slice = _data.Slice(_position, count);
            _position += count;
            return true;
        }
    }

    private static Outcome Parse(List<byte> handshake, out string? serverName)
    {
        serverName = null;
        ReadOnlySpan<byte> message = handshake.ToArray();
        var length = (message[1] << 16) | (message[2] << 8) | message[3];
        var hello = new Reader(message.Slice(4, length));

        // legacy_version, random
        if (!hello.Skip(2 + 32))
            return Outcome.Malformed;
        // legacy_session_id <0..32>
        if (!hello.ReadU8(out var sessionIdLength) || sessionIdLength > 32 || !hello.Skip(sessionIdLength))
            return Outcome.Malformed;
        // cipher_suites <2..2^16-2>
        if (!hello.ReadU16(out var cipherSuitesLength) || cipherSuitesLength < 2 || cipherSuitesLength % 2 != 0
            || !hello.Skip(cipherSuitesLength))
            return Outcome.Malformed;
        // legacy_compression_methods <1..2^8-1>
        if (!hello.ReadU8(out var compressionLength) || compressionLength < 1 || !hello.Skip(compressionLength))
            return Outcome.Malformed;
        // A ClientHello without extensions names no server.
        if (hello.AtEnd)
            return Outcome.Parsed;
        if (!hello.ReadU16(out var extensionsLength) || extensionsLength != hello.Remaining)
            return Outcome.Malformed;

        var seenServerName = false;
        while (!hello.AtEnd)
        {
            if (!hello.ReadU16(out var type) || !hello.ReadU16(out var extensionLength)
                || !hello.Read(extensionLength, out var extension))
                return Outcome.Malformed;
            if (type != ServerNameExtension)
                continue;
            // A second server_name extension would let two parsers disagree on the destination.
            if (seenServerName)
                return Outcome.Malformed;
            seenServerName = true;
            if (ReadHostName(extension) is not { } name)
                return Outcome.Malformed;
            serverName = name;
        }
        return Outcome.Parsed;
    }

    /// <summary>The single host_name of a server_name extension; null when the extension is not well-formed.</summary>
    private static string? ReadHostName(ReadOnlySpan<byte> extension)
    {
        if (extension.Length < 2)
            return null;
        var listLength = (extension[0] << 8) | extension[1];
        if (listLength != extension.Length - 2)
            return null;
        string? hostName = null;
        var p = 2;
        while (p < extension.Length)
        {
            if (extension.Length - p < 3)
                return null;
            var nameType = extension[p];
            var nameLength = (extension[p + 1] << 8) | extension[p + 2];
            p += 3;
            if (nameLength == 0 || extension.Length - p < nameLength)
                return null;
            var name = extension.Slice(p, nameLength);
            p += nameLength;
            if (nameType != HostNameType)
                continue;
            // RFC 6066: at most one name per type; the name is an ASCII DNS host name.
            if (hostName is not null)
                return null;
            foreach (var c in name)
                if (c <= 0x20 || c >= 0x7F)
                    return null;
            hostName = Encoding.ASCII.GetString(name);
        }
        return hostName;
    }
}
