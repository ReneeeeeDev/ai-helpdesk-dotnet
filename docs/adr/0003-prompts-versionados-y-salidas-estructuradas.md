# ADR 0003: Prompts versionados y salidas estructuradas validadas

- **Estado:** Aceptado
- **Fecha:** 2026-10-07

## Contexto
La calidad del triage depende del prompt. Un cambio de prompt sin control puede degradar silenciosamente la
clasificación de miles de tickets. Además, el código consume la respuesta del LLM como datos (categoría, prioridad, etc.),
así que una respuesta mal formada no puede romper el flujo.

## Decisión
1. **Prompts como artefactos versionados e inmutables** en `Infrastructure/Ai/Prompts/{key}/v{n}/`
   (`system.md`, `user.md`, `schema.json`, `meta.json`). Al arrancar se sincronizan a la tabla `PromptTemplates`.
   Activar o revertir una versión se hace desde admin, sin redeploy.
2. **Structured outputs** (`output_config.format` con JSON Schema). El modelo genera JSON que cumple el esquema por
   construcción (*constrained decoding*).
3. **Validación en capas** en `StructuredCompletionService`:
   `stop_reason` → JSON Schema (JsonSchema.Net) → reglas semánticas (FluentValidation: la categoría existe en el tenant,
   etc.). Si falla, se hace un **reintento de reparación** con los errores como feedback y, si vuelve a fallar, el ticket
   se marca para revisión humana.
4. **Trazabilidad**: cada llamada se guarda en `LlmCallLog` con la versión de prompt, tokens (incluida la caché), latencia
   y costo. Las correcciones de los agentes (`Overridden`) miden la precisión real por versión.
5. **Evaluación offline** (`tests/SupportAI.PromptEvals`) con un dataset dorado. Es obligatoria en los MR que cambian prompts.

## Consecuencias
- (+) Se sabe qué prompt produjo cada decisión, y revertir es un clic.
- (+) Los cambios de prompt pasan por code review igual que el código (CODEOWNERS → `@ai-team`).
- (−) Hay que mantener sincronizados `schema.json` y los records de C#. Un test unitario lo verifica.
