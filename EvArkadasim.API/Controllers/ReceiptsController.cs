using Application.Features.Expenses.Commands.CreateExpense;
using Application.Features.Expenses.Commands.UpdateExpense;
using Application.Features.Expenses.Dtos;
using Domain.Entities;
using Domain.Enums;
using EvArkadasim.API.Services.Receipts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;
using System.Security.Claims;
using System.Text.Json;

namespace EvArkadasim.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ReceiptsController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly IWebHostEnvironment _environment;
        private readonly IReceiptOcrService _ocrService;
        private readonly IMediator _mediator;

        public ReceiptsController(
            AppDbContext dbContext,
            IWebHostEnvironment environment,
            IReceiptOcrService ocrService,
            IMediator mediator)
        {
            _dbContext = dbContext;
            _environment = environment;
            _ocrService = ocrService;
            _mediator = mediator;
        }

        [HttpPost("Scan")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(20_000_000)]
        public async Task<IActionResult> Scan([FromForm] ReceiptScanRequest request, CancellationToken cancellationToken)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            if (request.HouseId <= 0 || !await IsActiveHouseMemberAsync(request.HouseId, currentUserId.Value, cancellationToken))
                return Forbid();
            if (request.Image == null || request.Image.Length == 0)
                return BadRequest(new { message = "Fiş görseli zorunludur." });
            if (request.Image.Length > 12_000_000)
                return BadRequest(new { message = "Fiş görseli en fazla 12 MB olabilir." });

            var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/jpeg", "image/png", "image/webp"
            };
            if (!allowedTypes.Contains(request.Image.ContentType))
                return BadRequest(new { message = "Fiş için JPG, PNG veya WEBP formatında bir görsel yükleyin." });
            if (!await HasValidImageSignatureAsync(request.Image, cancellationToken))
                return BadRequest(new { message = "Fiş görselinin dosya içeriği geçerli değil." });

            var turkeyToday = DateTime.UtcNow.AddHours(3).Date;
            var uploadsRoot = Path.Combine(_environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"), "uploads", "receipts");
            Directory.CreateDirectory(uploadsRoot);

            var safeExtension = request.Image.ContentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => ".jpg"
            };
            var fileName = $"{request.HouseId}_{currentUserId.Value}_{turkeyToday:yyyyMMdd}_{Guid.NewGuid():N}{safeExtension}";
            var fullPath = Path.Combine(uploadsRoot, fileName);

            try
            {
                await using (var target = System.IO.File.Create(fullPath))
                {
                    await request.Image.CopyToAsync(target, cancellationToken);
                }

                ReceiptOcrResult ocrResult;
                await using (var readStream = request.Image.OpenReadStream())
                {
                    ocrResult = await _ocrService.ExtractAsync(readStream, request.Image.FileName, cancellationToken);
                }

                var receipt = new Receipt
                {
                    HouseId = request.HouseId,
                    UploadedByUserId = currentUserId.Value,
                    ImageUrl = $"/uploads/receipts/{fileName}",
                    CreatedAt = DateTime.UtcNow,
                };
                ApplyOcrResult(receipt, ocrResult, turkeyToday);

                _dbContext.Receipts.Add(receipt);
                await _dbContext.SaveChangesAsync(cancellationToken);

                return Ok(await BuildReceiptDetailAsync(receipt.Id, cancellationToken));
            }
            catch
            {
                if (System.IO.File.Exists(fullPath))
                    System.IO.File.Delete(fullPath);
                throw;
            }
        }

        [HttpGet("ByHouse/{houseId:int}")]
        public async Task<IActionResult> ByHouse(int houseId, CancellationToken cancellationToken)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            if (!await IsActiveHouseMemberAsync(houseId, currentUserId.Value, cancellationToken))
                return Forbid();

            var receipts = await _dbContext.Receipts
                .AsNoTracking()
                .Where(x => x.HouseId == houseId)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new
                {
                    x.Id,
                    x.HouseId,
                    x.UploadedByUserId,
                    x.ImageUrl,
                    x.StoreName,
                    x.ReceiptDate,
                    x.DetectedTotalAmount,
                    x.Status,
                    x.CreatedAt,
                    ItemCount = x.Items.Count,
                    x.ConvertedExpenseId
                })
                .ToListAsync(cancellationToken);

            return Ok(receipts);
        }

        [HttpGet("{receiptId:int}")]
        public async Task<IActionResult> Get(int receiptId, CancellationToken cancellationToken)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            var houseId = await GetReceiptHouseIdAsync(receiptId, cancellationToken);
            if (houseId is null) return NotFound();
            if (!await IsActiveHouseMemberAsync(houseId.Value, currentUserId.Value, cancellationToken))
                return Forbid();

            var receipt = await BuildReceiptDetailAsync(receiptId, cancellationToken);
            return receipt == null ? NotFound() : Ok(receipt);
        }

        [HttpGet("ByExpense/{expenseId:int}")]
        public async Task<IActionResult> GetByExpense(int expenseId, CancellationToken cancellationToken)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();

            var receiptId = await _dbContext.Receipts
                .AsNoTracking()
                .Where(x => x.ConvertedExpenseId == expenseId)
                .Select(x => (int?)x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (!receiptId.HasValue) return NotFound();

            var houseId = await GetReceiptHouseIdAsync(receiptId.Value, cancellationToken);
            if (!houseId.HasValue) return NotFound();
            if (!await IsActiveHouseMemberAsync(houseId.Value, currentUserId.Value, cancellationToken))
                return Forbid();

            return Ok(await BuildReceiptDetailAsync(receiptId.Value, cancellationToken));
        }

        [HttpDelete("{receiptId:int}")]
        public async Task<IActionResult> Delete(int receiptId, CancellationToken cancellationToken)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();

            var receipt = await _dbContext.Receipts
                .FirstOrDefaultAsync(x => x.Id == receiptId, cancellationToken);
            if (receipt is null) return NotFound();
            if (!await IsActiveHouseMemberAsync(receipt.HouseId, currentUserId.Value, cancellationToken))
                return Forbid();
            if (receipt.Status == ReceiptStatus.Converted || receipt.ConvertedExpenseId.HasValue)
                return Conflict(new { message = "Harcamaya dönüştürülmüş fiş silinemez." });

            _dbContext.Receipts.Remove(receipt);
            await _dbContext.SaveChangesAsync(cancellationToken);
            DeleteReceiptFile(receipt.ImageUrl);
            return NoContent();
        }

        [HttpPost("{receiptId:int}/Reparse")]
        public async Task<IActionResult> Reparse(int receiptId, CancellationToken cancellationToken)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();

            var receipt = await _dbContext.Receipts
                .Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.Id == receiptId, cancellationToken);

            if (receipt == null) return NotFound();
            if (!await IsActiveHouseMemberAsync(receipt.HouseId, currentUserId.Value, cancellationToken))
                return Forbid();
            if (receipt.Status == ReceiptStatus.Converted)
                return Conflict(new { message = "Harcamaya dönüştürülmüş fiş yeniden işlenemez." });
            if (string.IsNullOrWhiteSpace(receipt.ImageUrl)) return BadRequest(new { message = "Fiş görseli bulunamadı." });

            var relativePath = receipt.ImageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var webRoot = _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
            var fullPath = Path.Combine(webRoot, relativePath);
            if (!System.IO.File.Exists(fullPath)) return NotFound("Fiş dosyası bulunamadı.");

            await using var stream = System.IO.File.OpenRead(fullPath);
            var ocrResult = await _ocrService.ExtractAsync(stream, Path.GetFileName(fullPath), cancellationToken);

            ApplyOcrResult(receipt, ocrResult);
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Ok(await BuildReceiptDetailAsync(receiptId, cancellationToken));
        }

        [HttpPut("{receiptId:int}")]
        public async Task<IActionResult> Update(int receiptId, [FromBody] UpdateReceiptRequest request, CancellationToken cancellationToken)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();

            var receipt = await _dbContext.Receipts
                .Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.Id == receiptId, cancellationToken);

            if (receipt == null) return NotFound();
            if (!await IsActiveHouseMemberAsync(receipt.HouseId, currentUserId.Value, cancellationToken))
                return Forbid();
            if (receipt.Status == ReceiptStatus.Converted)
                return Conflict(new { message = "Harcamaya dönüştürülmüş fiş değiştirilemez." });
            if (request.Items is null || request.Items.Count is < 1 or > 100)
                return BadRequest(new { message = "Fişte 1 ile 100 arasında kalem bulunmalıdır." });
            if (request.DetectedTotalAmount < 0)
                return BadRequest(new { message = "Fiş toplamı negatif olamaz." });

            var activeMemberIds = await GetActiveHouseMemberIdsAsync(receipt.HouseId, cancellationToken);
            var invalidAssignment = request.Items.Any(item =>
            {
                var participantIds = ResolveRequestedParticipantIds(item, activeMemberIds);
                return participantIds.Any(id => !activeMemberIds.Contains(id))
                    || (item.IsAssigned && participantIds.Count == 0);
            });
            if (invalidAssignment)
                return BadRequest(new { message = "Fiş kalemleri yalnızca aktif ev üyelerine atanabilir." });

            var normalizedItems = request.Items
                .OrderBy(x => x.SortOrder)
                .Select((item, index) => new
                {
                    Name = item.Name?.Trim(),
                    Price = Math.Max(item.Price, 0),
                    Quantity = item.Quantity <= 0 ? 1 : item.Quantity,
                    LineTotal = item.LineTotal > 0
                        ? item.LineTotal
                        : Math.Max(item.Price, 0) * (item.Quantity <= 0 ? 1 : item.Quantity),
                    Item = item,
                    SortOrder = index
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.LineTotal > 0)
                .ToList();
            if (normalizedItems.Count == 0)
                return BadRequest(new { message = "En az bir geçerli fiş kalemi bulunmalıdır." });

            receipt.StoreName = string.IsNullOrWhiteSpace(request.StoreName)
                ? null
                : request.StoreName.Trim()[..Math.Min(request.StoreName.Trim().Length, 256)];
            receipt.ReceiptDate = NormalizeReceiptDate(request.ReceiptDate);
            receipt.DetectedTotalAmount = request.DetectedTotalAmount > 0
                ? request.DetectedTotalAmount
                : null;
            receipt.Status = ReceiptStatus.Reviewed;
            receipt.UpdatedAt = DateTime.UtcNow;

            _dbContext.ReceiptItems.RemoveRange(receipt.Items);
            receipt.Items.Clear();

            foreach (var normalized in normalizedItems)
            {
                var item = normalized.Item;
                var name = normalized.Name!;
                var participantIds = ResolveRequestedParticipantIds(item, activeMemberIds);
                var isAssigned = item.IsAssigned || participantIds.Count > 0;
                var isSharedWithWholeHouse = isAssigned && participantIds.Count == activeMemberIds.Count;
                receipt.Items.Add(new ReceiptItem
                {
                    Name = name[..Math.Min(name.Length, 256)],
                    Price = normalized.Price,
                    Quantity = normalized.Quantity,
                    LineTotal = normalized.LineTotal,
                    BoxLeft = item.BoxLeft,
                    BoxTop = item.BoxTop,
                    BoxWidth = item.BoxWidth,
                    BoxHeight = item.BoxHeight,
                    IsAssigned = isAssigned,
                    IsShared = isSharedWithWholeHouse,
                    PersonalUserId = participantIds.Count == 1 ? participantIds[0] : null,
                    ParticipantUserIdsJson = isAssigned ? JsonSerializer.Serialize(participantIds) : null,
                    SortOrder = normalized.SortOrder,
                });
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(await BuildReceiptDetailAsync(receiptId, cancellationToken));
        }

        [HttpPost("{receiptId:int}/ConvertToExpense")]
        public async Task<IActionResult> ConvertToExpense(int receiptId, [FromBody] ConvertReceiptToExpenseRequest request, CancellationToken cancellationToken)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();

            var receipt = await _dbContext.Receipts
                .Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.Id == receiptId, cancellationToken);

            if (receipt == null) return NotFound();
            if (!await IsActiveHouseMemberAsync(receipt.HouseId, currentUserId.Value, cancellationToken))
                return Forbid();
            if (receipt.Status == ReceiptStatus.Converted || receipt.ConvertedExpenseId.HasValue)
                return Conflict(new { message = "Bu fiş daha önce harcamaya dönüştürüldü." });
            if (receipt.Items.Count == 0) return BadRequest(new { message = "Fişte dönüştürülecek kalem bulunamadı." });
            var itemTotal = receipt.Items.Sum(x => x.LineTotal);
            if (receipt.DetectedTotalAmount is not > 0
                || Math.Abs(receipt.DetectedTotalAmount.Value - itemTotal) > 0.02m
                || receipt.Items.Any(x => IsAggregateReceiptItem(x.Name)))
                return BadRequest(new { message = "Fiş toplamı ve kalemler doğrulanmadan harcamaya dönüştürülemez." });

            var activeMemberIds = await GetActiveHouseMemberIdsAsync(receipt.HouseId, cancellationToken);
            if (!activeMemberIds.Contains(request.PayerUserId))
                return BadRequest(new { message = "Ödeyen kişi aktif bir ev üyesi olmalıdır." });
            var itemParticipants = receipt.Items.ToDictionary(
                item => item.Id,
                item => ResolveStoredParticipantIds(item, activeMemberIds));
            if (itemParticipants.Values.Any(ids => ids.Count == 0 || ids.Any(id => !activeMemberIds.Contains(id))))
                return BadRequest(new { message = "Fiş kalemlerinin tamamı en az bir aktif ev üyesine atanmalıdır." });
            if (request.Category.HasValue && !Enum.IsDefined(request.Category.Value))
                return BadRequest(new { message = "Geçersiz harcama kategorisi." });

            var sharedTotal = 0m;
            var personalTotals = activeMemberIds.ToDictionary(id => id, _ => 0m);
            foreach (var item in receipt.Items)
            {
                var participantIds = itemParticipants[item.Id];
                if (participantIds.Count == activeMemberIds.Count)
                {
                    sharedTotal += item.LineTotal;
                    continue;
                }

                foreach (var share in SplitAmount(item.LineTotal, participantIds))
                    personalTotals[share.UserId] += share.Amount;
            }

            var personalItems = personalTotals
                .Where(entry => entry.Value > 0)
                .Select(entry => new PersonalExpenseDto { UserId = entry.Key, Tutar = entry.Value })
                .ToList();

            var title = string.IsNullOrWhiteSpace(receipt.StoreName) ? "Fiş Harcaması" : $"{receipt.StoreName} Fişi";
            var note = string.IsNullOrWhiteSpace(request.Note)
                ? receipt.StoreName ?? "Fişten oluşturuldu"
                : request.Note.Trim();
            note = note[..Math.Min(note.Length, 512)];

            var command = new CreateExpenseCommand
            {
                Tur = title,
                Tutar = receipt.Items.Sum(x => x.LineTotal),
                HouseId = receipt.HouseId,
                OdeyenUserId = request.PayerUserId,
                KaydedenUserId = currentUserId.Value,
                OrtakHarcamaTutari = sharedTotal,
                Participants = activeMemberIds.OrderBy(id => id).ToList(),
                Date = receipt.ReceiptDate ?? DateTime.UtcNow,
                SahsiHarcamalar = personalItems,
                Category = request.Category ?? ExpenseCategory.Market,
                Note = note,
                Description = note,
                Aciklama = note
            };

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var createdExpense = await _mediator.Send(command, cancellationToken);

            receipt.Status = ReceiptStatus.Converted;
            receipt.ConvertedExpenseId = createdExpense.Id;
            receipt.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Ok(new
            {
                receiptId = receipt.Id,
                expense = createdExpense
            });
        }

        [HttpPut("{receiptId:int}/Converted")]
        public async Task<IActionResult> UpdateConverted(
            int receiptId,
            [FromBody] UpdateConvertedReceiptRequest request,
            CancellationToken cancellationToken)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();

            var receipt = await _dbContext.Receipts
                .Include(x => x.Items)
                .FirstOrDefaultAsync(x => x.Id == receiptId, cancellationToken);
            if (receipt == null) return NotFound();
            if (!await IsActiveHouseMemberAsync(receipt.HouseId, currentUserId.Value, cancellationToken))
                return Forbid();
            if (receipt.Status != ReceiptStatus.Converted || !receipt.ConvertedExpenseId.HasValue)
                return Conflict(new { message = "Yalnızca harcamaya dönüştürülmüş fişler bu uçtan güncellenebilir." });
            if (request.Items is null || request.Items.Count is < 1 or > 100)
                return BadRequest(new { message = "Fişte 1 ile 100 arasında kalem bulunmalıdır." });

            var activeMemberIds = await GetActiveHouseMemberIdsAsync(receipt.HouseId, cancellationToken);
            if (!activeMemberIds.Contains(request.PayerUserId))
                return BadRequest(new { message = "Ödeyen kişi aktif bir ev üyesi olmalıdır." });
            if (request.Category.HasValue && !Enum.IsDefined(request.Category.Value))
                return BadRequest(new { message = "Geçersiz harcama kategorisi." });

            var normalizedItems = request.Items
                .OrderBy(x => x.SortOrder)
                .Select((item, index) => new
                {
                    Name = item.Name?.Trim(),
                    Price = Math.Max(item.Price, 0),
                    Quantity = item.Quantity <= 0 ? 1 : item.Quantity,
                    LineTotal = item.LineTotal > 0
                        ? item.LineTotal
                        : Math.Max(item.Price, 0) * (item.Quantity <= 0 ? 1 : item.Quantity),
                    Participants = ResolveRequestedParticipantIds(item, activeMemberIds),
                    Item = item,
                    SortOrder = index
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.LineTotal > 0)
                .ToList();
            if (normalizedItems.Count == 0)
                return BadRequest(new { message = "En az bir geçerli fiş kalemi bulunmalıdır." });
            if (request.DetectedTotalAmount is not > 0
                || Math.Abs(request.DetectedTotalAmount.Value - normalizedItems.Sum(x => x.LineTotal)) > 0.02m
                || normalizedItems.Any(x => IsAggregateReceiptItem(x.Name)))
                return BadRequest(new { message = "Fiş toplamı ve kalemler doğrulanmalıdır." });
            if (normalizedItems.Any(x => x.Participants.Count == 0
                || x.Participants.Any(id => !activeMemberIds.Contains(id))))
                return BadRequest(new { message = "Her fiş kalemi en az bir aktif ev üyesine atanmalıdır." });

            var sharedTotal = 0m;
            var personalTotals = activeMemberIds.ToDictionary(id => id, _ => 0m);
            foreach (var item in normalizedItems)
            {
                if (item.Participants.Count == activeMemberIds.Count)
                {
                    sharedTotal += item.LineTotal;
                    continue;
                }

                foreach (var share in SplitAmount(item.LineTotal, item.Participants))
                    personalTotals[share.UserId] += share.Amount;
            }

            var personalItems = personalTotals
                .Where(entry => entry.Value > 0)
                .Select(entry => new PersonalExpenseDto { UserId = entry.Key, Tutar = entry.Value })
                .ToList();
            var total = normalizedItems.Sum(x => x.LineTotal);
            var expense = await _dbContext.Expenses
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == receipt.ConvertedExpenseId.Value, cancellationToken);
            if (expense == null) return NotFound(new { message = "Fişe bağlı harcama bulunamadı." });

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            _dbContext.ReceiptItems.RemoveRange(receipt.Items);
            receipt.Items.Clear();
            foreach (var normalized in normalizedItems)
            {
                var item = normalized.Item;
                var name = normalized.Name!;
                receipt.Items.Add(new ReceiptItem
                {
                    Name = name[..Math.Min(name.Length, 256)],
                    Price = normalized.Price,
                    Quantity = normalized.Quantity,
                    LineTotal = normalized.LineTotal,
                    BoxLeft = item.BoxLeft,
                    BoxTop = item.BoxTop,
                    BoxWidth = item.BoxWidth,
                    BoxHeight = item.BoxHeight,
                    IsAssigned = true,
                    IsShared = normalized.Participants.Count == activeMemberIds.Count,
                    PersonalUserId = normalized.Participants.Count == 1 ? normalized.Participants[0] : null,
                    ParticipantUserIdsJson = JsonSerializer.Serialize(normalized.Participants),
                    SortOrder = normalized.SortOrder
                });
            }

            receipt.StoreName = string.IsNullOrWhiteSpace(request.StoreName)
                ? receipt.StoreName
                : request.StoreName.Trim()[..Math.Min(request.StoreName.Trim().Length, 256)];
            receipt.ReceiptDate = NormalizeReceiptDate(request.ReceiptDate ?? receipt.ReceiptDate);
            receipt.DetectedTotalAmount = total;
            receipt.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            var note = string.IsNullOrWhiteSpace(request.Note) ? expense.Note : request.Note.Trim();
            await _mediator.Send(new UpdateExpenseCommand
            {
                ExpenseId = expense.Id,
                Dto = new UpdateExpenseDto
                {
                    Tur = string.IsNullOrWhiteSpace(receipt.StoreName) ? expense.Tur : $"{receipt.StoreName} Fişi",
                    Tutar = total,
                    OrtakHarcamaTutari = sharedTotal,
                    OdeyenUserId = request.PayerUserId,
                    Participants = activeMemberIds.OrderBy(id => id).ToList(),
                    SahsiHarcamalar = personalItems,
                    Category = request.Category ?? expense.Category,
                    PostDate = receipt.ReceiptDate,
                    DueDate = receipt.ReceiptDate,
                    Note = note,
                    Description = note,
                    Aciklama = note
                }
            }, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return Ok(await BuildReceiptDetailAsync(receiptId, cancellationToken));
        }

        private async Task<object?> BuildReceiptDetailAsync(int receiptId, CancellationToken cancellationToken)
        {
            var receipt = await _dbContext.Receipts
                .AsNoTracking()
                .Include(x => x.Items)
                .Where(x => x.Id == receiptId)
                .FirstOrDefaultAsync(cancellationToken);
            if (receipt == null) return null;

            var convertedExpense = receipt.ConvertedExpenseId.HasValue
                ? await _dbContext.Expenses
                    .AsNoTracking()
                    .Where(x => x.Id == receipt.ConvertedExpenseId.Value)
                    .Select(x => new
                    {
                        x.Id,
                        x.OdeyenUserId,
                        x.Category,
                        x.Note
                    })
                    .FirstOrDefaultAsync(cancellationToken)
                : null;

            return new
            {
                receipt.Id,
                receipt.HouseId,
                receipt.UploadedByUserId,
                receipt.ImageUrl,
                receipt.ImageWidth,
                receipt.ImageHeight,
                receipt.RawOcrText,
                receipt.StoreName,
                receipt.ReceiptDate,
                receipt.DetectedTotalAmount,
                receipt.Status,
                receipt.CreatedAt,
                receipt.UpdatedAt,
                receipt.ConvertedExpenseId,
                ConvertedExpense = convertedExpense,
                Items = receipt.Items
                    .OrderBy(item => item.SortOrder)
                    .Select(item => new
                    {
                        item.Id,
                        item.Name,
                        item.Price,
                        item.Quantity,
                        item.LineTotal,
                        item.BoxLeft,
                        item.BoxTop,
                        item.BoxWidth,
                        item.BoxHeight,
                        item.IsAssigned,
                        item.IsShared,
                        item.PersonalUserId,
                        ParticipantUserIds = ParseParticipantUserIds(item.ParticipantUserIdsJson),
                        item.SortOrder
                    })
                    .ToList()
            };
        }

        private static List<int> ResolveRequestedParticipantIds(
            UpdateReceiptItemRequest item,
            HashSet<int> activeMemberIds)
        {
            var explicitIds = (item.ParticipantUserIds ?? new List<int>())
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (explicitIds.Count > 0) return explicitIds;
            if (item.IsShared) return activeMemberIds.OrderBy(id => id).ToList();
            return item.PersonalUserId.HasValue ? new List<int> { item.PersonalUserId.Value } : new List<int>();
        }

        private static List<int> ResolveStoredParticipantIds(ReceiptItem item, HashSet<int> activeMemberIds)
        {
            var explicitIds = ParseParticipantUserIds(item.ParticipantUserIdsJson);
            if (explicitIds.Count > 0) return explicitIds;
            if (item.IsShared) return activeMemberIds.OrderBy(id => id).ToList();
            return item.PersonalUserId.HasValue ? new List<int> { item.PersonalUserId.Value } : new List<int>();
        }

        private static List<int> ParseParticipantUserIds(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<int>();
            try
            {
                return (JsonSerializer.Deserialize<List<int>>(json) ?? new List<int>())
                    .Where(id => id > 0)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();
            }
            catch (JsonException)
            {
                return new List<int>();
            }
        }

        private static bool IsAggregateReceiptItem(string? name)
        {
            var value = name?.Trim() ?? string.Empty;
            return value.StartsWith("Fis Toplami", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Fiş Toplamı", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Ortak Kalan Tutar", StringComparison.OrdinalIgnoreCase);
        }

        private static List<(int UserId, decimal Amount)> SplitAmount(decimal total, List<int> participantIds)
        {
            var ordered = participantIds.Distinct().OrderBy(id => id).ToList();
            if (total <= 0 || ordered.Count == 0) return new List<(int, decimal)>();

            var baseAmount = Math.Floor(total * 100 / ordered.Count) / 100;
            var remainderInCents = (int)Math.Round((total - baseAmount * ordered.Count) * 100);
            return ordered.Select((userId, index) => (
                userId,
                baseAmount + (index < remainderInCents ? 0.01m : 0m)
            )).ToList();
        }

        private static DateTime? NormalizeReceiptDate(DateTime? value)
        {
            if (!value.HasValue) return null;
            var date = value.Value;
            return new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Utc);
        }

        private int? GetCurrentUserId()
        {
            var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;
            return int.TryParse(raw, out var userId) && userId > 0 ? userId : null;
        }

        private Task<bool> IsActiveHouseMemberAsync(int houseId, int userId, CancellationToken cancellationToken)
        {
            return _dbContext.HouseMembers
                .AsNoTracking()
                .AnyAsync(x => x.HouseId == houseId && x.UserId == userId && x.IsActive, cancellationToken);
        }

        private async Task<HashSet<int>> GetActiveHouseMemberIdsAsync(int houseId, CancellationToken cancellationToken)
        {
            return (await _dbContext.HouseMembers
                    .AsNoTracking()
                    .Where(x => x.HouseId == houseId && x.IsActive)
                    .Select(x => x.UserId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        private Task<int?> GetReceiptHouseIdAsync(int receiptId, CancellationToken cancellationToken)
        {
            return _dbContext.Receipts
                .AsNoTracking()
                .Where(x => x.Id == receiptId)
                .Select(x => (int?)x.HouseId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        private void ApplyOcrResult(Receipt receipt, ReceiptOcrResult ocrResult, DateTime? fallbackReceiptDate = null)
        {
            receipt.RawOcrText = ocrResult.RawText;
            var storeName = ocrResult.StoreName?.Trim();
            receipt.StoreName = string.IsNullOrWhiteSpace(storeName)
                ? null
                : storeName[..Math.Min(storeName.Length, 256)];
            receipt.ReceiptDate = NormalizeReceiptDate(
                ocrResult.ReceiptDate ?? fallbackReceiptDate ?? receipt.ReceiptDate ?? DateTime.UtcNow.AddHours(3).Date);
            receipt.DetectedTotalAmount = ocrResult.TotalAmount ?? receipt.DetectedTotalAmount;
            receipt.ImageWidth = ocrResult.ImageWidth ?? receipt.ImageWidth;
            receipt.ImageHeight = ocrResult.ImageHeight ?? receipt.ImageHeight;
            receipt.Status = ocrResult.Items.Count > 0 ? ReceiptStatus.Parsed : ReceiptStatus.Uploaded;
            receipt.UpdatedAt = DateTime.UtcNow;

            if (receipt.Items.Count > 0)
            {
                _dbContext.ReceiptItems.RemoveRange(receipt.Items);
                receipt.Items.Clear();
            }

            foreach (var item in ocrResult.Items
                .Where(value => !string.IsNullOrWhiteSpace(value.Name) && value.LineTotal > 0)
                .Select((value, index) => new ReceiptItem
            {
                Name = value.Name.Trim()[..Math.Min(value.Name.Trim().Length, 256)],
                Price = Math.Max(value.Price, 0),
                Quantity = value.Quantity <= 0 ? 1 : value.Quantity,
                LineTotal = value.LineTotal,
                BoxLeft = value.BoxLeft,
                BoxTop = value.BoxTop,
                BoxWidth = value.BoxWidth,
                BoxHeight = value.BoxHeight,
                IsAssigned = false,
                IsShared = true,
                ParticipantUserIdsJson = null,
                SortOrder = index
            }))
            {
                receipt.Items.Add(item);
            }
        }

        private static async Task<bool> HasValidImageSignatureAsync(
            IFormFile image,
            CancellationToken cancellationToken)
        {
            var header = new byte[12];
            await using var stream = image.OpenReadStream();
            var read = await stream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);

            return image.ContentType.ToLowerInvariant() switch
            {
                "image/jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
                "image/png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                "image/webp" => read >= 12 &&
                    header.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                    header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
                _ => false
            };
        }

        private void DeleteReceiptFile(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl) ||
                !imageUrl.StartsWith("/uploads/receipts/", StringComparison.OrdinalIgnoreCase))
                return;

            var webRoot = _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
            var receiptsRoot = Path.GetFullPath(Path.Combine(webRoot, "uploads", "receipts"));
            var relativePath = imageUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(webRoot, relativePath));
            var rootPrefix = receiptsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(fullPath))
                System.IO.File.Delete(fullPath);
        }
    }

    public class ReceiptScanRequest
    {
        public int HouseId { get; set; }
        public IFormFile? Image { get; set; }
    }

    public class UpdateReceiptRequest
    {
        public string? StoreName { get; set; }
        public DateTime? ReceiptDate { get; set; }
        public decimal? DetectedTotalAmount { get; set; }
        public List<UpdateReceiptItemRequest> Items { get; set; } = new();
    }

    public class UpdateReceiptItemRequest
    {
        public string? Name { get; set; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal LineTotal { get; set; }
        public int? BoxLeft { get; set; }
        public int? BoxTop { get; set; }
        public int? BoxWidth { get; set; }
        public int? BoxHeight { get; set; }
        public bool IsAssigned { get; set; }
        public bool IsShared { get; set; } = true;
        public int? PersonalUserId { get; set; }
        public List<int>? ParticipantUserIds { get; set; }
        public int SortOrder { get; set; }
    }

    public class ConvertReceiptToExpenseRequest
    {
        public int PayerUserId { get; set; }
        public int RecordedByUserId { get; set; }
        public string? Note { get; set; }
        public ExpenseCategory? Category { get; set; }
    }

    public class UpdateConvertedReceiptRequest : UpdateReceiptRequest
    {
        public int PayerUserId { get; set; }
        public ExpenseCategory? Category { get; set; }
        public string? Note { get; set; }
    }
}
