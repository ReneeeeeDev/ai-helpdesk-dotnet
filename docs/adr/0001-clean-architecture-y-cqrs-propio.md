# ADR 0001: Clean Architecture con un dispatcher CQRS propio

- **Estado:** Aceptado
- **Fecha:** 2026-10-07

## Contexto
El sistema combina reglas de negocio no triviales (máquina de estados de tickets, SLA, multi-tenancy) con integraciones
volátiles (LLM, SQL Server, Identity). Necesitamos poder cambiar de proveedor de LLM o de base de datos sin tocar el dominio,
y testear la lógica sin infraestructura.

MediatR, la opción habitual en .NET para CQRS, pasó a licencia comercial en 2025. AutoMapper también.

## Decisión
- Capas Domain → Application → Infrastructure → Api, con Contracts para DTOs compartidos con Blazor.
- Dispatcher CQRS propio (`ISender`, `ICommand<T>`, `IQuery<T>`) con *pipeline behaviors* (logging, validación,
  transacción). Son unas 150 líneas, sin dependencias externas y fáciles de depurar.
- Mapeo manual con métodos `ToDto()`.
- Las reglas de dependencia se verifican con NetArchTest en `tests/SupportAI.Architecture.Tests`.

## Consecuencias
- (+) Sin licencias comerciales; el pipeline es explícito y testeable.
- (+) El dominio no conoce EF Core ni ASP.NET.
- (−) Algo más de código repetitivo (handlers y mapeos manuales).
- (−) `IApplicationDbContext` expone `DbSet<T>` a Application. Es un acoplamiento pragmático a EF Core que aceptamos
  a cambio de consultas LINQ directas en los handlers.
