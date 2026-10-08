@AGENTS.md

# Notas específicas para Claude Code

- Antes de dar por terminada una tarea, ejecuta `dotnet build SupportAI.sln` y los tests afectados. Si fallan, dilo con la salida.
- Para cambios que tocan varias capas, sigue el orden Domain → Application → Infrastructure → Api → Web y compila entre pasos.
- Comandos personalizados en `.claude/commands/`:
  - `/add-prompt-version <key>`: crea una nueva versión de un prompt sin tocar la anterior.
  - `/new-migration <Nombre>`: agrega una migración de EF Core y revisa el SQL generado.
- No ejecutes `tests/SupportAI.PromptEvals` sin permiso explícito: hace llamadas reales y facturables a Claude.
- No hagas `git push` ni cambies la configuración del remoto salvo petición explícita.
