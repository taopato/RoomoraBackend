using MediatR;
using Application.Features.Expenses.Dtos;
using Domain.Enums;
using System;
using System.Collections.Generic;

namespace Application.Features.Expenses.Commands.CreateExpense
{
    public class CreateExpenseCommand : IRequest<CreatedExpenseResponseDto>
    {
        // Mevcut alanlar (geriye dönük uyumluluk)
        public string Tur { get; set; } = string.Empty;
        public decimal Tutar { get; set; }                 // single/recurring: aylık; installment: toplam
        public int HouseId { get; set; }
        public int OdeyenUserId { get; set; }
        public int KaydedenUserId { get; set; }
        public decimal OrtakHarcamaTutari { get; set; }
        public DateTime Date { get; set; }                 // eski akışta kullanılır
        public DateTime? PostDate { get; set; }            // faturanın sisteme yansıyacağı tarih
        public DateTime? DueDate { get; set; }             // son ödeme tarihi

        public PaylasimTuru PaylasimTuru { get; set; }     // kullanılmasa da dursun
        public List<PersonalExpenseDto> SahsiHarcamalar { get; set; } = new();

        // --- Yeni: tek endpoint içinde mod desteği ---
        // "single" | "recurring" | "installment"  (gönderilmezse mevcut davranış)
        public string? Mode { get; set; }

        // --- Recurring için ---
        /// <summary>Vade günü 1–28 (zorunlu). Örn: 5 → her ayın 5’i.</summary>
        public byte? DueDay { get; set; }

        /// <summary>Plan başlangıç ayı (YYYY-MM-01). Gönderilmezse Date veya UtcNow kullanılır.</summary>
        public DateTime? StartMonth { get; set; }

        // --- Installment için ---
        public int? InstallmentCount { get; set; }         // örn. 6
        public int? CardholderUserId { get; set; }         // kart sahibi (varsayılan: OdeyenUserId)
        public List<int>? Participants { get; set; }       // boş ise ev üyeleri eşit bölünür
        public List<ShareUnitDto>? ShareUnits { get; set; } // null: eski eşit paylaşım

        // --- Not/Açıklama (FE çok isimle gönderebiliyor) ---
        // FE "note", "Aciklama", "Description" gibi alanlar gönderebilir.
        // Handler bu üçlüyü aynı öncelikle okuyacak.
        public string? Aciklama { get; set; }
        public string? Note { get; set; }
        public string? Description { get; set; }
        // 🔸 Kategori (FE iki yoldan birini gönderebilir)
        public ExpenseCategory? Category { get; set; }
        public int? CategoryId { get; set; }
    }
}
