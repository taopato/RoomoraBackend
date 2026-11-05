using System;
using System.Collections.Generic;

namespace Application.Features.Expenses.Dtos
{
    public class ExpenseDetailDto : ExpenseListDto
    {
        public List<PersonalExpenseDto> SahsiHarcamalar { get; set; } = new();
        public List<ShareDto> Shares { get; set; } = new();
        public decimal OrtakHarcamaTutari { get; set; }
        public List<ShareUnitDto> ShareUnits { get; set; } = new();
    }
}
