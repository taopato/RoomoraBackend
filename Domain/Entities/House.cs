using System;
using System.Collections.Generic;
using Domain.Enums;

namespace Domain.Entities
{
    public class House
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
        public string? CoverImageUrl { get; set; }
        public GroupType GroupType { get; set; } = GroupType.Home;
        public DateTime? TripStartDate { get; set; }
        public DateTime? TripEndDate { get; set; }
        public string CurrencyCode { get; set; } = "TRY";
        public bool IsArchived { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        // Bir ev grubunun üyeleri
        public ICollection<HouseMember> HouseMembers { get; set; } = new List<HouseMember>();

        // Evi oluşturan kullanıcı
        public int CreatorUserId { get; set; }
        public virtual User? CreatorUser { get; set; }
    }
}
