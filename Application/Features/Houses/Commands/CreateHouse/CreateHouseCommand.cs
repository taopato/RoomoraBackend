using Application.Features.Houses.Dtos;
using MediatR;
using Domain.Enums;

namespace Application.Features.Houses.Commands.CreateHouse
{
    public class CreateHouseCommand : IRequest<CreatedHouseDto>
    {
        public string Name { get; set; } = string.Empty;
        public int CreatorUserId { get; set; }
        public GroupType GroupType { get; set; } = GroupType.Home;
        public DateTime? TripStartDate { get; set; }
        public DateTime? TripEndDate { get; set; }
    }
}
