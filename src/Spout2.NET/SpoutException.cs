namespace Spout2.NET;

/// <summary>A Spout operation failed: the shared registry, a sender, or its texture.</summary>
public sealed class SpoutException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SpoutException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    public SpoutException(string message)
        : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The failure behind it.</param>
    public SpoutException(string message, Exception innerException)
        : base(message, innerException) { }
}
