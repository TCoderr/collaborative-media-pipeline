namespace MediaPipeline.Application.Media;

/// <summary>
/// Raised when media cannot be processed and retrying could not possibly help - a corrupt
/// file, an unsupported codec, or an image whose decoded size exceeds policy.
/// <para>
/// Declared in the application layer so the retry classifier can recognise it by type. The
/// message is written to be safe to persist and show to a collaborator: it never carries a
/// stack trace, a storage key or a credential.
/// </para>
/// </summary>
public sealed class PermanentMediaException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
