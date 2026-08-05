using System.Text.Json.Serialization;

namespace Auxilia.Steering.Codec;

/// <summary>
/// One frame of the steering wire protocol — the JSON payloads riding the run's
/// <c>steering</c> view (workflow → client) and the Core's deliver-input channel
/// (client → workflow). The platform carries these verbatim and never interprets them;
/// both ends speak them through <see cref="SteeringCodec"/>. The protocol is additive:
/// decoders ignore unknown frame types and unknown properties.
/// </summary>
public abstract record SteeringFrame
{
    /// <summary>The frame discriminator, serialized as <c>$type</c> (always the first property).</summary>
    [JsonPropertyName("$type")]
    [JsonPropertyOrder(-10)]
    public string Type => TypeDiscriminator;

    /// <summary>Each frame's <c>TypeName</c> constant; kept off the wire surface itself.</summary>
    protected abstract string TypeDiscriminator { get; }
}

// --- Workflow → client (published on the run's "steering" view) ---

/// <summary>Announces which input kinds the running session accepts (e.g. guidance, halt, end).</summary>
public sealed record SteeringCapabilities(
    [property: JsonPropertyName("accepts")] IReadOnlyList<string> Accepts) : SteeringFrame
{
    public const string TypeName = "capabilities";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>One selectable option of a <see cref="SteeringFormQuestion"/>.</summary>
public sealed record SteeringFormOption(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string? Description = null);

/// <summary>
/// One question of a form. <see cref="Detail"/> is an optional preformatted block;
/// <see cref="DetailFormat"/> is an open vocabulary — null/"code" = monospace, "markdown" = rendered.
/// </summary>
public sealed record SteeringFormQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("options")] IReadOnlyList<SteeringFormOption> Options,
    [property: JsonPropertyName("multiSelect")] bool MultiSelect = false,
    [property: JsonPropertyName("allowFreeText")] bool AllowFreeText = false,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("detailFormat")] string? DetailFormat = null);

/// <summary>Raises a question form in the steering client; answered with <see cref="SteeringFormAnswer"/>.</summary>
public sealed record SteeringFormRequested(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("questions")] IReadOnlyList<SteeringFormQuestion> Questions) : SteeringFrame
{
    public const string TypeName = "form-requested";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>A form was answered (or abandoned) — observers drop its decision card.</summary>
public sealed record SteeringFormResolved(
    [property: JsonPropertyName("requestId")] string RequestId) : SteeringFrame
{
    public const string TypeName = "form-resolved";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>The session is over — no stale decision cards survive.</summary>
public sealed record SteeringSessionEnded(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] string? Error = null) : SteeringFrame
{
    public const string TypeName = "session-ended";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>One agent turn finished; the session is idle awaiting operator input.</summary>
public sealed record SteeringTurnEnded(
    [property: JsonPropertyName("turn")] int Turn,
    [property: JsonPropertyName("summary")] string? Summary = null) : SteeringFrame
{
    public const string TypeName = "turn-ended";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>The session needs the operator's attention (e.g. a blocking permission prompt).</summary>
public sealed record SteeringAttention(
    [property: JsonPropertyName("message")] string Message) : SteeringFrame
{
    public const string TypeName = "attention";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>One switchable model of the session's live vocabulary.</summary>
public sealed record SteeringVocabularyModel(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("efforts")] IReadOnlyList<string> Efforts,
    [property: JsonPropertyName("defaultEffort")] string? DefaultEffort = null);

/// <summary>The session's live vocabulary — steering-client dropdowns are never statically populated.</summary>
public sealed record SteeringSessionVocabulary(
    [property: JsonPropertyName("models")] IReadOnlyList<SteeringVocabularyModel> Models) : SteeringFrame
{
    public const string TypeName = "session-vocabulary";
    protected override string TypeDiscriminator => TypeName;
}

// --- Client → workflow (posted through the Core's deliver-input endpoint) ---

/// <summary>Free-text guidance the operator sends into the running session.</summary>
public sealed record SteeringGuidance(
    [property: JsonPropertyName("text")] string Text) : SteeringFrame
{
    public const string TypeName = "guidance";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>The operator halts the run (hard stop).</summary>
public sealed record SteeringHalt : SteeringFrame
{
    public const string TypeName = "halt";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>The operator gracefully ends a multi-turn session.</summary>
public sealed record SteeringEnd : SteeringFrame
{
    public const string TypeName = "end";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>A live session-setting change; keys are the consuming workflow's vocabulary.</summary>
public sealed record SteeringSetting(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value) : SteeringFrame
{
    public const string TypeName = "setting";
    protected override string TypeDiscriminator => TypeName;
}

/// <summary>The operator's answer to one question of a form.</summary>
public sealed record SteeringAnswer(
    [property: JsonPropertyName("questionId")] string QuestionId,
    [property: JsonPropertyName("selectedIds")] IReadOnlyList<string> SelectedIds,
    [property: JsonPropertyName("freeText")] string? FreeText = null);

/// <summary>The submitted answers of a <see cref="SteeringFormRequested"/> form.</summary>
public sealed record SteeringFormAnswer(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("answers")] IReadOnlyList<SteeringAnswer> Answers) : SteeringFrame
{
    public const string TypeName = "form-answer";
    protected override string TypeDiscriminator => TypeName;
}
