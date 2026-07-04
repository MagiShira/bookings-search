using System.Collections.Concurrent;
using BookingsSearch.Models;

namespace BookingsSearch.Services;

public sealed class NotificationStore
{
    private readonly ConcurrentDictionary<string, BookingNotification> _store =
        new(StringComparer.OrdinalIgnoreCase);

    public void Add(BookingNotification notification) =>
        _store[notification.BookingId] = notification;

    public BookingNotification? GetByBookingId(string bookingId) =>
        _store.TryGetValue(bookingId, out var n) ? n : null;
}
