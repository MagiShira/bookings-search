using BookingsSearch.Models;

namespace BookingsSearch.Services;

public interface IBookingsService
{
    Task<IReadOnlyList<AppointmentVm>> GetTodaysAppointmentsAsync(string? staffMemberId = null, CancellationToken ct = default);
    Task<IReadOnlyList<AppointmentVm>> SearchAppointmentsAsync(SearchFilterVm filter, CancellationToken ct = default);
    Task<IReadOnlyList<StaffMemberVm>> GetStaffMembersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StaffStatusVm>> GetStaffStatusAsync(CancellationToken ct = default);
}
