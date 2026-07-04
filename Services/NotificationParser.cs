using System.Text.Json;
using System.Text.RegularExpressions;
using BookingsSearch.Models;

namespace BookingsSearch.Services;

public static partial class NotificationParser
{
    [GeneratedRegex(
        @"created an appointment,\s+(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}),.*?with\s+(?<staff>\[.+?\])",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AppointmentPattern();

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public static BookingNotification? Parse(string htmlText)
    {
        var text = StripHtml(htmlText);
        var match = AppointmentPattern().Match(text);
        if (!match.Success) return null;

        var bookingId  = match.Groups["id"].Value;
        var staffJson  = match.Groups["staff"].Value;

        List<NotificationStaffMember> staffMembers = [];
        try
        {
            var dtos = JsonSerializer.Deserialize<List<AttendeeDto>>(staffJson, _jsonOpts);
            if (dtos is not null)
                staffMembers = dtos
                    .Where(d => !string.IsNullOrEmpty(d.EmailAddress) && !string.IsNullOrEmpty(d.Id))
                    .Select(d => new NotificationStaffMember(d.DisplayName, d.EmailAddress, d.Id))
                    .ToList();
        }
        catch (JsonException) { /* malformed staff JSON — keep empty list */ }

        return new BookingNotification(bookingId, staffMembers, DateTimeOffset.UtcNow);
    }

    private static string StripHtml(string html)
    {
        var stripped = Regex.Replace(html, "<[^>]+>", " ");
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    private sealed class AttendeeDto
    {
        public string DisplayName  { get; init; } = "";
        public string EmailAddress { get; init; } = "";
        public string Id           { get; init; } = "";
    }
}
