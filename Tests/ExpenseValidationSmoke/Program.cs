using Application.Features.Expenses.Commands.CreateExpense;
using Application.Services.Repositories;
using Domain.Entities;

var house = new House
{
    Id = 1,
    HouseMembers = new List<HouseMember>
    {
        new() { HouseId = 1, UserId = 10, IsActive = true },
        new() { HouseId = 1, UserId = 11, IsActive = true }
    }
};
var validator = new CreateExpenseValidator(new HouseRepositoryStub(house));

var installment = new CreateExpenseCommand
{
    Mode = "installment",
    Tur = "Buzdolabi",
    Tutar = 1200m,
    HouseId = 1,
    KaydedenUserId = 10,
    OdeyenUserId = 10,
    CardholderUserId = 10,
    Participants = new List<int> { 10, 11 },
    InstallmentCount = 6,
    DueDay = 15,
    StartMonth = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)
};
Assert((await validator.ValidateAsync(installment)).IsValid, "Installment request rejected");

installment.InstallmentCount = 1;
Assert((await validator.ValidateAsync(installment)).IsValid, "Last installment rejected");

installment.InstallmentCount = 61;
Assert(!(await validator.ValidateAsync(installment)).IsValid, "Invalid installment count accepted");

var single = new CreateExpenseCommand
{
    Tur = "Market",
    Tutar = 100m,
    HouseId = 1,
    KaydedenUserId = 10,
    OdeyenUserId = 10
};
Assert(!(await validator.ValidateAsync(single)).IsValid, "Single expense amount mismatch accepted");
single.OrtakHarcamaTutari = 100m;
Assert((await validator.ValidateAsync(single)).IsValid, "Valid single expense rejected");

Console.WriteLine("Expense validation smoke tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class HouseRepositoryStub(House house) : IHouseRepository
{
    public Task<House> GetByIdAsync(int id) => Task.FromResult(house);
    public Task<House> AddAsync(House entity) => throw new NotSupportedException();
    public Task<List<House>> GetAllAsync() => throw new NotSupportedException();
    public Task AddMemberAsync(HouseMember member) => throw new NotSupportedException();
    public Task<bool> IsActiveMemberAsync(int houseId, int userId) => throw new NotSupportedException();
    public Task RemoveMemberAsync(int houseId, int userId) => throw new NotSupportedException();
    public Task UpdateAsync(House entity) => throw new NotSupportedException();
    public Task DeleteAsync(int houseId) => throw new NotSupportedException();
    public Task SaveChangesAsync() => throw new NotSupportedException();
}
