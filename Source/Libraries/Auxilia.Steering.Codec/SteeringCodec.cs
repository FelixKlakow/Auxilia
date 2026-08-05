using System.Text.Json;

namespace Auxilia.Steering.Codec;

/// <summary>
/// Encodes and decodes <see cref="SteeringFrame"/>s. Decoding is tolerant by design — the
/// protocol is additive, so an unknown frame type, unknown properties, or malformed JSON
/// yield <c>null</c> instead of an exception: a newer peer must never break an older one.
/// </summary>
public static class SteeringCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes a frame to its wire JSON (<c>$type</c> first).</summary>
    public static string Encode(SteeringFrame frame)
        => JsonSerializer.Serialize(frame, frame.GetType(), Options);

    /// <summary>Decodes a wire payload, or null for unknown frame types and malformed JSON.</summary>
    public static SteeringFrame? Decode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("$type", out var discriminator))
                return null;
            return discriminator.GetString() switch
            {
                SteeringCapabilities.TypeName => doc.Deserialize<SteeringCapabilities>(Options),
                SteeringFormRequested.TypeName => doc.Deserialize<SteeringFormRequested>(Options),
                SteeringFormResolved.TypeName => doc.Deserialize<SteeringFormResolved>(Options),
                SteeringSessionEnded.TypeName => doc.Deserialize<SteeringSessionEnded>(Options),
                SteeringTurnEnded.TypeName => doc.Deserialize<SteeringTurnEnded>(Options),
                SteeringAttention.TypeName => doc.Deserialize<SteeringAttention>(Options),
                SteeringSessionVocabulary.TypeName => doc.Deserialize<SteeringSessionVocabulary>(Options),
                SteeringGuidance.TypeName => doc.Deserialize<SteeringGuidance>(Options),
                SteeringHalt.TypeName => doc.Deserialize<SteeringHalt>(Options),
                SteeringEnd.TypeName => doc.Deserialize<SteeringEnd>(Options),
                SteeringSetting.TypeName => doc.Deserialize<SteeringSetting>(Options),
                SteeringFormAnswer.TypeName => doc.Deserialize<SteeringFormAnswer>(Options),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
