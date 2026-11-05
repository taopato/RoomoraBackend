using System.Net.Http.Json;
using System.Text.Json;
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
        try
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

            using var response = await _http.PostAsJsonAsync("https://exp.host/--/api/v2/push/send", messages, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Expo push failed with {StatusCode}", response.StatusCode);
                return;
            }

            using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!result.RootElement.TryGetProperty("data", out var tickets) || tickets.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Expo push returned no tickets for user {UserId}", userId);
                return;
            }
            var index = 0;
            var retiredDevice = false;
            foreach (var ticket in tickets.EnumerateArray())
            {
                if (index >= devices.Count) break;
                if (ticket.TryGetProperty("status", out var status) && status.GetString() == "error")
                {
                    var error = ticket.TryGetProperty("details", out var details)
                        && details.ValueKind == JsonValueKind.Object
                        && details.TryGetProperty("error", out var errorCode)
                        ? errorCode.GetString() : "Unknown";
                    _logger.LogWarning("Expo push ticket failed with {Error} for user {UserId}", error, userId);
                    if (error == "DeviceNotRegistered")
                    {
                        devices[index].IsActive = false;
                        retiredDevice = true;
                    }
                }
                index++;
            }
            if (retiredDevice) await _db.SaveChangesAsync(ct);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Expo push delivery failed for user {UserId}", userId);
        }
    }
}
