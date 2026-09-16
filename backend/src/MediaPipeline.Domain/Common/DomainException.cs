namespace MediaPipeline.Domain.Common;

/// <summary>
/// A domain rule was violated. Carries a stable machine-readable code so the API
/// layer can map it to an error envelope without string matching on messages.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>An entity state transition that the state machine forbids.</summary>
public sealed class InvalidStateTransitionException : DomainException
{
    public InvalidStateTransitionException(string entity, string from, string to)
        : base(
            "INVALID_STATE_TRANSITION",
            $"{entity} cannot move from {from} to {to}.")
    {
        Entity = entity;
        From = from;
        To = to;
    }

    public string Entity { get; }
    public string From { get; }
    public string To { get; }
}
