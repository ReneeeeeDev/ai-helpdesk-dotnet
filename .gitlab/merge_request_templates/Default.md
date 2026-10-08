## Qué cambia y por qué

<!-- Contexto del problema y resumen de la solución. Enlaza el issue: Closes #123 -->

## Cómo probarlo

<!-- Pasos concretos para el revisor (endpoints, pantallas, datos de prueba). -->

## Checklist del autor

- [ ] La rama está rebasada sobre `main` (`git fetch && git rebase origin/main`) y la historia está limpia (Conventional Commits).
- [ ] Tests nuevos o actualizados que cubren el cambio (unitarios y, si toca BD o HTTP, de integración).
- [ ] `dotnet build` sin advertencias y `dotnet test` en verde en local.
- [ ] Si hay migración: revisé el SQL (`dotnet ef migrations script`) y no hay operaciones destructivas sin plan de datos.
- [ ] Errores nuevos mapeados a ProblemDetails con un `code` estable; sin PII en logs.
- [ ] Sin secretos en el código ni en la configuración versionada.
- [ ] Documentación actualizada (`AGENTS.md`, `docs/`, ADR si es una decisión de arquitectura).

## Uso de asistentes de IA

- [ ] Este MR incluye código generado o sugerido por IA (Claude Code / Copilot / Cursor).
- Partes asistidas y cómo las verifiqué: <!-- p.ej. "handlers de X generados con Claude Code; revisé la consulta SQL generada y agregué tests de integración" -->

## Para el revisor

<!-- Puntos donde quieres especial atención. El job manual `ai-review` puede dejar una revisión automática como apoyo. -->

/label ~"needs-review"
