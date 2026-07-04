namespace BookingsSearch.Models;

public sealed record AppointmentVm(
    string Id,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string ServiceName,
    string? ServiceNotes,
    string? TicketId,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<string> StaffMemberIds,
    IReadOnlyList<string> StaffMemberNames,
    // email → Microsoft 365 GUID, populated when a Power Automate notification has been received
    IReadOnlyDictionary<string, string>? StaffM365Ids = null)
{
    public bool IsActive => DateTimeOffset.Now >= Start && DateTimeOffset.Now < End;
    public bool IsPast   => DateTimeOffset.Now >= End;
    public TimeSpan Duration => End - Start;
}
