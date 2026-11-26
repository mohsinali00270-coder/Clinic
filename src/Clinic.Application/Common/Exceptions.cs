namespace Clinic.Application.Common;

/// <summary>Mapped to 404. Also used for rows in other tenants, so their existence never leaks.</summary>
public sealed class NotFoundException(string entity, object key)
    : Exception($"{entity} '{key}' was not found.");

/// <summary>Mapped to 409.</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>Mapped to 403.</summary>
public sealed class ForbiddenException(string message = "You do not have permission to do this.") : Exception(message);

/// <summary>Mapped to 401, optionally with a ProblemDetails type the client reacts to.</summary>
public sealed class AuthFailedException(string message, string? problemType = null) : Exception(message)
{
    public string? ProblemType { get; } = problemType;
}

/// <summary>Mapped to 400 with field errors keyed like the request (and, later, dynamic field keys).</summary>
public sealed class RequestValidationException(IDictionary<string, string[]> errors)
    : Exception("One or more validation errors occurred.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public RequestValidationException(string field, string error)
        : this(new Dictionary<string, string[]> { [field] = [error] }) { }
}
