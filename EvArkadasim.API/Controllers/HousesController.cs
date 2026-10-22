using Application.Features.Houses.Commands.CreateHouse;
using Application.Features.Houses.Commands.DeleteHouse;
using Application.Features.Houses.Commands.LeaveHouse;
using Application.Features.Houses.Queries.GetHouseMembersWithDebts;
using Application.Features.Houses.Queries.GetUserDebtBetween;
using Application.Features.Houses.Queries.GetUserDebts;
using Application.Features.Houses.Queries.GetUserHouses;
using Application.Features.Invitations.Commands.SendInvitation;
using Application.Features.Invitations.Dtos;
using Application.Services.Repositories;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using System.IO;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using EvArkadasim.API.Services.Notifications;

namespace WebAPI.Controllers
{
    public sealed class UploadHouseCoverForm
    {
        public IFormFile? Image { get; set; }
    }

    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class HousesController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly IHouseRepository _houseRepository;
        private readonly IInvitationRepository _invitationRepository;
        private readonly IUserRepository _userRepository;
        private readonly IWebHostEnvironment _environment;
        private readonly IPushNotificationService _pushNotifications;

        public HousesController(
            IMediator mediator,
            IHouseRepository houseRepository,
            IInvitationRepository invitationRepository,
            IUserRepository userRepository,
            IWebHostEnvironment environment,
            IPushNotificationService pushNotifications)
        {
            _mediator = mediator;
            _houseRepository = houseRepository;
            _invitationRepository = invitationRepository;
            _userRepository = userRepository;
            _environment = environment;
            _pushNotifications = pushNotifications;
        }

        [HttpGet("{houseId:int}")]
        public async Task<IActionResult> GetById(int houseId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();
            if (!await _houseRepository.IsActiveMemberAsync(houseId, currentUserId.Value))
                return Forbid();

            try
            {
                var house = await _houseRepository.GetByIdAsync(houseId);
                return Ok(new
                {
                    house.Id,
                    house.Name,
                    house.CoverImageUrl,
                    house.GroupType,
                    house.TripStartDate,
                    house.TripEndDate,
                    house.CurrencyCode,
                    house.CreatorUserId
                });
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { message = "Ev bulunamadi." });
            }
        }

        [HttpPost("{houseId:int}/CoverImage")]
        [RequestSizeLimit(8 * 1024 * 1024)]
        public async Task<IActionResult> UploadCoverImage(int houseId, [FromForm] UploadHouseCoverForm form)
        {
            var image = form.Image;
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdClaim, out var userId) || userId <= 0)
                return Unauthorized(new { message = "Gecerli kullanici kimligi bulunamadi." });
            if (!await _houseRepository.IsActiveMemberAsync(houseId, userId))
                return Forbid();
            if (image == null || image.Length == 0 || image.Length > 8 * 1024 * 1024)
                return BadRequest(new { message = "Gecerli ve 8 MB'dan kucuk bir gorsel secin." });

            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            if (extension is not ".jpg" and not ".jpeg" and not ".png" and not ".webp")
                return BadRequest(new { message = "Yalnizca JPG, PNG veya WEBP gorseller desteklenir." });
            if (!await HasValidImageSignatureAsync(image, extension))
                return BadRequest(new { message = "Secilen dosya gecerli bir JPG, PNG veya WEBP gorseli degil." });

            var house = await _houseRepository.GetByIdAsync(houseId);
            var webRoot = string.IsNullOrWhiteSpace(_environment.WebRootPath)
                ? Path.Combine(_environment.ContentRootPath, "wwwroot")
                : _environment.WebRootPath;
            var folder = Path.Combine(webRoot, "uploads", "houses");
            Directory.CreateDirectory(folder);
            var fileName = $"house-{houseId}-{Guid.NewGuid():N}{extension}";
            var destination = Path.Combine(folder, fileName);
            await using (var stream = System.IO.File.Create(destination))
                await image.CopyToAsync(stream);

            var oldUrl = house.CoverImageUrl;
            house.CoverImageUrl = $"/uploads/houses/{fileName}";
            await _houseRepository.UpdateAsync(house);

            if (!string.IsNullOrWhiteSpace(oldUrl) && oldUrl.StartsWith("/uploads/houses/", StringComparison.OrdinalIgnoreCase))
            {
                var oldPath = Path.Combine(webRoot, oldUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
            }
            return Ok(new { coverImageUrl = house.CoverImageUrl });
        }

        private static async Task<bool> HasValidImageSignatureAsync(IFormFile image, string extension)
        {
            var header = new byte[12];
            await using var stream = image.OpenReadStream();
            var bytesRead = await stream.ReadAsync(header.AsMemory(0, header.Length));

            if (extension is ".jpg" or ".jpeg")
                return bytesRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
            if (extension == ".png")
                return bytesRead >= 8
                    && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
                    && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
            if (extension == ".webp")
                return bytesRead >= 12
                    && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                    && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P';

            return false;
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateHouseCommand command)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdClaim, out var userId) || userId <= 0)
            {
                return Unauthorized(new { message = "Gecerli kullanici kimligi bulunamadi." });
            }

            command.CreatorUserId = userId;
            var dto = await _mediator.Send(command);
            return Ok(dto);
        }

        [HttpPost("{houseId}/invitations")]
        public async Task<IActionResult> SendInvitation(int houseId, [FromBody] SendInvitationRequestDto request)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();
            if (!await _houseRepository.IsActiveMemberAsync(houseId, currentUserId.Value))
                return Forbid();

            var command = new SendInvitationCommand { HouseId = houseId, Email = request.Email };
            var result = await _mediator.Send(command);
            var invitedUser = await _userRepository.GetByEmailAsync(request.Email.Trim());
            if (invitedUser is not null)
            {
                await _pushNotifications.SendAsync(
                    invitedUser.Id,
                    "group",
                    "Roomora daveti",
                    "Yeni bir gruba davet edildin.",
                    "Roomora invitation",
                    "You were invited to a new group.",
                    data: new { route = "Bildirimler", houseId },
                    ct: HttpContext.RequestAborted);
            }
            return Ok(result);
        }

        [HttpPost("AcceptInvitation")]
        public async Task<IActionResult> AcceptInvitation([FromBody] AcceptInvitationRequestDto request)
        {
            var invitationCode = request.InvitationCode?.Trim();
            if (string.IsNullOrWhiteSpace(invitationCode))
            {
                return BadRequest(new { message = "Davet kodu zorunludur." });
            }

            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdClaim, out var userId) || userId <= 0)
            {
                return Unauthorized(new { message = "Gecerli kullanici kimligi bulunamadi." });
            }

            var user = await _userRepository.GetByIdAsync(userId);
            if (user == null)
            {
                return Unauthorized(new { message = "Kullanici bulunamadi." });
            }

            var invitation = await _invitationRepository.GetByCodeAsync(invitationCode);
            if (invitation == null)
            {
                return BadRequest(new { message = "Gecersiz davet linki." });
            }

            if (invitation.ExpiresAt.HasValue && invitation.ExpiresAt.Value < DateTime.UtcNow)
            {
                return BadRequest(new { message = "Davet linkinin suresi dolmustur." });
            }

            if (!string.IsNullOrWhiteSpace(invitation.Email) &&
                !string.Equals(invitation.Email.Trim(), user.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "Bu davet farkli bir e-posta adresine gonderilmis." });
            }

            var alreadyMember = await _houseRepository.IsActiveMemberAsync(invitation.HouseId, userId);
            if (!alreadyMember)
            {
                if (!string.Equals(invitation.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { message = "Bu davet daha once kullanilmis." });
                }

                await _houseRepository.AddMemberAsync(new HouseMember
                {
                    HouseId = invitation.HouseId,
                    UserId = userId,
                    JoinedDate = DateTime.UtcNow,
                    IsActive = true
                });
            }

            invitation.Status = "Accepted";
            invitation.AcceptedByUserId = userId;
            invitation.AcceptedAt = DateTime.UtcNow;
            await _invitationRepository.UpdateAsync(invitation);

            return Ok(new
            {
                success = true,
                message = alreadyMember ? "Zaten bu evin uyesisiniz." : "Eve basariyla katildiniz.",
                houseId = invitation.HouseId
            });
        }

        [HttpGet("{houseId}/members")]
        public async Task<IActionResult> GetHouseMembers(int houseId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();
            if (!await _houseRepository.IsActiveMemberAsync(houseId, currentUserId.Value))
                return Forbid();

            var result = await _mediator.Send(new GetHouseMembersWithDebtsQuery { HouseId = houseId });
            return Ok(result);
        }

        [HttpDelete("{houseId}/members/{userId}")]
        public async Task<IActionResult> RemoveMember(int houseId, int userId)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdClaim, out var requestingUserId) || requestingUserId <= 0)
                return Unauthorized(new { message = "Gecerli kullanici kimligi bulunamadi." });

            try
            {
                await _mediator.Send(new LeaveHouseCommand
                {
                    HouseId = houseId,
                    UserId = userId,
                    RequestingUserId = requestingUserId
                });
                return Ok(new { success = true, message = "Uye evden cikarildi." });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { message = "Kullanici veya ev bulunamadi." });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpDelete("{houseId:int}")]
        public async Task<IActionResult> DeleteHouse(int houseId)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!int.TryParse(userIdClaim, out var requestingUserId) || requestingUserId <= 0)
                return Unauthorized(new { message = "Gecerli kullanici kimligi bulunamadi." });

            try
            {
                await _mediator.Send(new DeleteHouseCommand
                {
                    HouseId = houseId,
                    RequestingUserId = requestingUserId
                });
                return Ok(new { success = true, message = "Ev grubu silindi." });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { message = "Ev bulunamadi." });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }

        [HttpGet("GetUserHouses/{userId}")]
        public async Task<IActionResult> GetUserHouses(int userId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();
            if (currentUserId.Value != userId)
                return Forbid();

            var result = await _mediator.Send(new GetUserHousesQuery { UserId = userId });
            if (!result.Success) return BadRequest(result.Message);
            return Ok(result.Data);
        }

        [HttpGet("GetUserDebts/{userId:int}/{houseId:int}")]
        public async Task<IActionResult> GetUserDebts(int userId, int houseId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();
            if (currentUserId.Value != userId)
                return Forbid();
            if (!await _houseRepository.IsActiveMemberAsync(houseId, currentUserId.Value))
                return Forbid();

            var result = await _mediator.Send(new GetUserDebtsQuery(userId, houseId));
            return Ok(result);
        }

        [HttpGet("GetUserDebtBetween/{houseId:int}")]
        public async Task<IActionResult> GetUserDebtBetween(int houseId, [FromQuery] int userAId, [FromQuery] int userBId)
        {
            var currentUserId = GetCurrentUserId();
            if (currentUserId is null)
                return Unauthorized();
            if (currentUserId.Value != userAId && currentUserId.Value != userBId)
                return Forbid();
            if (!await _houseRepository.IsActiveMemberAsync(houseId, currentUserId.Value)
                || !await _houseRepository.IsActiveMemberAsync(houseId, userAId)
                || !await _houseRepository.IsActiveMemberAsync(houseId, userBId))
                return Forbid();

            var result = await _mediator.Send(new GetUserDebtBetweenQuery { HouseId = houseId, UserAId = userAId, UserBId = userBId });
            return Ok(result);
        }

        private int? GetCurrentUserId()
        {
            var value = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub");
            return int.TryParse(value, out var userId) && userId > 0 ? userId : null;
        }
    }
}
