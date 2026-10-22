// EvArkadasim.API/Controllers/PaymentsController.cs
using System;
using System.IO;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Application.Services.Repositories;

using Application.Features.Payments.Commands.CreatePayment;
using Application.Features.Payments.Queries.GetPaymentList;
using Application.Features.Payments.Queries.GetPendingPayments;
using Application.Features.Payments.Commands.ApprovePayment;
using Application.Features.Payments.Commands.RejectPayment;
using Application.Features.Payments.Commands.DeletePayment;
using Domain.Enums;
using EvArkadasim.API.Services.Notifications;

namespace EvArkadasim.API.Controllers
{
    public class CreatePaymentForm
    {
        public int HouseId { get; set; }
        public int BorcluUserId { get; set; }
        public int AlacakliUserId { get; set; }
        public decimal Tutar { get; set; }
        public string? PaymentMethod { get; set; } = "BankTransfer";
        public IFormFile? Dekont { get; set; }
        public DateTime? OdemeTarihi { get; set; }
        public string? Aciklama { get; set; }
        public int? ChargeId { get; set; }
    }

    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class PaymentsController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly IWebHostEnvironment _env;
        private readonly IHouseRepository _houseRepository;
        private readonly IPaymentRepository _paymentRepository;
        private readonly IPushNotificationService _pushNotifications;

        public PaymentsController(
            IMediator mediator,
            IWebHostEnvironment env,
            IHouseRepository houseRepository,
            IPaymentRepository paymentRepository,
            IPushNotificationService pushNotifications)
        {
            _mediator = mediator;
            _env = env;
            _houseRepository = houseRepository;
            _paymentRepository = paymentRepository;
            _pushNotifications = pushNotifications;
        }

        [HttpPost("CreatePayment")]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> CreatePayment([FromForm] CreatePaymentForm form)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();
            if (form.BorcluUserId != currentUserId.Value)
                return Forbid();
            if (form.Tutar <= 0)
                return BadRequest(new { message = "Ödeme tutarı sıfırdan büyük olmalıdır." });
            if (form.BorcluUserId == form.AlacakliUserId)
                return BadRequest(new { message = "Borçlu ve alacaklı aynı kişi olamaz." });
            if (!await _houseRepository.IsActiveMemberAsync(form.HouseId, currentUserId.Value)
                || !await _houseRepository.IsActiveMemberAsync(form.HouseId, form.AlacakliUserId))
                return Forbid();

            var pmRaw = (form.PaymentMethod ?? "BankTransfer").Trim();
            var isCash = pmRaw.Equals("Cash", StringComparison.OrdinalIgnoreCase);
            var isTransfer = pmRaw.Equals("BankTransfer", StringComparison.OrdinalIgnoreCase);

            if (!isCash && !isTransfer)
                return BadRequest("paymentMethod geçersiz. 'Cash' veya 'BankTransfer' olmalı.");

            if (isTransfer && form.Dekont == null)
                return BadRequest("BankTransfer için dekont zorunludur.");

            string dekontUrl = "";
            if (form.Dekont != null)
            {
                var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var uploadsRoot = Path.Combine(webRoot, "uploads", "payments");
                Directory.CreateDirectory(uploadsRoot);

                var fileName = $"{Guid.NewGuid()}{Path.GetExtension(form.Dekont.FileName)}";
                var physicalPath = Path.Combine(uploadsRoot, fileName);
                await using (var fs = System.IO.File.Create(physicalPath))
                    await form.Dekont.CopyToAsync(fs);

                dekontUrl = $"/uploads/payments/{fileName}";
            }

            var cmd = new CreatePaymentCommand
            {
                HouseId = form.HouseId,
                BorcluUserId = form.BorcluUserId,
                AlacakliUserId = form.AlacakliUserId,
                Tutar = form.Tutar,
                PaymentMethod = isCash ? PaymentMethod.Cash : PaymentMethod.BankTransfer,
                OdemeTarihi = form.OdemeTarihi ?? DateTime.UtcNow,
                Aciklama = form.Aciklama,
                DekontUrl = dekontUrl,
                ChargeId = form.ChargeId
            };

            var result = await _mediator.Send(cmd);
            await _pushNotifications.SendAsync(
                form.AlacakliUserId,
                "payment",
                "Yeni ödeme bildirimi",
                $"{form.Tutar:N2} TL tutarındaki ödeme onayını bekliyor.",
                "New payment report",
                $"A payment of {form.Tutar:N2} TRY is waiting for your approval.",
                data: new { route = "BekleyenOdemeler", paymentId = result.Id, houseId = form.HouseId },
                ct: HttpContext.RequestAborted);
            return CreatedAtAction(nameof(CreatePayment), new { id = result.Id }, result);
        }

        [HttpGet("GetPayments/{houseId}")]
        public async Task<IActionResult> GetPayments(int houseId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();
            if (!await _houseRepository.IsActiveMemberAsync(houseId, currentUserId.Value))
                return Forbid();

            var list = await _mediator.Send(new GetPaymentListQuery { HouseId = houseId });
            return Ok(list);
        }

        [HttpGet("GetPendingPayments/{userId:int}")]
        public async Task<IActionResult> GetPendingPayments([FromRoute] int userId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();
            if (currentUserId.Value != userId)
                return Forbid();

            var result = await _mediator.Send(new GetPendingPaymentsQuery(userId));
            return Ok(result);
        }

        [HttpPost("ApprovePayment/{paymentId:int}")]
        public async Task<IActionResult> ApprovePayment([FromRoute] int paymentId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();

            var payment = await _paymentRepository.GetByIdAsync(paymentId);
            if (payment is null)
                return NotFound(new { message = "Ödeme bulunamadı." });
            if (payment.AlacakliUserId != currentUserId.Value)
                return Forbid();

            var result = await _mediator.Send(new ApprovePaymentCommand(paymentId));
            await _pushNotifications.SendAsync(
                payment.BorcluUserId,
                "payment",
                "Ödeme onaylandı",
                $"{payment.Tutar:N2} TL tutarındaki ödemen onaylandı.",
                "Payment approved",
                $"Your payment of {payment.Tutar:N2} TRY was approved.",
                data: new { route = "Bildirimler", paymentId },
                ct: HttpContext.RequestAborted);
            return Ok(new { success = true, message = "Ödeme başarıyla onaylandı", data = result });
        }

        [HttpPost("RejectPayment/{paymentId:int}")]
        public async Task<IActionResult> RejectPayment([FromRoute] int paymentId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();

            var payment = await _paymentRepository.GetByIdAsync(paymentId);
            if (payment is null)
                return NotFound(new { message = "Ödeme bulunamadı." });
            if (payment.AlacakliUserId != currentUserId.Value)
                return Forbid();

            var result = await _mediator.Send(new RejectPaymentCommand(paymentId));
            await _pushNotifications.SendAsync(
                payment.BorcluUserId,
                "payment",
                "Ödeme reddedildi",
                $"{payment.Tutar:N2} TL tutarındaki ödeme bildirimin reddedildi.",
                "Payment rejected",
                $"Your payment report of {payment.Tutar:N2} TRY was rejected.",
                data: new { route = "Bildirimler", paymentId },
                ct: HttpContext.RequestAborted);
            return Ok(new { success = true, message = "Ödeme reddedildi", data = result });
        }

        /// <summary>DELETE /api/Payments/{id} — Soft delete</summary>
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> DeletePayment([FromRoute] int id)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();

            await _mediator.Send(new DeletePaymentCommand(id, currentUserId.Value));
            return Ok(new { success = true, message = "Ödeme silindi." });
        }

        private int? GetCurrentUserId()
        {
            var value = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub");
            return int.TryParse(value, out var userId) && userId > 0 ? userId : null;
        }
    }
}
