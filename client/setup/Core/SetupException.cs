namespace Vigil.Setup.Core;

/// <summary>An expected failure with a message for the user (no stack trace on screen).</summary>
public class SetupException(string message) : Exception(message);

public enum PairingFailure
{
    /// <summary>No active code: expired, used or never created (404).</summary>
    NoCode,
    /// <summary>Wrong code (401); <see cref="PairingException.AttemptsLeft"/> tries left.</summary>
    WrongCode,
    /// <summary>No answer or an unexpected one.</summary>
    Server,
}

/// <summary>Pairing failed before anything was changed: the wizard asks for the code again.</summary>
public sealed class PairingException(PairingFailure failure, string message, int? attemptsLeft = null)
    : SetupException(message)
{
    public PairingFailure Failure { get; } = failure;
    public int? AttemptsLeft { get; } = attemptsLeft;
}
