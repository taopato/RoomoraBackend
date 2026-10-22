using Application.Features.Houses.Dtos;
using Application.Services.Repositories;
using Core.Utilities.Results;
using Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Application.Features.Houses.Queries.GetUserHouses
{
    public class GetUserHousesQueryHandler
        : IRequestHandler<GetUserHousesQuery, Response<List<UserHouseListDto>>>
    {
        private readonly IHouseMemberRepository _houseMemberRepo;

        public GetUserHousesQueryHandler(IHouseMemberRepository houseMemberRepo)
        {
            _houseMemberRepo = houseMemberRepo;
        }

        public async Task<Response<List<UserHouseListDto>>> Handle(
            GetUserHousesQuery request,
            CancellationToken cancellationToken)
        {
            var data = await _houseMemberRepo
                .Query()
                .Include(hm => hm.House)
                .ThenInclude(h => h.CreatorUser)
                .Where(hm => hm.UserId == request.UserId)
                .Select(hm => new UserHouseListDto
                {
                    Id = hm.House.Id,
                    Name = hm.House.Name,
                    CoverImageUrl = hm.House.CoverImageUrl,
                    GroupType = hm.House.GroupType,
                    TripStartDate = hm.House.TripStartDate,
                    TripEndDate = hm.House.TripEndDate,
                    CreatorUserId = hm.House.CreatorUserId,
                    CreatorFullName = hm.House.CreatorUser != null
                        ? $"{hm.House.CreatorUser.FirstName} {hm.House.CreatorUser.LastName}"
                        : "Bilinmiyor"
                })
                .ToListAsync(cancellationToken);

            return new Response<List<UserHouseListDto>>(data, true);
        }
    }
}
