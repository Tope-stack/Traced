namespace Traced.Api.Contracts;

/// <summary>Spec <c>Error</c>: the body of every 4xx response.</summary>
public sealed record ErrorResponse(string Error);
