# Instrucciones para GitHub Copilot

Este repositorio se aloja en GitLab, pero Copilot (VS Code / Visual Studio / JetBrains) lee este archivo igualmente.
Las convenciones completas están en [`AGENTS.md`](../AGENTS.md). Lo esencial:

- .NET 9, C# `latest`, nullable habilitado, warnings como errores, namespaces file-scoped.
- Clean Architecture: `Domain` no referencia nada; `Application` no referencia `Infrastructure` ni ASP.NET Core;
  los controllers no contienen lógica de negocio (solo despachan comandos/queries).
- CQRS con dispatcher propio (`ICommand<T>`, `IQuery<T>`, `ICommandHandler<,>`, `IQueryHandler<,>`). No sugieras MediatR ni AutoMapper.
- Usa `TimeProvider` en lugar de `DateTime.UtcNow`. Async con `CancellationToken` en todo.
- EF Core: configuraciones `IEntityTypeConfiguration<T>`, lecturas `AsNoTracking()` con proyección, enums como string.
- LLM: SDK oficial `Anthropic`, modelo `claude-opus-5-5`, salidas JSON con `OutputConfig.Format` (structured outputs),
  sin prefill ni `tool_choice` forzado. Prompts versionados en `Infrastructure/Ai/Prompts/{key}/v{n}/`, nunca editar una versión existente.
- Tests: xUnit + Shouldly + NSubstitute; nada de llamadas reales a Claude en tests (usar `FakeLlmClient`).
- Commits: Conventional Commits.
