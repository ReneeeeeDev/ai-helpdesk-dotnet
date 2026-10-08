using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupportAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PriorityAsInt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Conversión de datos: los nombres del enum pasan a su valor numérico antes de cambiar el tipo.
            // EF Core no puede inferir esto; sin este paso el ALTER COLUMN falla con los datos existentes.
            foreach (var table in new[] { "Tickets", "AiTriageResults" })
            {
                migrationBuilder.Sql($"""
                    UPDATE [{table}] SET [Priority] = CASE [Priority]
                        WHEN 'Low' THEN '0' WHEN 'Normal' THEN '1' WHEN 'High' THEN '2' WHEN 'Urgent' THEN '3'
                        ELSE [Priority] END;
                    """);
            }

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "Tickets",
                type: "int",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<int>(
                name: "Priority",
                table: "AiTriageResults",
                type: "int",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(40)",
                oldMaxLength: 40);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Priority",
                table: "Tickets",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldMaxLength: 40);

            migrationBuilder.AlterColumn<string>(
                name: "Priority",
                table: "AiTriageResults",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldMaxLength: 40);

            foreach (var table in new[] { "Tickets", "AiTriageResults" })
            {
                migrationBuilder.Sql($"""
                    UPDATE [{table}] SET [Priority] = CASE [Priority]
                        WHEN '0' THEN 'Low' WHEN '1' THEN 'Normal' WHEN '2' THEN 'High' WHEN '3' THEN 'Urgent'
                        ELSE [Priority] END;
                    """);
            }
        }
    }
}
