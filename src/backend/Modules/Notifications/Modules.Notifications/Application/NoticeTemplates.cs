using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TravelBooking.Modules.Notifications.Application;

/// <summary>The non-personal facts a booking notice shows (stored as JSON on the notification).</summary>
internal sealed record BookingNoticeValues(Guid OrderId, IReadOnlyList<string> BookingReferences, string? ChargedAmount, string? ChargedCurrency)
{
    public string ToJson() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

    public static BookingNoticeValues FromJson(string json) =>
        JsonSerializer.Deserialize<BookingNoticeValues>(json, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("Empty notice values.");
}

internal sealed record RenderedNotice(string Subject, string HtmlBody, string TextBody);

/// <summary>
/// The customer notices (ADR 0024): typed templates in code, HTML and plain text, every value HTML-encoded. Texts are per
/// culture (English first; a language is added as another <see cref="NoticeTexts"/>). Amounts and references come from
/// the server-side snapshot as they were stored, never recomputed. Never personal data beyond the recipient address, and
/// never document numbers, card data or supplier messages.
/// </summary>
internal static class NoticeTemplates
{
    public const string BookingConfirmed = "booking-confirmed";
    public const string BookingPartiallyConfirmed = "booking-partially-confirmed";
    public const string BookingNotBooked = "booking-not-booked";
    public const string RefundCompleted = "refund-completed";
    public const string BookingCancelled = "booking-cancelled";
    public const string RefundDelayed = "refund-delayed";

    /// <summary>Bumped when a template's meaning changes; stored on each notification.</summary>
    public const int Version = 1;

    public const string DefaultCulture = "en";

    private static readonly Dictionary<string, NoticeTexts> _texts = new(StringComparer.Ordinal) { ["en"] = NoticeTexts.English };

    public static bool IsKnown(string kind) => kind is BookingConfirmed or BookingPartiallyConfirmed or BookingNotBooked or RefundCompleted or BookingCancelled or RefundDelayed;

    public static RenderedNotice Render(string kind, string culture, BookingNoticeValues values)
    {
        var texts = _texts.GetValueOrDefault(culture) ?? _texts[DefaultCulture];
        var (subject, intro) = kind switch
        {
            BookingConfirmed => (texts.ConfirmedSubject, texts.ConfirmedIntro),
            BookingPartiallyConfirmed => (texts.PartialSubject, texts.PartialIntro),
            BookingNotBooked => (texts.NotBookedSubject, texts.NotBookedIntro),
            RefundCompleted => (texts.RefundSubject, texts.RefundIntro),
            BookingCancelled => (texts.CancelledSubject, texts.CancelledIntro),
            RefundDelayed => (texts.RefundDelayedSubject, texts.RefundDelayedIntro),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown notice."),
        };

        var lines = new List<(string Label, string Value)> { (texts.OrderLabel, values.OrderId.ToString("D", CultureInfo.InvariantCulture)) };
        if (values.BookingReferences.Count > 0)
        {
            lines.Add((texts.ReferencesLabel, string.Join(", ", values.BookingReferences)));
        }

        // A cancellation promises no amount: the refund, if any, still needs a second person's approval (ADR 0027).
        var amountLabel = kind switch
        {
            RefundCompleted => texts.RefundedLabel,
            RefundDelayed => texts.RefundAmountLabel,
            BookingCancelled => null,
            _ => texts.ChargedLabel,
        };
        if (amountLabel is not null)
        {
            lines.Add(values.ChargedAmount is { } amount && values.ChargedCurrency is { } currency
                ? (amountLabel, $"{amount} {currency}")
                : (amountLabel, texts.NothingCharged));
        }

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"").Append(Encode(texts.Language)).Append("\"><head><meta charset=\"utf-8\"><title>")
            .Append(Encode(subject)).Append("</title></head><body><p>").Append(Encode(intro)).Append("</p><table role=\"presentation\">");
        foreach (var (label, value) in lines)
        {
            html.Append("<tr><th scope=\"row\" style=\"text-align:start;padding-inline-end:1em\">").Append(Encode(label))
                .Append("</th><td>").Append(Encode(value)).Append("</td></tr>");
        }

        html.Append("</table><p>").Append(Encode(texts.Footer)).Append("</p></body></html>");

        var text = new StringBuilder().AppendLine(intro).AppendLine();
        foreach (var (label, value) in lines)
        {
            text.Append(label).Append(": ").AppendLine(value);
        }

        text.AppendLine().Append(texts.Footer);
        return new RenderedNotice(subject, html.ToString(), text.ToString());
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);
}

/// <summary>The texts of the notices in one language.</summary>
internal sealed record NoticeTexts(
    string Language,
    string ConfirmedSubject,
    string ConfirmedIntro,
    string PartialSubject,
    string PartialIntro,
    string NotBookedSubject,
    string NotBookedIntro,
    string OrderLabel,
    string ReferencesLabel,
    string ChargedLabel,
    string NothingCharged,
    string RefundSubject,
    string RefundIntro,
    string RefundedLabel,
    string CancelledSubject,
    string CancelledIntro,
    string RefundAmountLabel,
    string RefundDelayedSubject,
    string RefundDelayedIntro,
    string Footer)
{
    public static readonly NoticeTexts English = new(
        "en",
        "Your booking is confirmed",
        "Your flight booking is confirmed. Keep the booking reference for your trip.",
        "Part of your booking is confirmed",
        "Part of your booking is confirmed. You are charged only for what is booked; the rest of the amount held on your card is being released.",
        "We could not complete your booking",
        "We could not complete your booking. Nothing was charged, and the amount held on your card is being released (your bank may take a few days to show it).",
        "Order",
        "Booking reference",
        "Charged",
        "Nothing",
        "Your refund has been sent",
        "Your refund has been sent to the card you paid with. Your bank may take a few days to show it.",
        "Refunded",
        "Your booking has been cancelled",
        "Your booking has been cancelled. If a refund is due, our team checks it first and sends it to the card you paid with; we will email you when it is sent.",
        "Refund amount",
        "Your refund is delayed",
        "We could not complete your refund yet. Our team is looking into it and will contact you; you do not need to do anything.",
        "This is an automated message about your order. Your trip details are in your account.");
}
