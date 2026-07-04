namespace BookingsSearch.Models;

public sealed class SearchFilterVm
{
    public string? Query         { get; set; }
    public string? StaffMemberId { get; set; }
    public string? ServiceName   { get; set; }
    public DateOnly DateFrom     { get; set; } = DateOnly.FromDateTime(DateTime.Today.AddDays(-7));
    public DateOnly DateTo       { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}
