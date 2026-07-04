using System.Text;
using System.Text.Json;
using BookingsSearch.Components;
using BookingsSearch.Models;
using BookingsSearch.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient("ics");
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddSingleton<IcsBookingsService>();
builder.Services.AddSingleton<IBookingsService>(sp => sp.GetRequiredService<IcsBookingsService>());
builder.Services.AddHostedService<IcsRefreshService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/", () => Results.Redirect("/search"));

// ── Data API ──────────────────────────────────────────────────────────────────

app.MapGet("/api/export.csv", async (IBookingsService bookings,
    string? query, string? staffMemberId, string? dateFrom, string? dateTo,
    CancellationToken ct) =>
{
    var filter = new SearchFilterVm
    {
        Query         = query,
        StaffMemberId = staffMemberId,
        DateFrom      = dateFrom is not null ? DateOnly.Parse(dateFrom) : DateOnly.FromDateTime(DateTime.Today.AddDays(-7)),
        DateTo        = dateTo   is not null ? DateOnly.Parse(dateTo)   : DateOnly.FromDateTime(DateTime.Today),
    };

    var results = await bookings.SearchAppointmentsAsync(filter, ct);

    var sb = new StringBuilder();
    sb.AppendLine("Date,Start,End,Customer,Email,Phone,Service,Ticket ID,Notes");
    foreach (var a in results)
    {
        sb.AppendLine(string.Join(",",
            CsvCell(a.Start.LocalDateTime.ToString("yyyy-MM-dd")),
            CsvCell(a.Start.LocalDateTime.ToString("h:mm tt")),
            CsvCell(a.End.LocalDateTime.ToString("h:mm tt")),
            CsvCell(a.CustomerName),
            CsvCell(a.CustomerEmail ?? ""),
            CsvCell(a.CustomerPhone ?? ""),
            CsvCell(a.ServiceName),
            CsvCell(a.TicketId ?? ""),
            CsvCell(a.ServiceNotes ?? "")));
    }

    var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
    return Results.File(bytes, "text/csv; charset=utf-8", "appointments.csv");
});

app.MapPost("/api/webhook/booking-created", async (
    HttpRequest request,
    NotificationStore notificationStore,
    IConfiguration config,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var apiKey = config["Webhook:ApiKey"];
    if (!string.IsNullOrEmpty(apiKey) &&
        request.Headers["X-Api-Key"].FirstOrDefault() != apiKey)
        return Results.Unauthorized();

    JsonDocument doc;
    try { doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct); }
    catch (JsonException) { return Results.BadRequest("Invalid JSON"); }

    string? notificationText;
    using (doc)
    {
        if (!doc.RootElement.TryGetProperty("parameters", out var parameters) ||
            !parameters.TryGetProperty("NotificationDefinition/notificationText", out var textEl))
            return Results.BadRequest("Missing NotificationDefinition/notificationText");

        notificationText = textEl.GetString();
    }

    if (string.IsNullOrWhiteSpace(notificationText))
        return Results.BadRequest("Empty notificationText");

    var notification = NotificationParser.Parse(notificationText);
    if (notification is null)
    {
        logger.LogWarning("Could not parse booking notification: {Text}", notificationText);
        return Results.BadRequest("Could not parse notification text");
    }

    notificationStore.Add(notification);
    logger.LogInformation(
        "Received booking notification for appointment {BookingId} ({StaffCount} staff)",
        notification.BookingId, notification.StaffMembers.Count);

    return Results.Ok();
}).DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string CsvCell(string value) =>
    value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
        ? $"\"{value.Replace("\"", "\"\"")}\""
        : value;
