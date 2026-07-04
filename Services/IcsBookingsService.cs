using System.Net;
using System.Security.Cryptography;
using System.Text;
using BookingsSearch.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace BookingsSearch.Services;

public sealed class IcsBookingsService : IBookingsService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<IcsBookingsService> _logger;
    private readonly NotificationStore _notifications;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    private sealed record CacheEntry(
        IReadOnlyList<AppointmentVm> Appointments,
        string Checksum,
        DateTimeOffset FetchedAt);

    private CacheEntry? _cache;

    public IcsBookingsService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<IcsBookingsService> logger,
        NotificationStore notifications)
    {
        _httpFactory    = httpFactory;
        _config         = config;
        _logger         = logger;
        _notifications  = notifications;
    }

    private string IcsUrl => _config["Bookings:IcsUrl"]
        ?? throw new InvalidOperationException("Bookings:IcsUrl is not configured in appsettings.json");

    private string StaffDomain => _config["Bookings:StaffEmailDomain"] ?? "";

// ── Public interface ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AppointmentVm>> GetTodaysAppointmentsAsync(
        string? staffMemberId = null, CancellationToken ct = default)
    {
        var all   = await GetAllAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.Today);

        IEnumerable<AppointmentVm> results =
            all.Where(a => DateOnly.FromDateTime(a.Start.LocalDateTime) == today);

        if (!string.IsNullOrEmpty(staffMemberId))
            results = results.Where(a => a.StaffMemberIds.Contains(staffMemberId));

        return results.OrderBy(a => a.Start).ToList();
    }

    public async Task<IReadOnlyList<AppointmentVm>> SearchAppointmentsAsync(
        SearchFilterVm filter, CancellationToken ct = default)
    {
        var all  = await GetAllAsync(ct);
        var from = filter.DateFrom.ToDateTime(TimeOnly.MinValue);
        var to   = filter.DateTo.ToDateTime(TimeOnly.MaxValue);

        IEnumerable<AppointmentVm> results =
            all.Where(a => a.Start.LocalDateTime >= from && a.Start.LocalDateTime <= to);

        if (!string.IsNullOrEmpty(filter.StaffMemberId))
            results = results.Where(a => a.StaffMemberIds.Contains(filter.StaffMemberId));

        if (!string.IsNullOrEmpty(filter.ServiceName))
            results = results.Where(a =>
                a.ServiceName.Contains(filter.ServiceName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(filter.Query))
        {
            var tokens = filter.Query
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            results = results.Where(a => MatchesAllTokens(a, tokens));
        }

        return results.OrderBy(a => a.Start).ToList();
    }

    public async Task<IReadOnlyList<StaffMemberVm>> GetStaffMembersAsync(CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);

        return all
            .SelectMany(a => a.StaffMemberIds.Zip(a.StaffMemberNames, (id, name) => new { id, name }))
            .GroupBy(x => x.id)
            .Select(g => new StaffMemberVm(g.Key, g.First().name, g.Key, null))
            .OrderBy(s => s.DisplayName)
            .ToList();
    }

    public async Task<IReadOnlyList<StaffStatusVm>> GetStaffStatusAsync(CancellationToken ct = default)
    {
        var staffTask        = GetStaffMembersAsync(ct);
        var appointmentsTask = GetTodaysAppointmentsAsync(ct: ct);
        await Task.WhenAll(staffTask, appointmentsTask);

        var staff        = await staffTask;
        var appointments = await appointmentsTask;
        var now          = DateTimeOffset.Now;

        return staff.Select(member =>
        {
            var appts = appointments
                .Where(a => a.StaffMemberIds.Contains(member.Id))
                .OrderBy(a => a.Start)
                .ToList();

            var current = appts.FirstOrDefault(a => a.Start <= now && now < a.End);
            var next    = appts.FirstOrDefault(a => a.Start > now);
            var status  = current is not null ? StaffAvailability.In
                        : next    is not null ? StaffAvailability.Available
                        : StaffAvailability.Out;

            return new StaffStatusVm(member, status, current, next);
        })
        .OrderBy(s => s.Status)
        .ThenBy(s => s.Staff.DisplayName)
        .ToList();
    }

    // ── Cache + HTTP fetch ─────────────────────────────────────────────────────

    private async Task<IReadOnlyList<AppointmentVm>> GetAllAsync(CancellationToken ct)
    {
        if (_cache is not null)
            return _cache.Appointments;

        await _fetchLock.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache.Appointments;
            return await FetchAsync(null, ct);
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    internal async Task RefreshAsync(CancellationToken ct = default)
    {
        await _fetchLock.WaitAsync(ct);
        try   { await FetchAsync(_cache, ct); }
        finally { _fetchLock.Release(); }
    }

    private async Task<IReadOnlyList<AppointmentVm>> FetchAsync(CacheEntry? existing, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("ics");

        string content;
        try
        {
            content = await client.GetStringAsync(IcsUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ICS fetch failed");
            if (existing is not null)
            {
                _logger.LogWarning("Returning stale ICS data from {FetchedAt}", existing.FetchedAt);
                _cache = existing with { FetchedAt = DateTimeOffset.UtcNow };
                return existing.Appointments;
            }
            throw;
        }

        var checksum = Checksum(content);

        if (existing is not null && checksum == existing.Checksum)
        {
            _logger.LogDebug("ICS unchanged (checksum match) — skipping reparse");
            _cache = existing with { FetchedAt = DateTimeOffset.UtcNow };
            return existing.Appointments;
        }

        var calendar = Calendar.Load(content)
            ?? throw new InvalidOperationException("Failed to parse ICS calendar data");

        var appointments = calendar.Events
            .Select(MapEvent)
            .OrderBy(a => a.Start)
            .ToList();

        _cache = new CacheEntry(appointments, checksum, DateTimeOffset.UtcNow);

        _logger.LogInformation(
            "ICS updated: {Count} appointments (checksum {Checksum})",
            appointments.Count, checksum[..8]);

        return appointments;
    }

    private static string Checksum(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    // ── Search helpers ─────────────────────────────────────────────────────────

    private static bool MatchesAllTokens(AppointmentVm a, string[] tokens) =>
        tokens.All(t =>
            a.CustomerName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            (a.CustomerEmail?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (a.CustomerPhone?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
            a.ServiceName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
            (a.ServiceNotes?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (a.TicketId?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) ||
            a.StaffMemberNames.Any(n => n.Contains(t, StringComparison.OrdinalIgnoreCase)));

    // ── ICS event mapping ──────────────────────────────────────────────────────

    private AppointmentVm MapEvent(CalendarEvent e)
    {
        var start = ToOffset(e.DtStart!);
        var end   = e.DtEnd is not null ? ToOffset(e.DtEnd) : ToOffset(e.DtStart!);

        var (serviceName, customerName) = ParseSummary(e.Summary ?? "");
        var (customerEmail, customerPhone) = ParseDescription(e.Description ?? "");
        var (serviceNotes, ticketId) = NormalizeDescription(e.Description);
        var (staffIds, staffNames) = ExtractStaff(e);

        if (customerName == "" && e.Attendees is { Count: > 0 })
        {
            var customerAttendee = e.Attendees.FirstOrDefault(a => !IsStaff(a));
            if (customerAttendee is not null)
            {
                customerName  = customerAttendee.CommonName ?? EmailFromAttendee(customerAttendee);
                customerEmail ??= EmailFromAttendee(customerAttendee);
            }
        }

        var uid          = e.Uid ?? Guid.NewGuid().ToString();
        var notification = _notifications.GetByBookingId(uid);
        IReadOnlyDictionary<string, string>? m365Ids = notification?.StaffMembers.Count > 0
            ? notification.StaffMembers.ToDictionary(
                s => s.EmailAddress, s => s.M365Id,
                StringComparer.OrdinalIgnoreCase)
            : null;

        return new AppointmentVm(
            Id:               uid,
            CustomerName:     string.IsNullOrWhiteSpace(customerName) ? "(Unknown)" : customerName,
            CustomerEmail:    customerEmail,
            CustomerPhone:    customerPhone,
            ServiceName:      serviceName,
            ServiceNotes:     serviceNotes,
            TicketId:         ticketId,
            Start:            start,
            End:              end,
            StaffMemberIds:   staffIds,
            StaffMemberNames: staffNames,
            StaffM365Ids:     m365Ids);
    }

    private static (string service, string customer) ParseSummary(string summary)
    {
        var parts = summary.Split([" - "], 2, StringSplitOptions.None);
        return parts.Length == 2
            ? (parts[0].Trim(), parts[1].Trim())
            : (summary.Trim(), "");
    }

    private static (string? email, string? phone) ParseDescription(string description)
    {
        string? email = null, phone = null;
        foreach (var line in description.ReplaceLineEndings("\n").Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("Email:", StringComparison.OrdinalIgnoreCase))
                email = l[6..].Trim();
            else if (l.StartsWith("Phone Number:", StringComparison.OrdinalIgnoreCase))
                phone = l[13..].Trim();
            else if (l.StartsWith("Phone:", StringComparison.OrdinalIgnoreCase))
                phone = l[6..].Trim();
        }
        return (email, phone);
    }

    private static (string? notes, string? ticketId) NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return (null, null);

        var answers = new List<string>();
        string? ticketId = null;
        bool inCustomFields = false;
        int questionNum = 0;

        foreach (var rawLine in description.ReplaceLineEndings("\n").Split('\n'))
        {
            var l = rawLine.Trim();
            if (l.Length == 0 || l.All(c => c == '*' || c == '-')) continue;

            if (l.Equals("Custom Fields", StringComparison.OrdinalIgnoreCase))
            {
                inCustomFields = true;
                continue;
            }

            if (inCustomFields && (
                l.StartsWith("Buffer time:", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("Time zone:", StringComparison.OrdinalIgnoreCase)))
                break;

            if (!inCustomFields) continue;

            var qMatch = System.Text.RegularExpressions.Regex.Match(
                l, @"^Question\s+(\d+)\s*[-–]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (qMatch.Success) { questionNum = int.Parse(qMatch.Groups[1].Value); continue; }

            var aMatch = System.Text.RegularExpressions.Regex.Match(
                l, @"^Answer\s*[-–]\s*(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (aMatch.Success)
            {
                var answer = aMatch.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(answer)) continue;

                if (questionNum == 1)
                {
                    var idMatch = System.Text.RegularExpressions.Regex.Match(
                        answer, @"^\(?(?:No\.\s*)?(\d+)\)?$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (idMatch.Success) { ticketId = idMatch.Groups[1].Value; continue; }
                }

                answers.Add($"Q{questionNum}: {answer}");
            }
        }

        return (answers.Count > 0 ? string.Join("\n", answers) : null, ticketId);
    }

    private (IReadOnlyList<string> ids, IReadOnlyList<string> names) ExtractStaff(CalendarEvent e)
    {
        var staffAttendees = (e.Attendees ?? []).Where(IsStaff).ToList();

        if (e.Organizer is not null && IsStaffEmail(EmailFromCalAddress(e.Organizer.Value)))
        {
            var orgEmail = EmailFromCalAddress(e.Organizer.Value);
            if (staffAttendees.All(a =>
                    !EmailFromAttendee(a).Equals(orgEmail, StringComparison.OrdinalIgnoreCase)))
            {
                staffAttendees.Insert(0, new Attendee
                {
                    CommonName = e.Organizer.CommonName,
                    Value      = e.Organizer.Value,
                });
            }
        }

        return (
            staffAttendees.Select(EmailFromAttendee).ToList(),
            staffAttendees.Select(a => a.CommonName ?? EmailFromAttendee(a)).ToList());
    }

    private bool IsStaff(Attendee a) => IsStaffEmail(EmailFromAttendee(a));

    private bool IsStaffEmail(string email)
    {
        if (string.IsNullOrEmpty(email)) return false;
        if (!string.IsNullOrEmpty(StaffDomain))
            return email.EndsWith("@" + StaffDomain, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static string EmailFromAttendee(Attendee a) => EmailFromCalAddress(a.Value);

    private static string EmailFromCalAddress(Uri? uri) =>
        uri?.AbsoluteUri.Replace("mailto:", "", StringComparison.OrdinalIgnoreCase) ?? "";

    private static DateTimeOffset ToOffset(CalDateTime dt)
    {
        if (dt.IsUtc)
            return new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc));

        if (dt.TzId is not null)
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(dt.TzId);
                return new DateTimeOffset(dt.Value, tz.GetUtcOffset(dt.Value));
            }
            catch { /* unknown TZID — fall through */ }
        }

        return new DateTimeOffset(dt.Value, TimeZoneInfo.Local.GetUtcOffset(dt.Value));
    }
}
