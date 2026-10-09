using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReceiptParticipantAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImageHeight",
                table: "Receipts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageWidth",
                table: "Receipts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParticipantUserIdsJson",
                table: "ReceiptItems",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageHeight",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "ImageWidth",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "ParticipantUserIdsJson",
                table: "ReceiptItems");
        }
    }
}
