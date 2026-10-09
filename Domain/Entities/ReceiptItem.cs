namespace Domain.Entities
{
    public class ReceiptItem
    {
        public int Id { get; set; }

        public int ReceiptId { get; set; }
        public Receipt Receipt { get; set; } = null!;

        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal Quantity { get; set; } = 1;
        public decimal LineTotal { get; set; }
        public int? BoxLeft { get; set; }
        public int? BoxTop { get; set; }
        public int? BoxWidth { get; set; }
        public int? BoxHeight { get; set; }
        public bool IsAssigned { get; set; }

        public bool IsShared { get; set; } = true;
        public int? PersonalUserId { get; set; }
        public string? ParticipantUserIdsJson { get; set; }

        public int SortOrder { get; set; }
    }
}
