---
description: Agrega una migración de EF Core y revisa el SQL generado
argument-hint: <NombreMigracion en PascalCase>
---

1. Compila: `dotnet build src/SupportAI.Api`.
2. Crea la migración:
   `dotnet ef migrations add $ARGUMENTS -p src/SupportAI.Infrastructure -s src/SupportAI.Api -o Persistence/Migrations`
3. Genera el script idempotente y revísalo:
   `dotnet ef migrations script --idempotent -p src/SupportAI.Infrastructure -s src/SupportAI.Api -o artifacts/migration.sql`
4. Señala al usuario operaciones destructivas (DROP COLUMN, cambios de tipo, renombres que EF interpretó como drop+add).
5. No apliques la migración a ninguna base de datos sin confirmación.
