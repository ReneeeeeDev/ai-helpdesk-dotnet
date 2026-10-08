## Cambio de prompt

- **Prompt:** `ticket.triage` <!-- clave -->
- **Versión nueva:** v<!-- n+1 --> (la versión anterior NO se modificó)
- **Se activa al desplegar:** sí / no (`activateOnSeed` en `meta.json`)

### Motivación

<!-- ¿Qué fallaba? Enlaza ejemplos reales (anonimizados) o el reporte de precisión de /admin/ai (tasa de correcciones). -->

### Cambios

<!-- Diff conceptual: instrucciones, ejemplos few-shot, esquema JSON, effort, max tokens. -->

### Resultado de la evaluación (job `prompt-eval`)

| Métrica | Versión anterior | Versión nueva |
|---|---|---|
| Categoría | | |
| Prioridad exacta | | |
| Prioridad ±1 | | |
| Equipo | | |
| requires_human | | |
| JSON válido / reparaciones | | |
| Costo por caso | | |

<!-- Adjunta el reporte .md del artefacto "Prompt eval". -->

## Checklist

- [ ] Nueva carpeta `v{n+1}`; `v{n}` intacta (el hash detecta ediciones de versiones publicadas).
- [ ] `schema.json` mantiene `additionalProperties: false` y todas las propiedades en `required`.
- [ ] Records de C# y `FakeLlmClient` actualizados si cambió el esquema; `PromptCatalogTests` en verde.
- [ ] El contenido del usuario sigue dentro de etiquetas XML y declarado como no confiable.
- [ ] Ejecuté el job manual `prompt-eval` y la precisión no empeora.
- [ ] Plan de rollback: activar v{n} desde `/admin/prompts`.

/label ~"prompt-change" ~"needs-review"
/assign_reviewer @ai-team
