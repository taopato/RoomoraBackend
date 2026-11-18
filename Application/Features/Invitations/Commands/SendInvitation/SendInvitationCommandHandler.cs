using Application.Features.Invitations.Dtos;
using Application.Common.Email;
using Application.Services.Repositories;
using Core.Interfaces;
using Core.Utilities.Results;
using Domain.Entities;
using MediatR;
using Microsoft.Extensions.Configuration;

namespace Application.Features.Invitations.Commands.SendInvitation
{
    public class SendInvitationCommandHandler : IRequestHandler<SendInvitationCommand, Response<SendInvitationResponseDto>>
    {
        private readonly IInvitationRepository _invitationRepository;
        private readonly IMailService _mailService;
        private readonly string _webBaseUrl;

        public SendInvitationCommandHandler(
            IInvitationRepository invitationRepository,
            IMailService mailService,
            IConfiguration configuration)
        {
            _invitationRepository = invitationRepository;
            _mailService = mailService;
            _webBaseUrl = (configuration["AppUrls:WebBaseUrl"] ?? "https://roomora.takosware.com").TrimEnd('/');
        }

        public async Task<Response<SendInvitationResponseDto>> Handle(SendInvitationCommand request, CancellationToken cancellationToken)
        {
            var token = Guid.NewGuid().ToString("N")[..16].ToUpper();
            var expiresAt = DateTime.UtcNow.AddDays(7);

            var invitation = new Invitation
            {
                Email = request.Email,
                HouseId = request.HouseId,
                Token = token,
                SentAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                Status = "Pending"
            };

            await _invitationRepository.AddAsync(invitation);

            var inviteLink = $"{_webBaseUrl}/davetiye-kabul?token={token}&houseId={request.HouseId}&email={Uri.EscapeDataString(request.Email)}";

            await _mailService.SendEmailAsync(
                request.Email,
                "Roomora ev davetin",
                RoomoraEmailTemplate.Invitation(inviteLink, expiresAt));

            var response = new SendInvitationResponseDto
            {
                // Davet kodu yalnızca davet edilen e-postaya gider; davet edene dönülmez
                InvitationCode = string.Empty,
                ExpiresAt = expiresAt
            };

            return new Response<SendInvitationResponseDto>(response, true, "Davet başarıyla gönderildi.");
        }
    }
}
