using MediatR;
using Application.Features.Expenses.Dtos;
using Application.Services.Repositories;

namespace Application.Features.Expenses.Queries.GetExpensesByHouse
{
    public class GetExpensesByHouseQueryHandler
        : IRequestHandler<GetExpensesByHouseQuery, List<ExpenseListDto>>
    {
        private readonly IExpenseRepository _repo;
        private readonly IUserRepository _userRepo;

        public GetExpensesByHouseQueryHandler(
            IExpenseRepository repo,
            IUserRepository userRepo)
        {
            _repo = repo;
            _userRepo = userRepo;
        }

        public async Task<List<ExpenseListDto>> Handle(
            GetExpensesByHouseQuery request,
            CancellationToken ct)
        {
            var list = await _repo.GetByHouseIdAsync(request.HouseId);
            var dtoList = new List<ExpenseListDto>();
            foreach (var e in list)
            {
                // 🔹 Soft-deleted olanları listeleme
                if (!e.IsActive) continue;

                var dto = ExpenseDtoMapper.ToListDto(e);
                var payer = await _userRepo.GetByIdAsync(e.OdeyenUserId);
                var creator = await _userRepo.GetByIdAsync(e.KaydedenUserId);

                dto.OdeyenKullaniciAdi = payer is null ? "" : $"{payer.FirstName} {payer.LastName}";
                dto.KaydedenKullaniciAdi = creator is null ? "" : $"{creator.FirstName} {creator.LastName}";
                dto.KayitTarihi = e.CreatedDate;
                dto.PostDate = e.PostDate;
                dto.DueDate = e.DueDate;
                dto.PreShareDays = e.PreShareDays;
                dto.VisibilityMode = e.VisibilityMode;
                dto.Category = e.Category;
                dto.Participants = e.Shares
                    .Select(share => (share.UserId, Amount: share.PaylasimTutar))
                    .Concat(e.PersonalExpenses.Select(personal => (personal.UserId, Amount: personal.Tutar)))
                    .GroupBy(part => part.UserId)
                    .Select(group => new ExpenseParticipantDto { UserId = group.Key, Amount = group.Sum(part => part.Amount) })
                    .Where(part => part.Amount > 0)
                    .ToList();

                // 🔹 Notu description'a yansıt
                if (string.IsNullOrWhiteSpace(dto.Description))
                    dto.Description = e.Note ?? string.Empty;

                dtoList.Add(dto);
            }
            return dtoList;
        }
    }
}
