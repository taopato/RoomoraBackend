using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;

namespace EvArkadasim.API.Services.Notifications;

public sealed class ExpoPushNotificationService : IPushNotificationService
{
    private const string SendUrl = "https://exp.host/--/api/v2/push/send";
    private const string ReceiptsUrl = "https://exp.host/--/api/v2/push/getReceipts";
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

            using var response = await _http.PostAsJsonAsync(SendUrl, messages, ct);
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

    public async Task<IReadOnlyList<PushTestResult>> SendTestAsync(int userId, CancellationToken ct = default)
    {
        var devices = await _db.PushDevices
            .Where(x => x.UserId == userId && x.IsActive)
            .OrderByDescending(x => x.LastSeenAt)
            .ToListAsync(ct);
        if (devices.Count == 0) return Array.Empty<PushTestResult>();

        var messages = devices.Select(device => new
        {
            to = device.ExpoPushToken,
            sound = "default",
            title = device.LanguageCode == "en" ? "Roomora test notification" : "Roomora test bildirimi",
            body = device.LanguageCode == "en" ? "Phone notifications are working." : "Telefon bildirimleri çalışıyor.",
            data = new { route = "Bildirimler" },
            priority = "high",
        }).ToArray();

        PushTestResult Result(int index, string status, string? error = null)
            => new(devices[index].Platform, devices[index].DeviceName, devices[index].LastSeenAt, status, error);

        using var response = await _http.PostAsJsonAsync(SendUrl, messages, ct);
        if (!response.IsSuccessStatusCode)
            return devices.Select((_, index) => Result(index, "error", $"HTTP {(int)response.StatusCode}")).ToList();

        var results = new PushTestResult?[devices.Count];
        var ticketIds = new Dictionary<string, int>();
        using (var sent = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct))
        {
            if (sent.RootElement.TryGetProperty("data", out var tickets) && tickets.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var ticket in tickets.EnumerateArray())
                {
                    if (index >= devices.Count) break;
                    if (ticket.TryGetProperty("status", out var status) && status.GetString() == "ok"
                        && ticket.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } ticketId)
                        ticketIds[ticketId] = index;
                    else
                        results[index] = Result(index, "error", ExpoErrorCode(ticket));
                    index++;
                }
            }
        }

        if (ticketIds.Count > 0)
        {
            // Apple/Google'ın cevabı Expo'ya birkaç saniyede düşer
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            using var receiptResponse = await _http.PostAsJsonAsync(ReceiptsUrl, new { ids = ticketIds.Keys.ToArray() }, ct);
            if (receiptResponse.IsSuccessStatusCode)
            {
                using var receipts = await JsonDocument.ParseAsync(await receiptResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (receipts.RootElement.TryGetProperty("data", out var byId) && byId.ValueKind == JsonValueKind.Object)
                {
                    foreach (var (ticketId, index) in ticketIds)
                    {
                        if (!byId.TryGetProperty(ticketId, out var receipt)) continue;
                        results[index] = receipt.TryGetProperty("status", out var status) && status.GetString() == "ok"
                            ? Result(index, "delivered")
                            : Result(index, "error", ExpoErrorCode(receipt));
                    }
                }
            }
        }

        var retired = false;
        for (var index = 0; index < devices.Count; index++)
        {
            results[index] ??= ticketIds.ContainsValue(index) ? Result(index, "sent") : Result(index, "error", "NoTicket");
            if (results[index]!.Error == "DeviceNotRegistered")
            {
                devices[index].IsActive = false;
                retired = true;
            }
            if (results[index]!.Status == "error")
                _logger.LogWarning("Test push failed with {Error} for user {UserId} on {Platform}", results[index]!.Error, userId, devices[index].Platform);
        }
        if (retired) await _db.SaveChangesAsync(ct);
        return results.Select(result => result!).ToList();
    }

    private static string ExpoErrorCode(JsonElement ticketOrReceipt)
    {
        if (ticketOrReceipt.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } code)
            return code;
        return ticketOrReceipt.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text
            ? text[..Math.Min(text.Length, 160)]
            : "Unknown";
    }
}
