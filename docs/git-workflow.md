# Flujo de trabajo con Git y GitLab

## Modelo de ramas: trunk-based con ramas cortas
- `main` siempre desplegable y **protegida** en GitLab (sin push directo; solo merge vía MR con pipeline verde y 1 aprobación).
- Ramas de trabajo desde `main`, de vida corta (idealmente < 2 días):
  `feat/<tema>`, `fix/<tema>`, `chore/<tema>`, `docs/<tema>`, `refactor/<tema>`.
- Releases mediante tags SemVer (`v1.4.0`) sobre `main`. El pipeline de tags despliega a producción con aprobación manual.

## Configuración local recomendada
```bash
git config --global pull.rebase true        # git pull = fetch + rebase (sin merges "Merge branch main")
git config --global rebase.autoStash true   # guarda cambios locales antes del rebase
git config --global rerere.enabled true     # recuerda resoluciones de conflictos repetidos
git config --global fetch.prune true
dotnet tool restore && dotnet husky install # hooks: formato (pre-commit) y Conventional Commits (commit-msg)
```

## Ciclo de una tarea
```bash
git switch main && git pull
git switch -c feat/sla-business-hours
# ... commits pequeños: feat(sla): ..., test(sla): ...
git fetch origin
git rebase origin/main                    # mantén la rama al día (historia lineal)
git rebase -i origin/main                 # limpia la historia antes del MR: squash/fixup/reword
git push --force-with-lease -u origin feat/sla-business-hours
```
- Usa `git commit --fixup <sha>` durante la revisión y `git rebase -i --autosquash origin/main` al final.
- **Nunca** `--force` a secas; `--force-with-lease` evita pisar trabajo ajeno.
- El merge method del proyecto en GitLab es **Fast-forward merge** con la opción
  *"Squash commits when merging: Encourage"*. Si `main` avanzó, GitLab muestra "Rebase" en el MR o haces el rebase local.

## Resolución de conflictos
1. `git rebase origin/main` se detiene en el commit conflictivo.
2. `git status` lista los archivos; edita los marcadores `<<<<<<<`, `=======`, `>>>>>>>`.
3. `dotnet build && dotnet test --filter "Category!=Integration"` **antes** de continuar.
4. `git add <archivos>` y `git rebase --continue` (o `git rebase --abort` para volver al estado previo).
5. Con `rerere` activo, la misma resolución se reutiliza si el conflicto reaparece.

### Caso especial: migraciones de EF Core
Dos ramas que agregan migraciones siempre chocan en `SupportAIDbContextModelSnapshot.cs`. **No se resuelve a mano.**
```bash
git rebase origin/main
# conflicto en Persistence/Migrations/SupportAIDbContextModelSnapshot.cs
# Durante un rebase "--ours" = main (la base) y "--theirs" = tu commit. Queremos el snapshot de main:
git checkout --ours -- src/SupportAI.Infrastructure/Persistence/Migrations/SupportAIDbContextModelSnapshot.cs
git rm src/SupportAI.Infrastructure/Persistence/Migrations/*_MiMigracion*.cs
git rebase --continue
dotnet ef migrations add MiMigracion -p src/SupportAI.Infrastructure -s src/SupportAI.Api -o Persistence/Migrations
git add -A && git commit -m "feat(db): regenerar migración MiMigracion sobre main"
```
> Durante un **rebase**, `--ours` es la rama sobre la que reaplicas (`main`) y `--theirs` es tu commit. En un **merge** es al revés.

### Caso especial: prompts
Los prompts son inmutables por versión (`v1`, `v2`...). Si dos ramas crean `v3` del mismo prompt, la segunda en
integrarse renombra la suya a `v4`. Nunca se fusionan dos versiones distintas en una sola carpeta.

## Merge Requests y code review
- Plantillas en `.gitlab/merge_request_templates/` (`Default`, `Prompt-Change`).
- `CODEOWNERS` exige aprobación del equipo dueño: prompts → `@ai-team`, migraciones → `@db-owners`.
- Checklist del revisor: tests que cubren el cambio, migraciones revisadas (`dotnet ef migrations script`),
  sin secretos, errores mapeados a ProblemDetails, logs sin PII y, si cambió un prompt, resultado de `prompt-eval`.
- Comentarios con *suggestions* de GitLab para cambios pequeños; resolver todos los hilos antes de mergear.
- El job opcional `ai-review` publica una revisión automática hecha por Claude como comentario del MR. Es un
  **apoyo** al revisor humano, no lo reemplaza.

## Uso de asistentes de IA (Claude Code, Copilot, Cursor)
- Todas las herramientas leen las mismas convenciones de `AGENTS.md`.
- El código generado por IA pasa por el mismo pipeline, la misma revisión y los mismos tests que el resto.
- Declara en el MR si el cambio fue asistido por IA y qué partes revisaste manualmente.
- Nunca pegues secretos, datos de clientes ni tickets reales en un asistente externo.
