using System.Text.Json.Serialization;

namespace UrlShortener.Core.Analytics;

/// <summary>Coarse device category derived from the User-Agent. Raw user agents are not stored.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClientKind>))]
public enum ClientKind
{
    Unknown,
    Desktop,
    Mobile,
    Bot,
}

/// <summary>
/// One successful redirect. Deliberately privacy-preserving: the client IP is never stored, only a
/// keyed hash (<see cref="VisitorHash"/>) good enough to estimate unique visitors; the referrer is
/// reduced to its host so paths/query strings (which can carry tokens or PII) are dropped.
/// </summary>
public sealed record ClickEvent(
    string Code,
    DateTimeOffset OccurredAt,
    string? ReferrerHost,
    ClientKind ClientKind,
    string VisitorHash);
