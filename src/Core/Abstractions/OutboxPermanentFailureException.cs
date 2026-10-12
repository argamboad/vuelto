namespace Vuelto.Core.Abstractions;

/// <summary>
/// Thrown by an <see cref="IOutboxHandler"/> for a failure no retry can change — a URL the SSRF guard refuses, a
/// payload that doesn't parse (v4 audit H8, JOBS-3). The processor dead-letters the message on that attempt
/// instead of repeating it until the attempt cap; any other exception is retried with backoff as before.
/// </summary>
public sealed class OutboxPermanentFailureException(string message) : Exception(message);
