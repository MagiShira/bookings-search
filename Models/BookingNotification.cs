namespace BookingsSearch.Models;

public sealed record BookingNotification(
    string BookingId,
    IReadOnlyList<NotificationStaffMember> StaffMembers,
    DateTimeOffset ReceivedAt);

public sealed record NotificationStaffMember(
    string DisplayName,
    string EmailAddress,
    string M365Id);
