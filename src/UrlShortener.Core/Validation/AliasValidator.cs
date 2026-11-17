using System.Text.RegularExpressions;
using UrlShortener.Core.Domain;

namespace UrlShortener.Core.Validation;

/// <summary>Validates user-chosen custom aliases.</summary>
public static partial class AliasValidator
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    /// <summary>
    /// Path segments the service itself routes (or may route in future). An alias with these
    /// names would be shadowed by, or shadow, a real endpoint.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "health", "healthz", "swagger", "openapi", "admin", "static", "assets", "favicon.ico", "robots.txt",
    };

    public static DomainError? Validate(string alias)
    {
        const string field = "customAlias";

        if (alias.Length is < MinLength or > MaxLength)
        {
            return DomainError.Validation(
                "alias_length", $"Custom alias must be {MinLength}-{MaxLength} characters.", field);
        }

        if (!AllowedPattern().IsMatch(alias))
        {
            return DomainError.Validation(
                "alias_invalid_characters",
                "Custom alias may contain only letters, digits, '-' and '_'.",
                field);
        }

        if (Reserved.Contains(alias))
        {
            return DomainError.Validation("alias_reserved", $"'{alias}' is reserved.", field);
        }

        return null;
    }

    /// <summary>
    /// Cheap syntactic check used on the redirect path to reject junk before touching storage.
    /// Accepts both generated codes and custom aliases.
    /// </summary>
    public static bool IsWellFormedCode(string? code) =>
        code is { Length: >= MinLength and <= MaxLength } && AllowedPattern().IsMatch(code);

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllowedPattern();
}
