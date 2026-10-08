# ADR 0002: Outbox transaccional para efectos secundarios (triage con LLM, notificaciones)

- **Estado:** Aceptado
- **Fecha:** 2026-10-07

## Contexto
Al crear un ticket hay que llamar a Claude para el triage y notificar a los agentes. Hacerlo dentro del request HTTP
acopla la latencia y la disponibilidad del LLM a la creación del ticket. Además, un fallo entre el `COMMIT` y la
llamada al LLM perdería el trabajo sin dejar rastro.

## Decisión
- Las entidades levantan eventos de dominio (`TicketCreated`, `TicketStatusChanged`, ...).
- `DomainEventsToOutboxInterceptor` serializa esos eventos en la tabla `OutboxMessages` **en la misma transacción**
  que el cambio de estado.
- `OutboxProcessor` (BackgroundService) los procesa por lotes con `UPDLOCK, READPAST`, lo que permite varias instancias
  sin doble procesamiento. Usa backoff exponencial y, tras N intentos, los mueve a *dead-letter*.
- Los handlers de eventos son idempotentes. El triage, por ejemplo, no se repite si ya existe un resultado para esa versión de prompt.

## Consecuencias
- (+) Crear un ticket nunca falla porque Claude esté caído; el triage se reintenta solo.
- (+) Garantía *at-least-once* y trazabilidad completa en la tabla outbox.
- (−) Consistencia eventual: el triage aparece segundos después (se avisa a la UI por SignalR).
