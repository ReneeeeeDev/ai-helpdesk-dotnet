# AGENTS.md: instrucciones para asistentes de código (Claude Code, Copilot, Cursor)

Este archivo es la **fuente única** de convenciones del repositorio. `CLAUDE.md`,
`.github/copilot-instructions.md` y `.cursor/rules/` lo referencian. Si cambias una
convención, cámbiala **aquí**.

## Qué es SupportAI
Mesa de ayuda multi-tenant. Los tickets se clasifican con Claude (triage en JSON validado
por esquema), los agentes reciben respuestas sugeridas con citas a la base de conocimiento,
resúmenes en streaming y un copiloto con tool use. Stack: .NET 9, ASP.NET Core Web API,
EF Core 9 + SQL Server, Blazor (MudBlazor), SDK oficial `Anthropic` para C#.

## Comandos
```bash
dotnet build SupportAI.sln                      # compila (TreatWarningsAsErrors activo)
dotnet test SupportAI.sln                       # todos los tests (integración requiere SQL Server)
dotnet test --filter "Category!=Integration"    # solo unitarios y de arquitectura
dotnet format SupportAI.sln                     # aplica formato (.editorconfig)
dotnet ef migrations add <Nombre> -p src/SupportAI.Infrastructure -s src/SupportAI.Api -o Persistence/Migrations
dotnet ef database update -p src/SupportAI.Infrastructure -s src/SupportAI.Api
dotnet run --project src/SupportAI.Api           # API  → https://localhost:7180/scalar
dotnet run --project src/SupportAI.Web           # Web  → https://localhost:7200
```
Las herramientas locales (`dotnet-ef`, `husky`) se restauran con `dotnet tool restore`.

## Arquitectura (Clean Architecture; las reglas se verifican en `tests/SupportAI.Architecture.Tests`)
```
Domain  ←  Application  ←  Infrastructure  ←  Api
Contracts (DTOs)  ←  Application, Api, Web
```
- **Domain**: entidades, value objects, eventos de dominio y reglas. **Sin** EF Core, ASP.NET ni paquetes NuGet.
  Las invariantes se protegen en métodos de la entidad (`ticket.ChangeStatus(...)`), nunca con setters públicos.
- **Application**: casos de uso CQRS (`ICommand<T>`/`IQuery<T>` + handler), validadores FluentValidation e
  interfaces de puertos (`ILlmClient`, `IPromptRegistry`, `ITenantContext`, `IApplicationDbContext`).
  No depende de Infrastructure ni de ASP.NET Core.
- **Infrastructure**: EF Core (`SupportAIDbContext`), interceptores, outbox, Identity/JWT, cliente Claude,
  prompts versionados y background workers.
- **Api**: controllers delgados. Solo traducen HTTP ↔ comandos/queries; nada de lógica de negocio.
- **Web**: Blazor Server. Solo habla con la API (vía `SupportAI.Contracts`), **nunca** con la BD.

No usamos MediatR (ahora tiene licencia comercial) ni AutoMapper. Tenemos un dispatcher propio en
`Application/Abstractions/Messaging` y un mapeo manual con métodos `ToDto()`.

## Convenciones de C#
- Namespaces file-scoped, `var` cuando el tipo es evidente, campos privados `_camelCase`.
- `record` para DTOs/comandos/queries; `sealed` por defecto en clases que no se heredan.
- Async de punta a punta con `CancellationToken` en toda firma pública asíncrona.
- Tiempo: inyectar `TimeProvider`, **nunca** `DateTime.UtcNow` directo (los tests usan `FakeTimeProvider`).
- Errores de negocio: `DomainException` (→ 422), `NotFoundException` (→ 404), `ForbiddenException` (→ 403),
  `ConflictException` (→ 409). El `GlobalExceptionHandler` los convierte en ProblemDetails (RFC 9457).
- Logging estructurado con plantillas (`logger.LogInformation("Ticket {TicketId} triaged", id)`), sin interpolación.

## EF Core
- Una clase `IEntityTypeConfiguration<T>` por entidad en `Infrastructure/Persistence/Configurations`.
- Enums se guardan como string. Las entidades multi-tenant implementan `ITenantOwned` y reciben un filtro global.
- Lecturas: `AsNoTracking()` + proyección a DTO. Escrituras: cargar el agregado, invocar el método de dominio y `SaveChangesAsync`.
- Los eventos de dominio se guardan en el **outbox** dentro de la misma transacción (interceptor). Nunca publiques efectos
  secundarios (LLM, notificaciones) directamente desde un handler de escritura.
- Si al hacer rebase hay conflicto en `SupportAIDbContextModelSnapshot.cs`: **no lo resuelvas a mano**. Elimina tu
  migración, acepta el snapshot de `main` y regenera la migración (ver `docs/git-workflow.md`).

## LLM / Claude (lee esto antes de tocar `Infrastructure/Ai`)
- El modelo por defecto es `claude-opus-5-5` y se configura por prompt en `meta.json`. El thinking es adaptativo y no se
  puede desactivar en este modelo; la profundidad se controla con `effort` (`low` para triage, `medium` para respuestas).
- **No** se usa prefill de assistant ni `tool_choice` forzado (ambos devuelven 400 en este modelo). Para obtener JSON se usa
  `output_config.format` (structured outputs) y para herramientas `strict: true` con `tool_choice: auto`.
- Los prompts están versionados en `Infrastructure/Ai/Prompts/{key}/v{n}/` (`system.md`, `user.md`, `schema.json`, `meta.json`).
  **Nunca edites una versión existente**: crea `v{n+1}` y actívala desde admin. Así cada `LlmCallLog` sabe qué versión se usó.
- El contenido del usuario siempre va dentro de etiquetas XML (`<ticket>…</ticket>`) y se trata como dato no confiable.
- Antes de enviar texto al LLM pasa por `IPiiRedactor`.
- Toda llamada pasa por `StructuredCompletionService`/`ILlmClient` y queda registrada en `LlmCallLog` (tokens, caché, costo).
- Los tests **nunca** llaman a la API real: usan `FakeLlmClient`. La evaluación real vive en `tests/SupportAI.PromptEvals` (cuesta dinero, es manual).
- Secretos: `ANTHROPIC_API_KEY` / `Anthropic:ApiKey` solo por user-secrets o variables de entorno. Jamás en appsettings ni en commits.

## Tests
- xUnit + Shouldly + NSubstitute. Nombre: `Metodo_Escenario_ResultadoEsperado`.
- Los tests de integración (`[Trait("Category","Integration")]`) usan SQL Server real (LocalDB en local, servicio `mssql` en CI) y Respawn.
- Todo bug corregido lleva un test que lo reproduce.

## Git y GitLab
- Ramas cortas desde `main`: `feat/…`, `fix/…`, `chore/…`, `docs/…`.
- Commits en **Conventional Commits** (validado por el hook `commit-msg`). `pre-commit` corre `dotnet format --verify-no-changes`.
- Antes de abrir o actualizar un MR: `git fetch && git rebase origin/main`. El merge es fast-forward.
- Usa las plantillas de `.gitlab/merge_request_templates`. Si un cambio fue asistido por IA, decláralo en el MR.
- Los cambios en `Ai/Prompts/**` requieren correr el job manual `prompt-eval` y adjuntar el resultado en el MR.
