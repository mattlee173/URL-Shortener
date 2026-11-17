using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;
using UrlShortener.Core.Validation;

namespace UrlShortener.Core.Services;

public sealed record CreateShortLinkCommand(string? Url, string? CustomAlias);

/// <summary>Application service holding the shortener's business rules.</summary>
public sealed class ShortLinkService
{
    private readonly IShortLinkRepository _repository;
    private readonly IShortCodeGenerator _codeGenerator;
    private readonly TargetUrlValidator _urlValidator;
    private readonly ShortenerOptions _options;
    private readonly TimeProvider _time;

    public ShortLinkService(
        IShortLinkRepository repository,
        IShortCodeGenerator codeGenerator,
        TargetUrlValidator urlValidator,
        ShortenerOptions options,
        TimeProvider time)
    {
        _repository = repository;
        _codeGenerator = codeGenerator;
        _urlValidator = urlValidator;
        _options = options;
        _time = time;
    }

    public async Task<Result<ShortLink>> CreateAsync(CreateShortLinkCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var url = _urlValidator.Validate(command.Url);
        if (!url.IsSuccess)
        {
            return url.Error!;
        }

        var now = UtcNowMs();

        if (!string.IsNullOrWhiteSpace(command.CustomAlias))
        {
            var alias = command.CustomAlias.Trim();
            if (AliasValidator.Validate(alias) is { } aliasError)
            {
                return aliasError;
            }

            var link = NewLink(alias, url.Value, now);
            return await _repository.TryAddAsync(link, ct).ConfigureAwait(false)
                ? link
                : DomainError.Conflict("alias_taken", $"The alias '{alias}' is already in use.");
        }

        // Random codes: on the (astronomically rare) collision, retry with a new code.
        // The repository insert is atomic, so concurrent creators cannot both win the same code.
        for (var attempt = 0; attempt < _options.MaxGenerationAttempts; attempt++)
        {
            var link = NewLink(_codeGenerator.Generate(), url.Value, now);
            if (await _repository.TryAddAsync(link, ct).ConfigureAwait(false))
            {
                return link;
            }
        }

        return DomainError.Unavailable(
            "code_generation_failed", "Could not allocate a unique short code. Please retry.");
    }

    /// <summary>Resolves a code for redirection. Deleted links resolve to <see cref="ErrorKind.Gone"/>.</summary>
    public async Task<Result<ShortLink>> ResolveAsync(string code, CancellationToken ct)
    {
        var link = await _repository.GetAsync(code, ct).ConfigureAwait(false);
        if (link is null)
        {
            return DomainError.NotFound($"No link exists for code '{code}'.");
        }

        if (link.IsDeleted)
        {
            return DomainError.Gone("link_deleted", "This link has been deleted.");
        }

        return link;
    }

    /// <summary>Returns link metadata, including deleted links (management view).</summary>
    public async Task<Result<ShortLink>> GetAsync(string code, CancellationToken ct)
    {
        var link = await _repository.GetAsync(code, ct).ConfigureAwait(false);
        return link is null ? DomainError.NotFound($"No link exists for code '{code}'.") : link;
    }

    public async Task<Result<bool>> DeleteAsync(string code, CancellationToken ct)
    {
        var deleted = await _repository.MarkDeletedAsync(code, UtcNowMs(), ct).ConfigureAwait(false);
        return deleted ? true : DomainError.NotFound($"No active link exists for code '{code}'.");
    }

    /// <summary>
    /// Timestamps are kept at millisecond precision, matching storage. Otherwise the create
    /// response (full tick precision) would disagree with every later read of the same link.
    /// </summary>
    private DateTimeOffset UtcNowMs()
    {
        var now = _time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private static ShortLink NewLink(string code, string url, DateTimeOffset now) =>
        new() { Code = code, TargetUrl = url, CreatedAt = now };
}
