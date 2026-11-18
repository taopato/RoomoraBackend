using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;

namespace EvArkadasim.API.Controllers;

public sealed class CreateScheduledChargeRequest
{
    public int HouseId { get; set; }
    public string? Title { get; set; }
    public string? Note { get; set; }
    public ChargeType Type { get; set; }
    public int PayerUserId { get; set; }
    public decimal FixedAmount { get; set; }
    public int CollectionStartDay { get; set; } = 10;
    public int DueDay { get; set; } = 15;
    public string? StartMonth { get; set; }
    public List<int> ParticipantUserIds { get; set; } = new();
}

public sealed class SetSharePaidRequest
{
    public bool IsPaid { get; set; }
}

public sealed class SetExternalPaidRequest
{
    public bool IsPaid { get; set; }
}

public sealed class ScheduledDueItemDto
{
    public int PlanId { get; set; }
    public int CycleId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public int DueDay { get; set; }
    public int CollectionStartDay { get; set; }
    public int PayerUserId { get; set; }
    public string PayerName { get; set; } = string.Empty;
}

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RecurringChargesController : ControllerBase
{
    private static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);
    private readonly AppDbContext _db;

    public RecurringChargesController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateScheduledChargeRequest request, CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null) return Unauthorized();
        if (!await CanAccessHouseAsync(request.HouseId, currentUserId.Value, ct)) return Forbid();

        if (request.FixedAmount <= 0)
            return BadRequest(new { message = "Aylık tutar sıfırdan büyük olmalıdır." });
        if (request.DueDay is < 1 or > 28)
            return BadRequest(new { message = "Son ödeme günü 1-28 arasında olmalıdır." });
        var activeMembers = await _db.HouseMembers
            .AsNoTracking()
            .Where(x => x.HouseId == request.HouseId && x.IsActive)
            .Select(x => x.UserId)
            .OrderBy(x => x)
            .ToListAsync(ct);

        if (!activeMembers.Contains(request.PayerUserId))
            return BadRequest(new { message = "Ödeme sorumlusu aktif bir ev üyesi olmalıdır." });

        var participants = (request.ParticipantUserIds.Count > 0
                ? request.ParticipantUserIds.Intersect(activeMembers)
                : activeMembers)
            .Append(request.PayerUserId)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (participants.Count < 2)
            return BadRequest(new { message = "Dönemsel ödeme için en az iki katılımcı gereklidir." });

        var now = TurkeyNow();
        var startMonth = NormalizeStartMonth(request.StartMonth, now);
        var plan = new RecurringCharge
        {
            HouseId = request.HouseId,
            Type = request.Type,
            Title = string.IsNullOrWhiteSpace(request.Title) ? TypeLabel(request.Type) : request.Title.Trim(),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            PayerUserId = request.PayerUserId,
            CreatedByUserId = currentUserId.Value,
            AmountMode = AmountMode.Fixed,
            FixedAmount = decimal.Round(request.FixedAmount, 2),
            SplitPolicy = SplitPolicy.Equal,
            ParticipantsJson = JsonSerializer.Serialize(participants),
            DueDay = request.DueDay,
            CollectionStartDay = Math.Max(1, request.DueDay - 5),
            PaymentWindowDays = Math.Min(5, request.DueDay - 1),
            AccountingMode = RecurringAccountingMode.ScheduledCollection,
            StartMonth = startMonth,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        _db.RecurringCharges.Add(plan);
        await _db.SaveChangesAsync(ct);

        var cycle = await EnsureCurrentCycleAsync(plan, now, ct);
        var names = await GetUserNamesAsync(request.HouseId, ct);
        return Ok(new { isSuccess = true, data = BuildPlanDto(plan, cycle, names, currentUserId.Value, now) });
    }

    [HttpGet("house/{houseId:int}")]
    public async Task<IActionResult> GetHousePlans(int houseId, CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null) return Unauthorized();
        if (!await CanAccessHouseAsync(houseId, currentUserId.Value, ct)) return Forbid();

        var now = TurkeyNow();
        var plans = await _db.RecurringCharges
            .Where(x => x.HouseId == houseId && x.IsActive && x.AccountingMode == RecurringAccountingMode.ScheduledCollection)
            .OrderBy(x => x.DueDay)
            .ThenBy(x => x.Title)
            .ToListAsync(ct);

        var cycles = new Dictionary<int, ChargeCycle?>();
        foreach (var plan in plans)
            cycles[plan.Id] = await EnsureCurrentCycleAsync(plan, now, ct);

        var names = await GetUserNamesAsync(houseId, ct);
        var data = plans.Select(plan => BuildPlanDto(plan, cycles[plan.Id], names, currentUserId.Value, now)).ToList();
        return Ok(new { isSuccess = true, data });
    }

    [HttpGet("house/{houseId:int}/my-due")]
    public async Task<IActionResult> GetMyDue(int houseId, CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null) return Unauthorized();
        if (!await CanAccessHouseAsync(houseId, currentUserId.Value, ct)) return Forbid();

        var now = TurkeyNow();
        var plans = await _db.RecurringCharges
            .Where(x => x.HouseId == houseId && x.IsActive && x.AccountingMode == RecurringAccountingMode.ScheduledCollection)
            .ToListAsync(ct);

        foreach (var plan in plans)
            await EnsureCurrentCycleAsync(plan, now, ct);

        var planIds = plans.Select(x => x.Id).ToList();
        var cycles = await _db.ChargeCycles
            .Include(x => x.Contract)
            .Include(x => x.Shares)
            .Where(x => planIds.Contains(x.ContractId))
            .ToListAsync(ct);

        var names = await GetUserNamesAsync(houseId, ct);
        var items = new List<ScheduledDueItemDto>();
        foreach (var cycle in cycles)
        {
            var plan = cycle.Contract;
            var canonicalStartDay = Math.Max(1, (plan.DueDay ?? 1) - 5);
            if (currentUserId.Value == plan.PayerUserId || !TryGetCollectionStart(cycle.Period, canonicalStartDay, out var collectionStart))
                continue;
            if (DateOnly.FromDateTime(now.DateTime) < collectionStart) continue;
            var dueDate = new DateOnly(collectionStart.Year, collectionStart.Month, plan.DueDay ?? 28);
            if (DateOnly.FromDateTime(now.DateTime) > dueDate) continue;

            var share = cycle.Shares.FirstOrDefault(x => x.UserId == currentUserId.Value);
            if (share is null || share.Status == ChargeCycleShareStatus.Paid)
                continue;

            items.Add(new ScheduledDueItemDto
            {
                PlanId = plan.Id,
                CycleId = cycle.Id,
                Title = plan.Title,
                Type = plan.Type.ToString(),
                Period = cycle.Period,
                Amount = share.Amount,
                Status = share.Status.ToString(),
                DueDay = plan.DueDay ?? 28,
                CollectionStartDay = canonicalStartDay,
                PayerUserId = plan.PayerUserId,
                PayerName = names.GetValueOrDefault(plan.PayerUserId, "Ödeme sorumlusu"),
            });
        }

        await _db.SaveChangesAsync(ct);
        var total = items.Sum(x => x.Amount);
        return Ok(new { isSuccess = true, data = new { total, items } });
    }

    [HttpPut("cycles/{cycleId:int}/shares/{userId:int}")]
    public async Task<IActionResult> SetSharePaid(int cycleId, int userId, SetSharePaidRequest request, CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null) return Unauthorized();

        var cycle = await LoadCycleAsync(cycleId, ct);
        if (cycle is null) return NotFound(new { message = "Dönem bulunamadı." });
        if (cycle.Contract.PayerUserId != currentUserId.Value) return Forbid();
        if (userId == cycle.Contract.PayerUserId)
            return BadRequest(new { message = "Ödeme sorumlusunun kendi payı otomatik tamamlanır." });

        var share = cycle.Shares.FirstOrDefault(x => x.UserId == userId);
        if (share is null) return NotFound(new { message = "Katılımcı payı bulunamadı." });

        share.Status = request.IsPaid ? ChargeCycleShareStatus.Paid : UnpaidStatus(cycle.Contract, TurkeyNow());
        share.PaidDate = request.IsPaid ? DateTime.UtcNow : null;
        share.ConfirmedByUserId = request.IsPaid ? currentUserId.Value : null;
        share.UpdatedAt = DateTime.UtcNow;
        RefreshCycleStatus(cycle, TurkeyNow());
        await _db.SaveChangesAsync(ct);

        var names = await GetUserNamesAsync(cycle.Contract.HouseId, ct);
        return Ok(new { isSuccess = true, data = BuildPlanDto(cycle.Contract, cycle, names, currentUserId.Value, TurkeyNow()) });
    }

    [HttpPut("cycles/{cycleId:int}/external-payment")]
    public async Task<IActionResult> SetExternalPaid(int cycleId, SetExternalPaidRequest request, CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null) return Unauthorized();

        var cycle = await LoadCycleAsync(cycleId, ct);
        if (cycle is null) return NotFound(new { message = "Dönem bulunamadı." });
        if (cycle.Contract.PayerUserId != currentUserId.Value) return Forbid();

        cycle.PaidDate = request.IsPaid ? DateTime.UtcNow : null;
        cycle.PaidByUserId = request.IsPaid ? currentUserId.Value : null;
        cycle.UpdatedAt = DateTime.UtcNow;
        RefreshCycleStatus(cycle, TurkeyNow());
        await _db.SaveChangesAsync(ct);

        var names = await GetUserNamesAsync(cycle.Contract.HouseId, ct);
        return Ok(new { isSuccess = true, data = BuildPlanDto(cycle.Contract, cycle, names, currentUserId.Value, TurkeyNow()) });
    }

    [HttpDelete("{planId:int}")]
    public async Task<IActionResult> Deactivate(int planId, CancellationToken ct)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId is null) return Unauthorized();

        var plan = await _db.RecurringCharges.FirstOrDefaultAsync(x => x.Id == planId && x.IsActive, ct);
        if (plan is null) return NotFound(new { message = "Plan bulunamadı." });
        if (plan.PayerUserId != currentUserId.Value && plan.CreatedByUserId != currentUserId.Value) return Forbid();

        plan.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return Ok(new { isSuccess = true });
    }

    private async Task<ChargeCycle?> EnsureCurrentCycleAsync(RecurringCharge plan, DateTimeOffset now, CancellationToken ct)
    {
        var period = $"{now:yyyy-MM}";
        if (string.CompareOrdinal(plan.StartMonth, period) > 0) return null;

        var cycle = await _db.ChargeCycles
            .Include(x => x.Shares)
            .FirstOrDefaultAsync(x => x.ContractId == plan.Id && x.Period == period, ct);

        if (cycle is null)
        {
            var participants = ParseParticipants(plan.ParticipantsJson);
            if (participants.Count == 0)
            {
                participants = await _db.HouseMembers
                    .AsNoTracking()
                    .Where(x => x.HouseId == plan.HouseId && x.IsActive)
                    .Select(x => x.UserId)
                    .OrderBy(x => x)
                    .ToListAsync(ct);
            }

            participants = participants.Append(plan.PayerUserId).Distinct().OrderBy(x => x).ToList();
            var shares = BuildEqualShares(plan.FixedAmount ?? 0m, participants);
            var dueLocal = new DateTimeOffset(now.Year, now.Month, Math.Clamp(plan.DueDay ?? 1, 1, 28), 0, 0, 0, TurkeyOffset);
            cycle = new ChargeCycle
            {
                ContractId = plan.Id,
                Contract = plan,
                Period = period,
                Status = ChargeCycleStatus.Open,
                TotalAmount = plan.FixedAmount ?? 0m,
                DueDate = dueLocal.UtcDateTime,
                UpdatedAt = DateTime.UtcNow,
                Shares = shares.Select(x => new ChargeCycleShare
                {
                    UserId = x.UserId,
                    Amount = x.Amount,
                    Status = x.UserId == plan.PayerUserId ? ChargeCycleShareStatus.Paid : ChargeCycleShareStatus.Unpaid,
                    PaidDate = x.UserId == plan.PayerUserId ? DateTime.UtcNow : null,
                    ConfirmedByUserId = x.UserId == plan.PayerUserId ? plan.PayerUserId : null,
                    UpdatedAt = DateTime.UtcNow,
                }).ToList(),
            };
            _db.ChargeCycles.Add(cycle);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Aynı dönemi eş zamanlı başka bir istek oluşturdu; başarısız kayıt bırakılıp onunki kullanılır
                foreach (var share in cycle.Shares)
                    _db.Entry(share).State = EntityState.Detached;
                _db.Entry(cycle).State = EntityState.Detached;
                plan.Cycles.Remove(cycle);
                cycle = await _db.ChargeCycles
                    .Include(x => x.Shares)
                    .FirstAsync(x => x.ContractId == plan.Id && x.Period == period, ct);
            }
        }

        cycle.Contract = plan;
        RefreshCycleStatus(cycle, now);
        await _db.SaveChangesAsync(ct);
        return cycle;
    }

    private static void RefreshCycleStatus(ChargeCycle cycle, DateTimeOffset now)
    {
        foreach (var share in cycle.Shares.Where(x => x.UserId != cycle.Contract.PayerUserId && x.Status != ChargeCycleShareStatus.Paid))
        {
            share.Status = UnpaidStatus(cycle.Contract, now);
            share.UpdatedAt = DateTime.UtcNow;
        }

        cycle.FundedAmount = cycle.Shares.Where(x => x.Status == ChargeCycleShareStatus.Paid).Sum(x => x.Amount);
        cycle.Status = cycle.PaidDate.HasValue
            ? ChargeCycleStatus.Paid
            : cycle.Shares.All(x => x.Status == ChargeCycleShareStatus.Paid)
                ? ChargeCycleStatus.Funded
                : now.Day > (cycle.Contract.DueDay ?? 28)
                    ? ChargeCycleStatus.Overdue
                    : now.Day >= cycle.Contract.CollectionStartDay
                        ? ChargeCycleStatus.Collecting
                        : ChargeCycleStatus.Open;
        cycle.UpdatedAt = DateTime.UtcNow;
    }

    private static ChargeCycleShareStatus UnpaidStatus(RecurringCharge plan, DateTimeOffset now)
        => now.Day > (plan.DueDay ?? 28) ? ChargeCycleShareStatus.Overdue : ChargeCycleShareStatus.Unpaid;

    private async Task<ChargeCycle?> LoadCycleAsync(int cycleId, CancellationToken ct)
        => await _db.ChargeCycles
            .Include(x => x.Contract)
            .Include(x => x.Shares)
            .FirstOrDefaultAsync(x => x.Id == cycleId && x.Contract.IsActive, ct);

    private static object BuildPlanDto(
        RecurringCharge plan,
        ChargeCycle? cycle,
        IReadOnlyDictionary<int, string> names,
        int currentUserId,
        DateTimeOffset now)
    {
        object? cycleDto = null;
        if (cycle is not null)
        {
            var shares = cycle.Shares
                .OrderByDescending(x => x.UserId == plan.PayerUserId)
                .ThenBy(x => names.GetValueOrDefault(x.UserId, string.Empty))
                .Select(x => new
                {
                    x.UserId,
                    userName = names.GetValueOrDefault(x.UserId, $"Kullanıcı #{x.UserId}"),
                    x.Amount,
                    status = x.Status.ToString(),
                    x.PaidDate,
                    isPayer = x.UserId == plan.PayerUserId,
                })
                .ToList();

            cycleDto = new
            {
                cycle.Id,
                cycle.Period,
                status = cycle.Status.ToString(),
                cycle.TotalAmount,
                cycle.FundedAmount,
                externalPaid = cycle.PaidDate.HasValue,
                cycle.PaidDate,
                isCollectionOpen = now.Day >= Math.Max(1, (plan.DueDay ?? 1) - 5)
                    && now.Day <= (plan.DueDay ?? 28),
                isOverdue = now.Day > (plan.DueDay ?? 28) && !cycle.PaidDate.HasValue,
                paidCount = shares.Count(x => x.status == ChargeCycleShareStatus.Paid.ToString()),
                totalShareCount = shares.Count,
                outstandingAmount = cycle.Shares
                    .Where(x => x.UserId != plan.PayerUserId && x.Status != ChargeCycleShareStatus.Paid)
                    .Sum(x => x.Amount),
                shares,
            };
        }

        return new
        {
            plan.Id,
            plan.HouseId,
            plan.Title,
            plan.Note,
            type = plan.Type.ToString(),
            fixedAmount = plan.FixedAmount ?? 0m,
            plan.PayerUserId,
            payerName = names.GetValueOrDefault(plan.PayerUserId, "Ödeme sorumlusu"),
            dueDay = plan.DueDay ?? 1,
            collectionStartDay = Math.Max(1, (plan.DueDay ?? 1) - 5),
            plan.StartMonth,
            isPayer = currentUserId == plan.PayerUserId,
            cycle = cycleDto,
        };
    }

    private static List<(int UserId, decimal Amount)> BuildEqualShares(decimal total, List<int> participants)
    {
        var result = new List<(int UserId, decimal Amount)>();
        if (total <= 0 || participants.Count == 0) return result;

        var amount = decimal.Round(total / participants.Count, 2, MidpointRounding.AwayFromZero);
        var difference = decimal.Round(total - (amount * participants.Count), 2);
        for (var index = 0; index < participants.Count; index++)
            result.Add((participants[index], amount + (index == 0 ? difference : 0m)));
        return result;
    }

    private static List<int> ParseParticipants(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? new List<int>()
                : JsonSerializer.Deserialize<List<int>>(json)?.Where(x => x > 0).Distinct().ToList() ?? new List<int>();
        }
        catch
        {
            return new List<int>();
        }
    }

    private static bool TryGetCollectionStart(string period, int startDay, out DateOnly result)
    {
        if (DateTime.TryParseExact(period, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            result = new DateOnly(parsed.Year, parsed.Month, Math.Clamp(startDay, 1, 28));
            return true;
        }

        result = default;
        return false;
    }

    private async Task<Dictionary<int, string>> GetUserNamesAsync(int houseId, CancellationToken ct)
        => await _db.HouseMembers
            .AsNoTracking()
            .Where(x => x.HouseId == houseId && x.IsActive)
            .Select(x => new { x.UserId, Name = (x.User.FirstName + " " + x.User.LastName).Trim() })
            .ToDictionaryAsync(x => x.UserId, x => x.Name, ct);

    private async Task<bool> CanAccessHouseAsync(int houseId, int userId, CancellationToken ct)
        => await _db.HouseMembers.AsNoTracking()
            .AnyAsync(x => x.HouseId == houseId && x.UserId == userId && x.IsActive, ct);

    private int? GetCurrentUserId()
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return int.TryParse(raw, out var id) && id > 0 ? id : null;
    }

    private static DateTimeOffset TurkeyNow() => DateTimeOffset.UtcNow.ToOffset(TurkeyOffset);

    private static string NormalizeStartMonth(string? value, DateTimeOffset fallback)
        => DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM")
            : fallback.ToString("yyyy-MM");

    private static string TypeLabel(ChargeType type) => type switch
    {
        ChargeType.Rent => "Kira",
        ChargeType.Internet => "İnternet",
        ChargeType.Electric => "Elektrik",
        ChargeType.Water => "Su",
        ChargeType.Gas => "Doğalgaz",
        _ => "Düzenli ödeme",
    };
}
