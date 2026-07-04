namespace BookingsSearch.Models;

public enum StaffAvailability { In, Available, Out }

public sealed record StaffStatusVm(
    StaffMemberVm Staff,
    StaffAvailability Status,
    AppointmentVm? CurrentAppointment,
    AppointmentVm? NextAppointment);
