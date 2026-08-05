using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bussola.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkillTag",
                table: "OnboardingSteps");

            migrationBuilder.AddColumn<int>(
                name: "SkillArea",
                table: "OnboardingSteps",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkillArea",
                table: "OnboardingSteps");

            migrationBuilder.AddColumn<string>(
                name: "SkillTag",
                table: "OnboardingSteps",
                type: "text",
                nullable: true);
        }
    }
}
