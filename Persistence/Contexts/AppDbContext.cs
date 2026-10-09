using Microsoft.EntityFrameworkCore;
using Domain.Entities;
using Domain.Enums;
using Persistence.Configurations; // <-- eklendi

namespace Persistence.Contexts
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> opts)
            : base(opts) { }

        public DbSet<Expense> Expenses { get; set; }
        public DbSet<House> Houses { get; set; }
        public DbSet<HouseMember> HouseMembers { get; set; }
        public DbSet<HouseNoteSection> HouseNoteSections { get; set; }
        public DbSet<HouseNoteItem> HouseNoteItems { get; set; }
        public DbSet<Invitation> Invitations { get; set; }
        public DbSet<Payment> Payments { get; set; } = null!;
        public DbSet<PersonalExpense> PersonalExpenses { get; set; }
        public DbSet<Share> Shares { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<VerificationCode> VerificationCodes { get; set; }
        public DbSet<Bill> Bills { get; set; }
        public DbSet<BillShare> BillShares { get; set; }
        public DbSet<BillDocument> BillDocuments { get; set; }

        // ✔ LedgerLine kullanıyoruz (LedgerEntry yerine)
        public DbSet<LedgerLine> LedgerLines { get; set; }
        public DbSet<PaymentAllocation> PaymentAllocations { get; set; }

        public DbSet<RecurringCharge> RecurringCharges { get; set; } = null!;
        public DbSet<ChargeCycle> ChargeCycles { get; set; } = null!;
        public DbSet<ChargeCycleShare> ChargeCycleShares { get; set; } = null!;
        public DbSet<Receipt> Receipts { get; set; } = null!;
        public DbSet<ReceiptItem> ReceiptItems { get; set; } = null!;
        public DbSet<RefreshToken> RefreshTokens { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Expense: precision and restrict cascade for two user FKs
            modelBuilder.Entity<Expense>(eb =>
            {
                eb.Property(e => e.Tutar).HasPrecision(18, 2);
                eb.Property(e => e.OrtakHarcamaTutari).HasPrecision(18, 2);

                eb.HasOne(e => e.OdeyenUser)
                  .WithMany()
                  .HasForeignKey(e => e.OdeyenUserId)
                  .OnDelete(DeleteBehavior.Restrict);

                eb.HasOne(e => e.KaydedenUser)
                  .WithMany()
                  .HasForeignKey(e => e.KaydedenUserId)
                  .OnDelete(DeleteBehavior.Restrict);
            });

            // Payment: precision and restrict cascade
            modelBuilder.Entity<Payment>(pb =>
            {
                pb.Property(p => p.Tutar).HasPrecision(18, 2);
                pb.Property(p => p.RowVersion).IsRowVersion();

                // Enum int olarak saklanır, default BankTransfer
                pb.Property(p => p.PaymentMethod)
                  .HasConversion<int>()
                  .HasDefaultValue(PaymentMethod.BankTransfer)
                  .HasSentinel((PaymentMethod)0);

                // PaymentStatus enum mapping
                pb.Property(p => p.Status)
                  .HasConversion<int>()
                  .HasDefaultValue(PaymentStatus.Pending)
                  .HasSentinel((PaymentStatus)0);

                pb.Property(p => p.DekontUrl).HasMaxLength(400);
            });

            // PersonalExpense: precision
            modelBuilder.Entity<PersonalExpense>(peb =>
            {
                peb.Property(pe => pe.Tutar).HasPrecision(18, 2);
            });

            // Share: precision
            modelBuilder.Entity<Share>(sb =>
            {
                sb.Property(s => s.PaylasimTutar).HasPrecision(18, 2);
                sb.Property(s => s.Date).HasColumnName("Date");
                sb.HasOne(s => s.Expense)
                  .WithMany(e => e.Shares)
                  .HasForeignKey(s => s.ExpenseId)
                  .OnDelete(DeleteBehavior.Cascade);
                sb.HasOne(s => s.User)
                  .WithMany()
                  .HasForeignKey(s => s.UserId)
                  .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<HouseMember>()
                .HasKey(hm => new { hm.HouseId, hm.UserId });

            modelBuilder.Entity<RefreshToken>(token =>
            {
                token.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
                token.Property(x => x.ReplacedByTokenHash).HasMaxLength(64);
                token.HasIndex(x => x.TokenHash).IsUnique();
                token.HasIndex(x => new { x.UserId, x.ExpiresAt });
                token.HasOne(x => x.User)
                    .WithMany()
                    .HasForeignKey(x => x.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<HouseNoteSection>(section =>
            {
                section.Property(x => x.Title).HasMaxLength(80).IsRequired();
                section.HasOne(x => x.House)
                    .WithMany()
                    .HasForeignKey(x => x.HouseId)
                    .OnDelete(DeleteBehavior.Cascade);
                section.HasOne(x => x.CreatedByUser)
                    .WithMany()
                    .HasForeignKey(x => x.CreatedByUserId)
                    .OnDelete(DeleteBehavior.Restrict);
                section.HasMany(x => x.Items)
                    .WithOne(x => x.Section)
                    .HasForeignKey(x => x.SectionId)
                    .OnDelete(DeleteBehavior.Cascade);
                section.HasIndex(x => new { x.HouseId, x.DeletedAt, x.Title });
            });

            modelBuilder.Entity<HouseNoteItem>(item =>
            {
                item.Property(x => x.Content).HasMaxLength(280).IsRequired();
                item.HasOne(x => x.CreatedByUser)
                    .WithMany()
                    .HasForeignKey(x => x.CreatedByUserId)
                    .OnDelete(DeleteBehavior.Restrict);
                item.HasOne(x => x.CompletedByUser)
                    .WithMany()
                    .HasForeignKey(x => x.CompletedByUserId)
                    .OnDelete(DeleteBehavior.Restrict);
                item.HasIndex(x => new { x.SectionId, x.IsCompleted, x.DeletedAt });
            });

            modelBuilder.Entity<House>()
                .HasOne(h => h.CreatorUser)
                .WithMany()
                .HasForeignKey(h => h.CreatorUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Expense>()
                .HasOne(e => e.OdeyenUser)
                .WithMany()
                .HasForeignKey(e => e.OdeyenUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Payment>()
                .HasOne(p => p.BorcluUser)
                .WithMany()
                .HasForeignKey(p => p.BorcluUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Payment>()
                .HasOne(p => p.AlacakliUser)
                .WithMany()
                .HasForeignKey(p => p.AlacakliUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Payment>(eb =>
            {
                eb.Property(p => p.AlacakliOnayi)
                  .HasColumnType("bit")
                  .IsRequired();
            });

            modelBuilder.Entity<Bill>(eb =>
            {
                eb.Property(x => x.Amount).HasColumnType("decimal(18,2)");
                eb.Property(x => x.Month).HasMaxLength(7).IsRequired(); // "YYYY-MM"
                eb.HasIndex(x => new { x.HouseId, x.UtilityType, x.Month }).IsUnique();

                eb.HasMany(x => x.Shares)
                  .WithOne(s => s.Bill)
                  .HasForeignKey(s => s.BillId)
                  .OnDelete(DeleteBehavior.Cascade);

                eb.HasMany(x => x.Documents)
                  .WithOne(d => d.Bill)
                  .HasForeignKey(d => d.BillId)
                  .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<BillShare>(eb =>
            {
                eb.Property(x => x.ShareAmount).HasColumnType("decimal(18,2)");
            });

            modelBuilder.Entity<BillDocument>(eb =>
            {
                eb.Property(x => x.FileName).HasMaxLength(260);
                eb.Property(x => x.FilePathOrUrl).HasMaxLength(1024);
            });

            // ✔ LedgerLine (Amount/PaidAmount precision + index)
            modelBuilder.Entity<LedgerLine>(le =>
            {
                le.Property(x => x.Amount).HasColumnType("decimal(18,2)");
                le.Property(x => x.PaidAmount).HasColumnType("decimal(18,2)");
                le.Property(x => x.RowVersion).IsRowVersion();
                le.HasIndex(x => new { x.HouseId, x.FromUserId, x.ToUserId, x.CreatedAt });
            });

            modelBuilder.Entity<PaymentAllocation>(pa =>
            {
                pa.Property(x => x.Amount).HasColumnType("decimal(18,2)");
                pa.HasIndex(x => x.PaymentId);
                // ⬇️ default değer (DB tarafı)
                pa.Property(x => x.CreatedAt)
                  .HasDefaultValueSql("GETUTCDATE()");
            });

            // Recurring / ChargeCycle
            modelBuilder.Entity<RecurringCharge>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Type).HasConversion<int>();
                b.Property(x => x.AmountMode).HasConversion<int>();
                b.Property(x => x.SplitPolicy).HasConversion<int>();
                b.Property(x => x.AccountingMode).HasConversion<int>();
                b.Property(x => x.Title).HasMaxLength(120);
                b.Property(x => x.Note).HasMaxLength(500);
                b.Property(x => x.ParticipantsJson).HasMaxLength(2000);
                b.Property(x => x.FixedAmount).HasPrecision(18, 2);
            });

            modelBuilder.Entity<ChargeCycle>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Status).HasConversion<int>();
                b.HasIndex(x => new { x.ContractId, x.Period }).IsUnique();
                b.Property(x => x.TotalAmount).HasPrecision(18, 2);
                b.Property(x => x.FundedAmount).HasPrecision(18, 2);
            });

            modelBuilder.Entity<ChargeCycleShare>(b =>
            {
                b.HasKey(x => x.Id);
                b.Property(x => x.Status).HasConversion<int>();
                b.Property(x => x.Amount).HasPrecision(18, 2);
                b.HasIndex(x => new { x.ChargeCycleId, x.UserId }).IsUnique();
                b.HasOne(x => x.ChargeCycle)
                    .WithMany(x => x.Shares)
                    .HasForeignKey(x => x.ChargeCycleId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Payment>(b =>
            {
                b.HasOne(p => p.Charge)
                 .WithMany()
                 .HasForeignKey(p => p.ChargeId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // Apply external configurations (Expense/LedgerLine)
            modelBuilder.ApplyConfiguration(new ExpenseConfiguration());
            modelBuilder.ApplyConfiguration(new LedgerLineConfiguration());

            // PaymentAllocation ↔ LedgerLine (FK: LedgerLineId - long)
            modelBuilder.Entity<PaymentAllocation>(b =>
            {
                b.HasKey(x => x.Id);

                b.HasOne(x => x.Payment)
                 .WithMany()
                 .HasForeignKey(x => x.PaymentId)
                 .OnDelete(DeleteBehavior.Cascade);

                b.HasOne(x => x.LedgerLine)
                 .WithMany()
                 .HasForeignKey(x => x.LedgerLineId)
                 .OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<PaymentAllocation>(pa =>
            {
                pa.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            });

            modelBuilder.Entity<Receipt>(rb =>
            {
                rb.Property(x => x.ImageUrl).HasMaxLength(1024).IsRequired();
                rb.Property(x => x.StoreName).HasMaxLength(256);
                rb.Property(x => x.DetectedTotalAmount).HasColumnType("decimal(18,2)");
                rb.Property(x => x.Status).HasConversion<int>();
                rb.HasIndex(x => x.HouseId);
                rb.HasIndex(x => x.UploadedByUserId);
                rb.HasIndex(x => x.CreatedAt);
                rb.HasIndex(x => new { x.HouseId, x.UploadedByUserId, x.CreatedAt });

                rb.HasOne(x => x.House)
                  .WithMany()
                  .HasForeignKey(x => x.HouseId)
                  .OnDelete(DeleteBehavior.Cascade);

                rb.HasOne(x => x.UploadedByUser)
                  .WithMany()
                  .HasForeignKey(x => x.UploadedByUserId)
                  .OnDelete(DeleteBehavior.Restrict);

                rb.HasMany(x => x.Items)
                  .WithOne(x => x.Receipt)
                  .HasForeignKey(x => x.ReceiptId)
                  .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ReceiptItem>(rib =>
            {
                rib.Property(x => x.Name).HasMaxLength(256).IsRequired();
                rib.Property(x => x.Price).HasColumnType("decimal(18,2)");
                rib.Property(x => x.Quantity).HasColumnType("decimal(18,2)");
                rib.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
                rib.Property(x => x.IsAssigned).HasDefaultValue(false);
                rib.Property(x => x.ParticipantUserIdsJson).HasMaxLength(2000);
            });

        }
    }
}
