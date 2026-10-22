using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;

namespace EvArkadasim.API.Services.Notifications;

public sealed class ExpoPushNotificationService : IPushNotificationService
{
    private readonly HttpClient _http;
    private readonly AppDbContext _db;
    private readonly ILogger<ExpoPushNotificationService> _logger;

    public ExpoPushNotificationService(HttpClient http, AppDbContext db, ILogger<ExpoPushNotificationService> logger)
    {
        _http = http;
        _db = db;
        _logger = logger;
    }

    public async Task SendAsync(
        int userId,
        string category,
        string title,
        string body,
        string titleEn,
        string bodyEn,
        object? data = null,
        CancellationToken ct = default)
    {
        var devices = await _db.PushDevices
            .Where(x => x.UserId == userId && x.IsActive)
            .Where(x => category == "payment" ? x.PaymentEnabled : category == "bill" ? x.BillEnabled : x.GroupEnabled)
            .ToListAsync(ct);
        if (devices.Count == 0) return;

        var messages = devices.Select(device => new
        {
            to = device.ExpoPushToken,
            sound = "default",
            title = device.LanguageCode == "en" ? titleEn : title,
            body = device.LanguageCode == "en" ? bodyEn : body,
            data = data ?? new { },
            priority = "high",
        }).ToArray();

        try
        {
            using var response = await _http.PostAsJsonAsync("https://exp.host/--/api/v2/push/send", messages, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Expo push failed with {StatusCode}", response.StatusCode);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Expo push delivery failed for user {UserId}", userId);
        }
    }
}
