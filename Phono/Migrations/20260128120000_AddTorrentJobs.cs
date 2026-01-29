using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phono.Migrations
{
    /// <inheritdoc />
    public partial class AddTorrentJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TorrentJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MagnetLink = table.Column<string>(type: "TEXT", nullable: false),
                    TorrentHash = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    DownloadSpeed = table.Column<long>(type: "INTEGER", nullable: false),
                    Seeds = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastProgressAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TorrentJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TorrentJobs_CreatedAt",
                table: "TorrentJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TorrentJobs_Status",
                table: "TorrentJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TorrentJobs");
        }
    }
}
