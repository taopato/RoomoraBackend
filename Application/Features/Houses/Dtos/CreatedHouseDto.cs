namespace Application.Features.Houses.Dtos
{
    public class CreatedHouseDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? CoverImageUrl { get; set; }
        public Domain.Enums.GroupType GroupType { get; set; }
        public DateTime? TripStartDate { get; set; }
        public DateTime? TripEndDate { get; set; }
    }
}
