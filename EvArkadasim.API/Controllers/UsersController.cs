using Application.Features.Users.Commands.UpdateProfile;
using Application.Services.Repositories;
using Application.Services.Accounts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Persistence.Contexts;
using System.Security.Claims;

namespace WebAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly IUserRepository _userRepository;
        private readonly IWebHostEnvironment _environment;
        private readonly IAccountDeletionService _accountDeletionService;
        private readonly AppDbContext _dbContext;

        public UsersController(
            IMediator mediator,
            IUserRepository userRepository,
            IWebHostEnvironment environment,
            IAccountDeletionService accountDeletionService,
            AppDbContext dbContext)
        {
            _mediator = mediator;
            _userRepository = userRepository;
            _environment = environment;
            _accountDeletionService = accountDeletionService;
            _dbContext = dbContext;
        }

        /// <summary>
        /// PUT /api/Users/{userId}/Profile
        /// Kullanıcı profili günceller: ad soyad ve/veya şifre.
        /// </summary>
        [HttpPut("{userId:int}/Profile")]
        [Authorize]
        public async Task<IActionResult> UpdateProfile(int userId, [FromBody] UpdateProfileRequest request)
        {
            if (GetCurrentUserId() != userId)
                return Forbid();

            var result = await _mediator.Send(new UpdateProfileCommand(
                userId,
                request.FullName,
                request.PhoneNumber,
                request.Iban,
                null,
                request.CurrentPassword,
                request.NewPassword
            ));
            // Şifre değiştiyse açık oturumlar kapatılır
            if (!string.IsNullOrEmpty(request.NewPassword))
                await _dbContext.RefreshTokens
                    .Where(token => token.UserId == userId && token.RevokedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, DateTime.UtcNow));
            return Ok(result);
        }

        [HttpPost("{userId:int}/ProfileImage")]
        [Authorize]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(8_000_000)]
        public async Task<IActionResult> UploadProfileImage(
            int userId,
            IFormFile? image,
            CancellationToken cancellationToken)
        {
            if (GetCurrentUserId() != userId)
                return Forbid();
            if (image is null || image.Length == 0)
                return BadRequest(new { message = "Profil fotoğrafı zorunludur." });
            if (image.Length > 8_000_000)
                return BadRequest(new { message = "Profil fotoğrafı en fazla 8 MB olabilir." });

            var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/jpeg", "image/png", "image/webp"
            };
            if (!allowedTypes.Contains(image.ContentType))
                return BadRequest(new { message = "Yalnızca JPG, PNG veya WEBP fotoğraf yükleyebilirsiniz." });
            if (!await HasValidImageSignatureAsync(image, cancellationToken))
                return BadRequest(new { message = "Profil fotoğrafının dosya içeriği geçerli değil." });

            var extension = image.ContentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => ".jpg"
            };

            var user = await _userRepository.GetByIdAsync(userId);
            if (user is null) return NotFound(new { message = "Kullanıcı bulunamadı." });

            var webRoot = _environment.WebRootPath
                ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
            var uploadDirectory = Path.Combine(webRoot, "uploads", "profiles");
            Directory.CreateDirectory(uploadDirectory);

            var fileName = $"user-{userId}-{Guid.NewGuid():N}{extension}";
            var fullPath = Path.Combine(uploadDirectory, fileName);
            try
            {
                await using (var target = System.IO.File.Create(fullPath))
                    await image.CopyToAsync(target, cancellationToken);

                var oldImageUrl = user.ProfileImageUrl;
                var imageUrl = $"/uploads/profiles/{fileName}";
                var result = await _mediator.Send(new UpdateProfileCommand(
                    userId,
                    null,
                    null,
                    null,
                    imageUrl,
                    null,
                    null), cancellationToken);

                DeleteOldProfileImage(webRoot, oldImageUrl, imageUrl);
                return Ok(result);
            }
            catch
            {
                if (System.IO.File.Exists(fullPath))
                    System.IO.File.Delete(fullPath);
                throw;
            }
        }

        [HttpDelete("{userId:int}/Account")]
        [Authorize]
        public async Task<IActionResult> DeleteAccount(int userId, CancellationToken cancellationToken)
        {
            if (GetCurrentUserId() != userId)
                return Forbid();

            var result = await _accountDeletionService.DeleteAsync(userId, cancellationToken);
            var webRoot = _environment.WebRootPath
                ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
            DeleteOldProfileImage(webRoot, result.ProfileImageUrl, string.Empty);
            return NoContent();
        }

        private int GetCurrentUserId()
        {
            var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;
            if (!int.TryParse(raw, out var userId) || userId <= 0)
                throw new UnauthorizedAccessException("Geçerli kullanıcı bilgisi bulunamadı.");
            return userId;
        }

        private static void DeleteOldProfileImage(string webRoot, string? oldUrl, string newUrl)
        {
            if (string.IsNullOrWhiteSpace(oldUrl) || oldUrl == newUrl ||
                !oldUrl.StartsWith("/uploads/profiles/", StringComparison.OrdinalIgnoreCase))
                return;

            var relative = oldUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(webRoot, relative));
            var profileRoot = Path.GetFullPath(Path.Combine(webRoot, "uploads", "profiles"));
            var rootPrefix = profileRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(fullPath))
                System.IO.File.Delete(fullPath);
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
    }

    public record UpdateProfileRequest(
        string? FullName,
        string? PhoneNumber,
        string? Iban,
        string? CurrentPassword,
        string? NewPassword
    );
}
