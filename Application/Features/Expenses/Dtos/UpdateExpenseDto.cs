using System.Collections.Generic;

namespace Application.Features.Expenses.Dtos
{
    public class UpdateExpenseDto
    {
        public string Tur { get; set; } = string.Empty;
        public decimal Tutar { get; set; }
        public decimal OrtakHarcamaTutari { get; set; }
        public Domain.Enums.ExpenseCategory? Category { get; set; }
        public System.DateTime? PostDate { get; set; }
        public System.DateTime? DueDate { get; set; }
        public int? OdeyenUserId { get; set; }
        public List<int> Participants { get; set; } = new();
        public List<ShareUnitDto>? ShareUnits { get; set; }

        // Şahsi kalemler yalnızca seçilen katılımcılara atanabilir.
        public List<PersonalExpenseDto> SahsiHarcamalar { get; set; } = new();

        // 🔹 Not/Açıklama – FE farklı anahtarlar gönderebildiği için hepsini destekle
        public string? Aciklama { get; set; }
        public string? Note { get; set; }
        public string? Description { get; set; }
    }
}
