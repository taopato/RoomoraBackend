using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Application.Features.Auths.Dtos;
using Application.Common.Email;
using Application.Services.Repositories;
using Core.Interfaces;
using Domain.Entities;
using MediatR;

namespace Application.Features.Auths.Commands.SendVerificationCode
{
    public class SendVerificationCodeCommandHandler
        : IRequestHandler<SendVerificationCodeCommand, SendVerificationCodeResponseDto>
    {
        private readonly IVerificationCodeRepository _codeRepo;
        private readonly IMailService _mailService;
        private readonly IUserRepository _userRepo;

        public SendVerificationCodeCommandHandler(
            IVerificationCodeRepository codeRepo,
            IMailService mailService,
            IUserRepository userRepo)
        {
            _codeRepo = codeRepo;
            _mailService = mailService;
            _userRepo = userRepo;
        }

        public async Task<SendVerificationCodeResponseDto> Handle(
            SendVerificationCodeCommand request,
            CancellationToken cancellationToken)
        {
            var purpose = string.IsNullOrWhiteSpace(request.Purpose)
                ? "register"
                : request.Purpose.Trim().ToLowerInvariant();

            var existingUser = await _userRepo.GetByEmailAsync(request.Email);
            if (purpose == "register" && existingUser != null)
                throw new InvalidOperationException("Bu e-posta zaten kayıtlı. Lütfen Şifremi unuttum akışını kullanın.");

            if (purpose == "reset" && existingUser == null)
                throw new InvalidOperationException("Bu e-posta ile kayıtlı bir hesap bulunamadı.");

            // Önceki kodlar geçersiz olsun; aynı anda tek kod geçerli
            await _codeRepo.DeleteByEmailAsync(request.Email);
            var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

            var entity = new VerificationCode
            {
                Email = request.Email,
                Code = code,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10)
            };
            await _codeRepo.AddAsync(entity);

            await _mailService.SendEmailAsync(
                request.Email,
                purpose == "reset" ? "Roomora şifre yenileme kodun" : "Roomora doğrulama kodun",
                RoomoraEmailTemplate.VerificationCode(code, purpose));

            return new SendVerificationCodeResponseDto();
        }
    }
}
