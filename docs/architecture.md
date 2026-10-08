# Arquitectura de SupportAI

## Capas y dependencias

| Proyecto | Responsabilidad | Depende de |
|---|---|---|
| `SupportAI.Domain` | Agregados (`Ticket`, `Tenant`, `Team`, `User`, `SlaPolicy`, `KnowledgeArticle`), entidades de IA, eventos de dominio, invariantes | — |
| `SupportAI.Contracts` | DTOs y enums públicos (compartidos por API y Web) | — |
| `SupportAI.Application` | Casos de uso CQRS, validadores, puertos (`ILlmClient`, `IPromptRegistry`, `IAuthService`...), servicios de IA | Domain, Contracts |
| `SupportAI.Infrastructure` | EF Core, Identity/JWT, outbox, workers, cliente Claude, prompts embebidos | Application |
| `SupportAI.Api` | HTTP: controllers, autenticación, errores, idempotencia, rate limiting, SSE, SignalR | Infrastructure |
| `SupportAI.Web` | Blazor Server: UI por rol, cliente tipado de la API, SignalR | Contracts |

`tests/SupportAI.Architecture.Tests` hace fallar el build si alguien rompe estas reglas.

## Pipeline de un request

```
HTTP → CorrelationId → Serilog → ExceptionHandler → Auth (JWT) → TenantResolution → RateLimiter → Authorization
     → Controller → ISender
        → LoggingBehavior → AuthorizationBehavior ([AuthorizeRoles]) → ValidationBehavior (FluentValidation)
        → Handler → IApplicationDbContext (filtrado por tenant) → SaveChanges
             └─ interceptores: auditoría (TimeProvider) + eventos de dominio → OutboxMessages (misma transacción)
```

Los errores se traducen a ProblemDetails con un `code` estable: 400 validación, 401, 403, 404, 409 (concurrencia,
idempotencia, conflictos), 422 (reglas de dominio), 429 (rate limit o presupuesto de IA), 502/503 (proveedor de IA).

## Multi-tenancy

- El `tenant_id` viaja en el JWT. `TenantResolutionMiddleware` lo fija en un `TenantContext` scoped.
- Toda entidad `ITenantOwned` recibe un filtro global `TenantId == CurrentTenantId` (combinado con soft-delete).
  Sin tenant, el filtro no devuelve filas (*fail closed*).
- Los procesos en segundo plano abren un scope por mensaje y fijan el tenant del evento antes de tocar datos.
- Las herramientas del copiloto consultan con el mismo DbContext filtrado: el modelo no puede ver otro tenant.

## Mensajería: outbox transaccional

1. Las entidades levantan eventos (`TicketCreated`, `TicketStatusChanged`, `TicketSlaBreached`...).
2. `DomainEventsToOutboxInterceptor` los guarda en `OutboxMessages` dentro del mismo `SaveChanges`.
3. `OutboxProcessor` reserva lotes con un único `UPDATE ... OUTPUT` usando `UPDLOCK, READPAST` y un *lease* de 5 minutos,
   lo que es seguro con varias instancias.
4. Despacha a los `IDomainEventHandler<T>` (triage, notificaciones SignalR). Si falla, aplica backoff exponencial y,
   tras 8 intentos, mueve el mensaje a dead-letter.

## Capa de IA

```
TicketTriageService ─► PromptRegistry (versión activa en BD)
        │            ─► PiiRedactor + PromptRenderer (XML + escape de contenido no confiable)
        ▼
StructuredCompletionService ─► ILlmClient ─► ResilientLlmClient (circuit breaker) ─► ClaudeLlmClient ─► Claude
        │                                                                    (o FakeLlmClient en dev/tests)
        ├─ stop_reason: refusal → error tipado · max_tokens → reintento ×2
        ├─ JSON Schema (JsonSchema.Net) → record C# → reglas semánticas → reparación (1 vez)
        └─ LlmCallRecorder → LlmCallLogs (tokens, caché, latencia, costo, versión de prompt)
```

| Operación | Prompt | Salida | Effort | Entrega |
|---|---|---|---|---|
| Triage | `ticket.triage` | JSON (enums dinámicos por tenant) | low | Outbox (asíncrono) o manual |
| Respuesta sugerida | `ticket.reply` | JSON con citas a la KB | medium | Request/response |
| Resumen | `ticket.summary` | Texto | low | SSE (streaming) |
| Copiloto | `copilot.agent` | Texto + tool use (4 herramientas `strict`) | medium | SSE |
| Revisión de MR | `mr.review` (en la herramienta) | JSON con hallazgos | high | GitLab CI |

Controles de costo y abuso: presupuesto mensual de tokens por tenant (429), rate limit por tenant en endpoints de IA,
circuit breaker ante fallos repetidos, `max_tokens` por prompt y fallback del lado del servidor ante rechazos.

## Concurrencia e idempotencia

- `Ticket.RowVersion` (rowversion): la UI envía `If-Match` con el ETag. Si cambió, la API responde 409 y la UI recarga.
- `Idempotency-Key` en la creación de tickets: la reserva atómica (PK `UserId+Key`) impide ejecuciones duplicadas;
  un reintento recibe la respuesta original y una clave reutilizada con otro cuerpo devuelve 422.
- Refresh tokens rotativos: reutilizar uno ya rotado revoca toda la sesión del usuario.

## Observabilidad

- Serilog: consola legible y archivo JSON compacto, con `CorrelationId` en cada log y en la cabecera de respuesta.
- `/health/live` y `/health/ready` (comprobación de la BD) para orquestadores.
- Requests lentos (más de 500 ms) se registran como advertencia desde el pipeline CQRS.
- `/admin/ai`: tokens, costo, tasa de caché, latencia y fallos por día y operación, más la precisión por versión de prompt.
