using Microsoft.EntityFrameworkCore;

namespace TravelBooking.BuildingBlocks.Audit;

/// <summary>
/// Where an audited request came from: its trace id, the client's address (as resolved by the trusted forwarded-headers
/// configuration only) and its user agent. Built at the HTTP edge (<c>Http.AuditSources.From</c>).
/// </summary>
public sealed record AuditSource(string? CorrelationId, string? ClientAddress, string? UserAgent);

/// <summary>
/// One audited action (security rules: actor, action, target, before/after, client address and user agent, correlation
/// id), append-only. Each module writes its entries in its own schema, in the same save as the action itself (ADR 0022),
/// so an action is never recorded without having happened, nor happens without its record. Before and after are short
/// redacted summaries (statuses, amounts, references), never personal data.
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset At,
    string Actor,
    string Action,
    string Target,
    string? Before,
    string? After,
    string? CorrelationId,
    string? ClientAddress,
    string? UserAgent)
{
    public const int MaxTextLength = 500;

    public long Id { get; private set; }

    public static AuditEntry For(AuditSource source, DateTimeOffset at, string actor, string action, string target, string? before, string? after) =>
        new(at, actor, action, target, Clip(before), Clip(after), source.CorrelationId, source.ClientAddress, source.UserAgent);

    public static string? Clip(string? value, int max = MaxTextLength) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}

/// <summary>
/// The reason a staff member gives for an audited action (ADR 0022): a ticket reference, optionally with a short note in
/// plain characters (letters, digits, spaces and . _ : / # -), 3 to 200 characters. No markup, control or format
/// characters, so nothing can disguise the audit text, and never a card number: a run of 13 or more digits, even split
/// by spaces or hyphens, is refused (SAQ-A: card data never reaches our records). Names and other personal data are
/// excluded by procedure (runbooks: a ticket reference only), not by this rule.
/// </summary>
public static class AuditReasons
{
    public const int MaxLength = 200;
    private const int _cardLikeDigits = 13;

    public static bool IsValid(string? reason) =>
        reason is { Length: >= 3 and <= MaxLength } && char.IsAsciiLetterOrDigit(reason[0])
        && reason.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or ':' or '/' or '#' or '-')
        && !HasCardLikeNumber(reason);

    // Digits, possibly separated by single spaces or hyphens (as card numbers are written), 13 or more of them in a row.
    private static bool HasCardLikeNumber(string reason)
    {
        var digits = 0;
        for (var i = 0; i < reason.Length; i++)
        {
            var c = reason[i];
            if (char.IsAsciiDigit(c))
            {
                if (++digits >= _cardLikeDigits)
                {
                    return true;
                }
            }
            else if (!(c is ' ' or '-' && i > 0 && char.IsAsciiDigit(reason[i - 1]) && i + 1 < reason.Length && char.IsAsciiDigit(reason[i + 1])))
            {
                digits = 0;
            }
        }

        return false;
    }
}

public static class AuditLogModelBuilderExtensions
{
    /// <summary>The module's own append-only <c>AuditEntries</c> table (in its default schema).</summary>
    public static ModelBuilder AddAuditLog(this ModelBuilder modelBuilder)
    {
        var audit = modelBuilder.Entity<AuditEntry>();
        audit.ToTable("AuditEntries");
        audit.HasKey(a => a.Id);
        audit.Property(a => a.Id).UseIdentityColumn();
        audit.Property(a => a.Actor).HasMaxLength(100);
        audit.Property(a => a.Action).HasMaxLength(100).IsUnicode(false);
        audit.Property(a => a.Target).HasMaxLength(100).IsUnicode(false);
        audit.Property(a => a.Before).HasMaxLength(AuditEntry.MaxTextLength);
        audit.Property(a => a.After).HasMaxLength(AuditEntry.MaxTextLength);
        audit.Property(a => a.CorrelationId).HasMaxLength(64).IsUnicode(false);
        audit.Property(a => a.ClientAddress).HasMaxLength(64).IsUnicode(false);
        audit.Property(a => a.UserAgent).HasMaxLength(256);
        audit.HasIndex(a => new { a.Target, a.At });
        audit.HasIndex(a => a.At);
        return modelBuilder;
    }
}
