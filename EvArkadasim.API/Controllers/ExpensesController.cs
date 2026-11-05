using Application.Features.Expenses.Commands.CreateExpense;
using Application.Features.Expenses.Commands.CreateIrregularExpense;
using Application.Features.Expenses.Commands.DeleteExpense;
using Application.Features.Expenses.Commands.UpdateExpense;
using Application.Features.Expenses.Dtos;
using Application.Features.Expenses.Queries.GetExpense;
using Application.Features.Expenses.Queries.GetExpensesByHouse;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;
using EvArkadasim.API.Services.Notifications;
using System.Security.Claims;
using System.Threading.Tasks;

namespace WebAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ExpensesController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly AppDbContext _dbContext;
        private readonly HouseActivityNotificationService _activityNotifications;

        public ExpensesController(IMediator mediator, AppDbContext dbContext, HouseActivityNotificationService activityNotifications)
        {
            _mediator = mediator;
            _dbContext = dbContext;
            _activityNotifications = activityNotifications;
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateExpenseCommand command)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            if (!await CanAccessHouseAsync(command.HouseId, currentUserId.Value)) return Forbid();
            command.KaydedenUserId = currentUserId.Value;
            CreatedExpenseResponseDto result = await _mediator.Send(command);
            await _activityNotifications.NotifyExpenseCreatedAsync(command.HouseId, currentUserId.Value, result.Id, result.Tur, result.Tutar);
            return Ok(result);
        }

        [HttpPost("AddExpense")]
        public async Task<IActionResult> AddExpense([FromBody] CreateExpenseCommand command)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            if (!await CanAccessHouseAsync(command.HouseId, currentUserId.Value)) return Forbid();
            command.KaydedenUserId = currentUserId.Value;
            CreatedExpenseResponseDto result = await _mediator.Send(command);
            await _activityNotifications.NotifyExpenseCreatedAsync(command.HouseId, currentUserId.Value, result.Id, result.Tur, result.Tutar);
            return Ok(result);
        }

        [HttpGet("GetExpenses/{houseId}")]
        public async Task<IActionResult> GetExpenses(int houseId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            if (!await CanAccessHouseAsync(houseId, currentUserId.Value)) return Forbid();
            var data = await _mediator.Send(new GetExpensesByHouseQuery { HouseId = houseId });
            return Ok(new { isSuccess = true, data });
        }

        [HttpGet("GetExpense/{expenseId}")]
        public async Task<IActionResult> GetExpense(int expenseId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            if (!await CanAccessExpenseAsync(expenseId, currentUserId.Value)) return Forbid();
            var data = await _mediator.Send(new GetExpenseQuery { ExpenseId = expenseId });
            return Ok(new { isSuccess = true, data });
        }

        [HttpDelete("DeleteExpense/{expenseId}")]
        public async Task<IActionResult> DeleteExpense(int expenseId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            if (!await CanAccessExpenseAsync(expenseId, currentUserId.Value)) return Forbid();
            await _mediator.Send(new DeleteExpenseCommand { ExpenseId = expenseId });
            return Ok(new { isSuccess = true, message = "Harcama başarıyla silindi." });
        }

        [HttpPut("UpdateExpense/{expenseId}")]
        public async Task<IActionResult> UpdateExpense(int expenseId, [FromBody] UpdateExpenseDto dto)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            if (!await CanAccessExpenseAsync(expenseId, currentUserId.Value)) return Forbid();
            await _mediator.Send(new UpdateExpenseCommand
            {
                ExpenseId = expenseId,
                Dto = dto
            });
            return Ok(new { isSuccess = true, message = "Harcama başarıyla güncellendi." });
        }

        [HttpPost("CreateIrregular")]
        public async Task<IActionResult> CreateIrregular([FromBody] CreateIrregularExpenseRequest model)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null) return Unauthorized();
            if (!await CanAccessHouseAsync(model.HouseId, currentUserId.Value)) return Forbid();
            model.KaydedenUserId = currentUserId.Value;
            var result = await _mediator.Send(new CreateIrregularExpenseCommand { Model = model });
            await _activityNotifications.NotifyExpenseCreatedAsync(model.HouseId, currentUserId.Value, result.ExpenseId, model.Tur, result.Tutar);
            return Ok(result);
        }

        private int? GetCurrentUserId()
        {
            var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;
            return int.TryParse(raw, out var userId) && userId > 0 ? userId : null;
        }

        private Task<bool> CanAccessHouseAsync(int houseId, int userId)
        {
            return _dbContext.HouseMembers
                .AsNoTracking()
                .AnyAsync(member => member.HouseId == houseId && member.UserId == userId && member.IsActive);
        }

        private async Task<bool> CanAccessExpenseAsync(int expenseId, int userId)
        {
            var houseId = await _dbContext.Expenses
                .AsNoTracking()
                .Where(expense => expense.Id == expenseId && expense.IsActive)
                .Select(expense => (int?)expense.HouseId)
                .FirstOrDefaultAsync();
            return houseId.HasValue && await CanAccessHouseAsync(houseId.Value, userId);
        }
    }
}
