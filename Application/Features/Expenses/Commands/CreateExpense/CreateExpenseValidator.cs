using FluentValidation;
using Application.Services.Repositories;
using System.Linq;

namespace Application.Features.Expenses.Commands.CreateExpense
{
    public class CreateExpenseValidator : AbstractValidator<CreateExpenseCommand>
    {
        public CreateExpenseValidator(IHouseRepository houseRepo)
        {
            // Ortak kurallar
            RuleFor(x => x.Tur).NotEmpty();
            RuleFor(x => x.Tutar).GreaterThan(0);
            RuleFor(x => x.OrtakHarcamaTutari)
                .GreaterThanOrEqualTo(0).WithMessage("Ortak harcama tutarı negatif olamaz.");

            RuleForEach(x => x.SahsiHarcamalar).ChildRules(p =>
            {
                p.RuleFor(y => y.UserId).GreaterThan(0);
                p.RuleFor(y => y.Tutar)
                    .GreaterThanOrEqualTo(0).WithMessage("Şahsi harcama tutarı negatif olamaz.");
            });

            // INSTALLMENT kuralları
            When(x => (x.Mode ?? "").Trim().ToLower() == "installment" || (x.InstallmentCount ?? 0) > 1, () =>
            {
                RuleFor(x => x.InstallmentCount)
                    .Must(count => count is >= 1 and <= 60)
                    .WithMessage("Taksit sayısı 1 ile 60 arasında olmalı.");

                RuleFor(x => x.CardholderUserId ?? x.OdeyenUserId)
                    .GreaterThan(0).WithMessage("Kart sahibi (CardholderUserId) geçerli olmalı.");

                // >>> DÜZELTME: selector doğrudan property
                RuleFor(x => x.DueDay)
                    .NotNull().WithMessage("Vade günü zorunlu.")
                    .Must(d => d != null && d.Value >= 1 && d.Value <= 28)
                    .WithMessage("Vade günü 1–28 arasında olmalı.");

                // >>> DÜZELTME: property bazlı kontrol
                RuleFor(x => x.StartMonth)
                    .NotNull().Unless(x => x.Date != default)
                    .WithMessage("StartMonth veya Date verilmelidir.");
            });

            // RECURRING kuralları
            When(x => (x.Mode ?? "").Trim().ToLower() == "recurring", () =>
            {
                RuleFor(x => x.DueDay)
                    .NotNull().WithMessage("Vade günü zorunlu.")
                    .Must(d => d != null && d.Value >= 1 && d.Value <= 28)
                    .WithMessage("Vade günü 1–28 arasında olmalı.");

                RuleFor(x => x.StartMonth)
                    .NotNull().Unless(x => x.Date != default)
                    .WithMessage("StartMonth veya Date verilmelidir.");
            });

            // Yalnizca tek seferlik harcamalar sahsi/ortak tutar dagilimi tasir.
            When(x => (x.Mode ?? "").Trim().ToLowerInvariant() != "recurring"
                && (x.Mode ?? "").Trim().ToLowerInvariant() != "installment"
                && (x.InstallmentCount ?? 0) <= 1, () =>
            {
                RuleFor(x => x).Custom((cmd, ctx) =>
                {
                    var sumPersonal = cmd.SahsiHarcamalar.Sum(pe => pe.Tutar);
                    if (sumPersonal + cmd.OrtakHarcamaTutari != cmd.Tutar)
                        ctx.AddFailure("Tutar doğrulaması",
                            "Şahsi harcamalar + ortak harcama tutarı toplam Tutar ile eşit olmalı.");
                });
            });

            // Üyelik doğrulamaları (değişmedi)
            RuleFor(x => x).CustomAsync(async (cmd, ctx, ct) =>
            {
                var house = await houseRepo.GetByIdAsync(cmd.HouseId);
                if (house == null)
                {
                    ctx.AddFailure("HouseId", "Ev grubu bulunamadi.");
                    return;
                }

                var memberIds = house.HouseMembers
                    .Where(m => m.IsActive)
                    .Select(m => m.UserId)
                    .ToHashSet();

                if (!memberIds.Contains(cmd.KaydedenUserId))
                    ctx.AddFailure("KaydedenUserId", "Harcamayı kaydeden kişi aktif bir ev üyesi olmalı.");

                if (!memberIds.Contains(cmd.OdeyenUserId))
                    ctx.AddFailure("OdeyenUserId", "Ödeyen kişi aktif bir ev üyesi olmalı.");

                foreach (var pe in cmd.SahsiHarcamalar)
                {
                    if (!memberIds.Contains(pe.UserId))
                        ctx.AddFailure($"User {pe.UserId}", "Bu ev grubunun üyesi değil.");
                }

                if (cmd.Participants != null && cmd.Participants.Count > 0)
                {
                    foreach (var uid in cmd.Participants.Distinct())
                    {
                        if (!memberIds.Contains(uid))
                            ctx.AddFailure($"User {uid}", "Katılımcılar arasında ev üyesi olmayan kişi var.");
                    }
                }

                var cardholder = cmd.CardholderUserId ?? cmd.OdeyenUserId;
                if (!memberIds.Contains(cardholder))
                    ctx.AddFailure("Cardholder", "Kart sahibi ev üyesi olmalı.");
            });
        }
    }
}
