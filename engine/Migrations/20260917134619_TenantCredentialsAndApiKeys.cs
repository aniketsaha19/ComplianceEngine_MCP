using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ComplianceEngine.Migrations
{
    /// <inheritdoc />
    public partial class TenantCredentialsAndApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_ApiKeyHash",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ApiKeyHash",
                table: "Tenants");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Tenants",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Tenants",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Email",
                table: "Tenants",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_TenantId",
                table: "ApiKeys",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_Email",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Tenants");

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyHash",
                table: "Tenants",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_ApiKeyHash",
                table: "Tenants",
                column: "ApiKeyHash",
                unique: true);
        }
    }
}
