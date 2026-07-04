namespace BookingsSearch.Models;

public sealed record StaffMemberVm(
    string Id,
    string DisplayName,
    string? EmailAddress,
    string? Role);
