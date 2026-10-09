using System.ComponentModel.DataAnnotations;
using Domain.Enums;

namespace Domain.Entities
{
    public class Receipt
    {
        public int Id { get; set; }

        public int HouseId { get; set; }
        public House House { get; set; } = null!;

        public int UploadedByUserId { get; set; }
        public User UploadedByUser { get; set; } = null!;

        [MaxLength(1024)]
        public string ImageUrl { get; set; } = string.Empty;

        public string? RawOcrText { get; set; }

        [MaxLength(256)]
        public string? StoreName { get; set; }

        public DateTime? ReceiptDate { get; set; }
        public decimal? DetectedTotalAmount { get; set; }
        public int? ImageWidth { get; set; }
        public int? ImageHeight { get; set; }

        public ReceiptStatus Status { get; set; } = ReceiptStatus.Uploaded;

        public int? ConvertedExpenseId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        public ICollection<ReceiptItem> Items { get; set; } = new List<ReceiptItem>();
    }
}
