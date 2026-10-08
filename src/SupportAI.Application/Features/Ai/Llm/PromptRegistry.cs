using Microsoft.EntityFrameworkCore;
using SupportAI.Application.Abstractions.Ai;
using SupportAI.Application.Abstractions.Data;
using SupportAI.Domain.Ai;

namespace SupportAI.Application.Features.Ai.Llm;

/// <summary>Lee la versión activa de cada prompt desde la BD (sincronizada desde los archivos embebidos al arrancar).</summary>
internal sealed class PromptRegistry(IApplicationDbContext db) : IPromptRegistry
{
    public async Task<PromptDefinition> GetActiveAsync(string key, CancellationToken ct)
    {
        var template = await db.PromptTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Key == key && p.IsActive, ct)
            ?? throw new InvalidOperationException($"No hay una versión activa del prompt '{key}'.");
        return ToDefinition(template);
    }

    public async Task<PromptDefinition> GetVersionAsync(string key, int version, CancellationToken ct)
    {
        var template = await db.PromptTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Key == key && p.Version == version, ct)
            ?? throw new InvalidOperationException($"No existe la versión {version} del prompt '{key}'.");
        return ToDefinition(template);
    }

    private static PromptDefinition ToDefinition(PromptTemplate t) =>
        new(t.Key, t.Version, t.SystemPrompt, t.UserTemplate, t.JsonSchema, t.Model, t.Effort, t.MaxTokens);
}
