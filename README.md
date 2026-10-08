# SupportAI: mesa de ayuda inteligente

Plataforma multi-tenant de soporte construida con **.NET 9**, **ASP.NET Core Web API**, **EF Core 9 + SQL Server**,
**Blazor** y **Claude** (SDK oficial `Anthropic`).

Cuando un cliente abre un ticket, Claude lo **clasifica** (categoría, prioridad, sentimiento, idioma, equipo, PII) en JSON
validado contra un esquema. Si la confianza supera el umbral del tenant, el triage se aplica automáticamente; si no, un
agente lo acepta o lo corrige, y esa corrección queda como métrica de precisión de la versión del prompt.
Los agentes además tienen **respuestas sugeridas con citas** a la base de conocimiento, **resúmenes en streaming** y un
**copiloto con tool use** que consulta tickets, SLA y artículos.

## Cómo cubre cada tecnología

| Tecnología | Dónde está |
|---|---|
| **C# / .NET 9** | Records, primary constructors, `TimeProvider`, `LoggerMessage`, `GeneratedRegex`, nullable + warnings como errores |
| **ASP.NET Core Web API** | `src/SupportAI.Api`: controllers v1 versionados, JWT + refresh rotativo, ProblemDetails, `Idempotency-Key`, rate limiting, SSE, SignalR, OpenAPI + Scalar, health checks |
| **EF Core** | `Infrastructure/Persistence`: filtros globales multi-tenant y soft-delete, interceptores (auditoría y outbox), rowversion, `SEQUENCE`, columnas JSON, índices filtrados, keyset paging, migración con conversión de datos |
| **SQL Server** | LocalDB en desarrollo, contenedor `mssql` en CI; `UPDLOCK/READPAST` para el outbox; búsqueda con `COLLATE ..._CI_AI` |
| **Integración de LLMs** | `Infrastructure/Ai`: `ClaudeLlmClient` (structured outputs, streaming, tool use, prompt caching, fallback del servidor), circuit breaker, presupuesto de tokens, costos en `LlmCallLog` |
| **Prompt engineering y JSON** | Prompts versionados e inmutables en `Ai/Prompts/{key}/v{n}`, JSON Schema con enums dinámicos por tenant, validación en capas + reparación, defensa contra prompt injection, redacción de PII, evaluación con dataset dorado |
| **Git** | Historia lineal por fases (ramas `feat/*` + rebase + fast-forward), Conventional Commits con hook, `rerere`, guía de conflictos (incluido el de `ModelSnapshot` de EF): [docs/git-workflow.md](docs/git-workflow.md) |
| **GitLab** | [.gitlab-ci.yml](.gitlab-ci.yml) (lint, build, tests con JUnit/cobertura, SQL Server como servicio, SAST, evaluación de prompts bloqueante, Docker, environments), plantillas de MR e issues, CODEOWNERS |
| **Asistentes de IA** | [AGENTS.md](AGENTS.md) como fuente única; [CLAUDE.md](CLAUDE.md), `.cursor/rules/`, `.github/copilot-instructions.md`, comandos de Claude Code en `.claude/commands/` |

## Arquitectura

```
                ┌──────────────── Blazor Server (MudBlazor) ────────────────┐
                │ Bandeja · Detalle · Copiloto · KB · Admin IA · Settings   │
                └──────────┬─────────────────────────────▲──────────────────┘
                     HTTP + JWT (cliente tipado)    SignalR (TicketChanged)  SSE (resumen, copiloto)
                ┌──────────▼─────────────────────────────┴──────────────────┐
                │ SupportAI.Api  ─ controllers v1 → ISender (CQRS propio)   │
                │   behaviors: logging → autorización → validación          │
                ├────────────────────────────────────────────────────────────┤
                │ SupportAI.Application  ─ casos de uso, servicios de IA    │
                │ SupportAI.Domain       ─ Ticket (estados + SLA), Tenant…  │
                ├────────────────────────────────────────────────────────────┤
                │ SupportAI.Infrastructure                                   │
                │   EF Core + SQL Server · Identity/JWT · Outbox · SLA       │
                │   ClaudeLlmClient ─(SDK Anthropic)─► Claude API            │
                └────────────────────────────────────────────────────────────┘
```

Flujo de un ticket nuevo: `POST /tickets` → `Ticket.Create` levanta `TicketCreated` → el interceptor lo escribe en
`OutboxMessages` **en la misma transacción** → `OutboxProcessor` lo consume → `TicketTriageService` llama a Claude con
salida estructurada → se valida, se aplica o se deja como sugerencia → SignalR avisa a la UI.
Detalle en [docs/architecture.md](docs/architecture.md) y en las [ADRs](docs/adr).

## Inicio rápido

Requisitos: .NET SDK 9 y SQL Server LocalDB (incluido con Visual Studio). No hace falta API key: por defecto se usa el
proveedor de IA **simulado** (`Llm:Provider=Fake`), determinista y gratuito.

```bash
dotnet tool restore                                   # dotnet-ef y husky (instala los git hooks)
dotnet run --project src/SupportAI.Api --launch-profile http    # http://localhost:5180/scalar/v1
dotnet run --project src/SupportAI.Web --launch-profile http    # http://localhost:5200
```

La API aplica las migraciones y carga datos demo al arrancar en Development. Cuentas (contraseña `Passw0rd!`):

| Usuario | Rol | Para ver |
|---|---|---|
| `admin@acme.test` | Admin | Uso y costos de IA, versiones de prompts, configuración |
| `agente@acme.test` | Agente (Soporte Técnico) | Bandeja, triage, respuestas sugeridas, copiloto |
| `facturacion@acme.test` | Agente (Facturación) | Asignaciones por equipo |
| `cliente@acme.test` | Cliente | Portal: mis tickets y crear ticket |
| `admin@globex.test` | Admin de otro tenant | Aislamiento multi-tenant |

Con Docker: `docker compose up --build` levanta SQL Server, API (`:8080`) y Web (`:8081`).

### Usar Claude real

```bash
dotnet user-secrets set "Llm:Provider" "Claude" --project src/SupportAI.Api
dotnet user-secrets set "Llm:ApiKey" "<tu API key>" --project src/SupportAI.Api
# o bien: variable de entorno ANTHROPIC_API_KEY (y Llm__Provider=Claude)
```

Modelo por defecto: `claude-opus-5-5` (configurable por prompt en `meta.json`), con `effort` bajo para el triage y medio
para respuestas y copiloto. Cada llamada queda en `LlmCallLogs` con tokens, caché y costo estimado, visibles en `/admin/ai`.

## Tests

```bash
dotnet test                                         # 146 tests: dominio, application, arquitectura e integración
dotnet test --filter "Category!=Integration"        # sin base de datos
dotnet run --project tests/SupportAI.PromptEvals -- --provider fake      # harness de evaluación (gratis)
dotnet run --project tests/SupportAI.PromptEvals -- --provider claude    # evaluación real (facturable)
```

- **Integración**: API completa con `WebApplicationFactory` contra SQL Server real, `Respawn`, `FakeTimeProvider` (SLA)
  y el proveedor Fake de IA (sin llamadas externas).
- **Arquitectura**: NetArchTest verifica las dependencias entre capas y convenciones (handlers sellados, entidades sin setters públicos).
- **Prompts**: los esquemas son estrictos (`additionalProperties: false`, todo `required`) y coinciden con los records de C#.

## Prompt engineering

- **Prompts versionados e inmutables** (`Ai/Prompts/{key}/v{n}/system.md, user.md, schema.json, meta.json`). Se publican
  en la BD al arrancar; un hash detecta si alguien edita una versión ya publicada. Activar o revertir una versión desde
  `/admin/prompts` no requiere redeploy.
- **Structured outputs**: `output_config.format` con JSON Schema. Para el triage, las categorías y equipos del tenant se
  inyectan como `enum`, así que el modelo no puede inventar valores.
- **Validación en capas**: `stop_reason` (refusal / max_tokens), JSON Schema local, reglas semánticas y un reintento de
  reparación con los errores como feedback. Si vuelve a fallar, el ticket queda para revisión humana.
- **Seguridad**: el contenido de clientes va dentro de etiquetas XML, escapado y declarado como no confiable. La PII
  (emails, teléfonos, tarjetas con Luhn, IBAN) se redacta antes de enviarse.
- **Medición**: tasa de aceptación por versión de prompt (correcciones de agentes) y evaluación offline con 30 casos dorados.
  Como referencia, la heurística del proveedor Fake obtiene ~73 % en categoría; el job `prompt-eval` exige 85 % con Claude.

## Limitaciones conocidas

- .NET 9 deja de tener soporte en noviembre de 2026. Migrar a .NET 10 (LTS) es cambiar `TargetFramework` en
  `Directory.Build.props` y las versiones 9.x de `Directory.Packages.props`.
- El prompt caching solo se activa si el prefijo cacheado supera el mínimo del modelo; los system prompts actuales son
  cortos, así que la tasa de caché será baja hasta que crezcan (más ejemplos few-shot).
- LocalDB no tiene Full-Text Search: la KB usa búsqueda léxica ranqueada en la app (sustituible por FTS o vectores sin
  cambiar la interfaz `IKnowledgeSearch`).
- Los jobs de despliegue son plantillas (`DEPLOY_WEBHOOK_URL`) para adaptar a la plataforma de destino.
