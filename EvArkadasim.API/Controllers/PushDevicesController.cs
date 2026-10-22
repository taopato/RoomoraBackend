using System.Security.Claims;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;

namespace EvArkadasim.API.Controllers;

public sealed class RegisterPushDeviceRequest
{
    public string ExpoPushToken { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = "tr";
    public string? DeviceName { get; set; }
    public bool PaymentEnabled { get; set; } = true;
    public bool BillEnabled { get; set; } = true;
    public bool GroupEnabled { get; set; } = true;
}

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PushDevicesController : ControllerBase
{
    private readonly AppDbContext _db;
    public PushDevicesController(AppDbContext db) => _db = db;

    [HttpPost]
    public async Task<IActionResult> Register(RegisterPushDeviceRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        if (!request.ExpoPushToken.StartsWith("ExponentPushToken[", StringComparison.Ordinal)
            && !request.ExpoPushToken.StartsWith("ExpoPushToken[", StringComparison.Ordinal))
            return BadRequest(new { message = "Gecersiz bildirim anahtari." });

        var device = await _db.PushDevices.FirstOrDefaultAsync(x => x.ExpoPushToken == request.ExpoPushToken, ct);
        if (device is null)
        {
            device = new PushDevice { ExpoPushToken = request.ExpoPushToken, CreatedAt = DateTime.UtcNow };
            _db.PushDevices.Add(device);
        }
        device.UserId = userId.Value;
        device.Platform = string.Equals(request.Platform, "ios", StringComparison.OrdinalIgnoreCase) ? "ios" : "android";
        device.LanguageCode = string.Equals(request.LanguageCode, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "tr";
        device.DeviceName = request.DeviceName?.Trim();
        device.PaymentEnabled = request.PaymentEnabled;
        device.BillEnabled = request.BillEnabled;
        device.GroupEnabled = request.GroupEnabled;
        device.IsActive = true;
        device.LastSeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { device.Id });
    }

    [HttpDelete]
    public async Task<IActionResult> Unregister([FromQuery] string token, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var device = await _db.PushDevices.FirstOrDefaultAsync(x => x.UserId == userId && x.ExpoPushToken == token, ct);
        if (device is not null)
        {
            device.IsActive = false;
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    private int? CurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return int.TryParse(raw, out var id) && id > 0 ? id : null;
    }
}
