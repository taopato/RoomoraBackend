using Domain.Entities;
using MediatR;
using Application.Features.Houses.Dtos;
using Application.Services.Repositories;

namespace Application.Features.Houses.Commands.CreateHouse
{
    public class CreateHouseCommandHandler
        : IRequestHandler<CreateHouseCommand, CreatedHouseDto>
    {
        private readonly IHouseRepository _houseRepository;

        public CreateHouseCommandHandler(IHouseRepository houseRepository)
        {
            _houseRepository = houseRepository;
        }

        public async Task<CreatedHouseDto> Handle(CreateHouseCommand request, CancellationToken cancellationToken)
        {
            // 1) Yeni house oluştur
            var entity = new House
            {
                Name = request.Name,
                CreatorUserId = request.CreatorUserId,
                GroupType = request.GroupType,
                TripStartDate = request.GroupType == Domain.Enums.GroupType.Trip ? request.TripStartDate : null,
                TripEndDate = request.GroupType == Domain.Enums.GroupType.Trip ? request.TripEndDate : null,
            };

            // 2) Kaydet
            var added = await _houseRepository.AddAsync(entity);

            // 3) Oluşturan kullanıcıyı üyeye çevir
            var member = new HouseMember
            {
                HouseId = added.Id,
                UserId = request.CreatorUserId
            };
            await _houseRepository.AddMemberAsync(member);

            // 4) DTO dön
            return new CreatedHouseDto
            {
                Id = added.Id,
                Name = added.Name,
                GroupType = added.GroupType,
                TripStartDate = added.TripStartDate,
                TripEndDate = added.TripEndDate,
            };
        }
    }
}
