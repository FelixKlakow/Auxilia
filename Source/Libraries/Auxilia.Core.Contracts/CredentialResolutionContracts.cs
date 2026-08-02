namespace Auxilia.Core.Contracts;

/// <summary>
/// Runner → Core request to resolve one slot's credential for a running instance, presented at
/// just-in-time slot activation. The run-scoped resolution token is carried out of band (header),
/// so this body holds only the slot name and the instance's ephemeral public key.
/// </summary>
public sealed record ResolveSlotRequest(string SlotName, string PublicKey);

/// <summary>
/// The Core's resolved answer: the provider type and the slot's settings RSA-encrypted for the
/// requesting instance's public key. Plaintext connector settings never leave the Core — the
/// runner only ever relays this ciphertext to the workflow.
/// </summary>
public sealed record ResolvedSlotCredential(string ProviderType, string EncryptedSettings, DateTimeOffset ExpiresUtc);
