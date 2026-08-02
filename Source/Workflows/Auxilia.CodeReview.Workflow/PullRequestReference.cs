namespace Auxilia.CodeReview.Workflow;

/// <summary>Provider-neutral pull-request input record.</summary>
public sealed record PullRequestReference(string PrIdentifier, string BaseRef, string HeadRef);
