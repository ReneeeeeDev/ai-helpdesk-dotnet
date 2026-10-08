---
description: Crea una nueva versión de un prompt versionado sin modificar la anterior
argument-hint: <prompt-key, p.ej. ticket.triage>
---

Crea una nueva versión del prompt `$ARGUMENTS` en `src/SupportAI.Infrastructure/Ai/Prompts/$ARGUMENTS/`.

1. Localiza la versión más alta existente `v{n}` y copia su carpeta completa a `v{n+1}` (`system.md`, `user.md`, `schema.json`, `meta.json`).
2. En `v{n+1}/meta.json` incrementa `version` y deja `"activateOnSeed": false` salvo que el usuario pida activarla.
3. Aplica los cambios pedidos **solo** en `v{n+1}`. No toques `v{n}`.
4. Si cambia `schema.json`, actualiza el record C# correspondiente en `Application/Ai/Contracts` y sus fixtures de `FakeLlmClient`;
   comprueba que `schema.json` mantiene `additionalProperties: false` y que `required` lista todas las propiedades.
5. Ejecuta `dotnet test tests/SupportAI.Application.Tests --filter "FullyQualifiedName~Prompt"` y muestra el resultado.
6. Recuerda al usuario que el MR debe usar la plantilla `Prompt-Change` y correr el job manual `prompt-eval`.
