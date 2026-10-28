namespace FateAndFridge.Api.Errors;

/// <summary>
/// Error payload of the shared envelope (docs/API.md §4). <see cref="Details"/>
/// is always serialized, using an empty object when there is nothing to add.
/// </summary>
public sealed record ApiError(string Code, string Message, IReadOnlyDictionary<string, object?> Details);

/// <summary>Shared error envelope: <c>{ "error": {... } }</c>.</summary>
public sealed record ApiErrorEnvelope(ApiError Error);
