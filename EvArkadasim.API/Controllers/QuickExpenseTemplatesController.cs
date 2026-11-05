using System.Security.Claims;
using System.Text.Json;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;
using Application.Features.Expenses;
using Application.Features.Expenses.Dtos;

namespace EvArkadasim.API.Controllers;

public sealed class QuickExpenseTemplateRequest
{
    public int HouseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int CategoryId { get; set; }
    public string? Description { get; set; }
    public int PayerUserId { get; set; }
    public List<int> ParticipantUserIds { get; set; } = new();
    public Dictionary<int, decimal> PersonalAmounts { get; set; } = new();
    public List<ShareUnitDto>? ShareUnits { get; set; }
}

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class QuickExpenseTemplatesController : ControllerBase
{
    private readonly AppDbContext _db;

    public QuickExpenseTemplatesController(AppDbContext db) => _db = db;

    [HttpGet("house/{houseId:int}")]
    public async Task<IActionResult> List(int houseId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        if (!await IsMember(houseId, userId.Value, ct)) return Forbid();

        var templates = await _db.QuickExpenseTemplates.AsNoTracking()
            .Where(x => x.OwnerUserId == userId && x.HouseId == houseId && !x.IsArchived)
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync(ct);

        return Ok(templates.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create(QuickExpenseTemplateRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var validation = await Validate(request, userId.Value, ct);
        if (validation is not null) return validation;

        var entity = new QuickExpenseTemplate { OwnerUserId = userId.Value, CreatedAt = DateTime.UtcNow };
        Apply(entity, request);
        _db.QuickExpenseTemplates.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, QuickExpenseTemplateRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var entity = await _db.QuickExpenseTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == userId && !x.IsArchived, ct);
        if (entity is null) return NotFound(new { message = "Hizli harcama bulunamadi." });
        if (entity.HouseId != request.HouseId) return BadRequest(new { message = "Grup degistirilemez." });
        var validation = await Validate(request, userId.Value, ct);
        if (validation is not null) return validation;

        Apply(entity, request);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Archive(int id, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Unauthorized();
        var entity = await _db.QuickExpenseTemplates
            .FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == userId && !x.IsArchived, ct);
        if (entity is null) return NotFound();
        entity.IsArchived = true;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<IActionResult?> Validate(QuickExpenseTemplateRequest request, int userId, CancellationToken ct)
    {
        if (!await IsMember(request.HouseId, userId, ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80)
            return BadRequest(new { message = "Hizli harcama adi zorunludur." });
        if (request.Amount <= 0) return BadRequest(new { message = "Tutar sifirdan buyuk olmalidir." });
        if (!Enum.IsDefined((ExpenseCategory)request.CategoryId))
            return BadRequest(new { message = "Gecersiz harcama kategorisi." });

        var activeIds = await _db.HouseMembers.AsNoTracking()
            .Where(x => x.HouseId == request.HouseId && x.IsActive)
            .Select(x => x.UserId).ToListAsync(ct);
        if (!activeIds.Contains(request.PayerUserId))
            return BadRequest(new { message = "Odeyen kisi aktif bir grup uyesi olmalidir." });
        var participantIds = (request.ParticipantUserIds ?? new()).Distinct().ToList();
        if (participantIds.Count == 0 || participantIds.Any(x => !activeIds.Contains(x)))
            return BadRequest(new { message = "En az bir gecerli katilimci secilmelidir." });
        var personalAmounts = request.PersonalAmounts ?? new();
        if (personalAmounts.Any(x => !participantIds.Contains(x.Key) || x.Value < 0))
            return BadRequest(new { message = "Kisisel paylar katilimcilarla uyumlu degil." });
        if (personalAmounts.Values.Sum() > request.Amount)
            return BadRequest(new { message = "Kisisel paylar toplam tutari gecemez." });
        try { ShareUnitAllocation.Validate(request.ShareUnits, participantIds); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        return null;
    }

    private static void Apply(QuickExpenseTemplate entity, QuickExpenseTemplateRequest request)
    {
        entity.HouseId = request.HouseId;
        entity.Name = request.Name.Trim();
        entity.Amount = decimal.Round(request.Amount, 2);
        entity.CategoryId = request.CategoryId;
        entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        entity.PayerUserId = request.PayerUserId;
        entity.ParticipantUserIdsJson = JsonSerializer.Serialize((request.ParticipantUserIds ?? new()).Distinct().OrderBy(x => x));
        entity.PersonalAmountsJson = JsonSerializer.Serialize(request.PersonalAmounts ?? new());
        if (request.ShareUnits is not null)
            entity.ShareUnitsJson = JsonSerializer.Serialize(ShareUnitAllocation.Validate(
                request.ShareUnits, (request.ParticipantUserIds ?? new()).Distinct().ToList()) ?? new());
        else if (ShareUnitAllocation.ReadStored(entity.ShareUnitsJson)
            .Select(unit => unit.UserId).OrderBy(id => id)
            .SequenceEqual((request.ParticipantUserIds ?? new()).Distinct().OrderBy(id => id)) == false)
            entity.ShareUnitsJson = null;
        entity.UpdatedAt = DateTime.UtcNow;
    }

    private static object ToDto(QuickExpenseTemplate entity) => new
    {
        entity.Id,
        entity.HouseId,
        entity.Name,
        entity.Amount,
        entity.CategoryId,
        entity.Description,
        entity.PayerUserId,
        participantUserIds = Deserialize<List<int>>(entity.ParticipantUserIdsJson) ?? new(),
        personalAmounts = Deserialize<Dictionary<int, decimal>>(entity.PersonalAmountsJson) ?? new(),
        shareUnits = ShareUnitAllocation.ReadStored(entity.ShareUnitsJson),
        entity.UpdatedAt,
    };

    private static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json); }
        catch { return default; }
    }

    private Task<bool> IsMember(int houseId, int userId, CancellationToken ct) =>
        _db.HouseMembers.AsNoTracking().AnyAsync(x => x.HouseId == houseId && x.UserId == userId && x.IsActive, ct);

    private int? CurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return int.TryParse(raw, out var id) && id > 0 ? id : null;
    }
}
