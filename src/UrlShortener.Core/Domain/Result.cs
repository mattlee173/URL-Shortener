namespace UrlShortener.Core.Domain;

/// <summary>Categories of expected (non-exceptional) failures. The API layer maps each to an HTTP status.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Gone,
    Unavailable,
}

public sealed record DomainError(ErrorKind Kind, string Code, string Message, string? Field = null)
{
    public static DomainError Validation(string code, string message, string? field = null) =>
        new(ErrorKind.Validation, code, message, field);

    public static DomainError NotFound(string message) => new(ErrorKind.NotFound, "not_found", message);

    public static DomainError Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static DomainError Gone(string code, string message) => new(ErrorKind.Gone, code, message);

    public static DomainError Unavailable(string code, string message) => new(ErrorKind.Unavailable, code, message);
}

/// <summary>
/// Success-or-error value used for expected outcomes (bad input, unknown code, alias taken)
/// so control flow does not rely on exceptions.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = null;
    }

    private Result(DomainError error)
    {
        _value = default;
        Error = error;
    }

    public DomainError? Error { get; }

    public bool IsSuccess => Error is null;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read Value of a failed result ({Error!.Code}).");

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(DomainError error) => new(error);
}
