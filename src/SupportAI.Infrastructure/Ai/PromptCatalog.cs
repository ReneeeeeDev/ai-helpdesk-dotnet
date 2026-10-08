using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SupportAI.Domain.Ai;
using SupportAI.Infrastructure.Persistence;

namespace SupportAI.Infrastructure.Ai;

/// <summary>Prompt versionado tal como está en el repositorio (Ai/Prompts/{key}/v{n}/).</summary>
public sealed record PromptFile(
    string Key,
    int Version,
    string Description,
    string SystemPrompt,
    string UserTemplate,
    string? JsonSchema,
    string Model,
    string Effort,
    int MaxTokens,
    bool ActivateOnSeed);

/// <summary>Lee los prompts embebidos en el ensamblado (fuente de verdad versionada en Git).</summary>
public static class PromptCatalog
{
    private const string Prefix = "prompts/";
    private static readonly JsonSerializerOptions MetaJson = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<PromptFile> LoadAll()
    {
        var assembly = typeof(PromptCatalog).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal);

        var prompts = new List<PromptFile>();
        foreach (var metaPath in resources.Keys.Where(k => k.EndsWith("/meta.json", StringComparison.Ordinal)))
        {
            var folder = metaPath[..^"meta.json".Length];
            var meta = JsonSerializer.Deserialize<PromptMeta>(Read(assembly, resources[metaPath]), MetaJson)
                ?? throw new InvalidOperationException($"meta.json vacío en {folder}");

            var expectedFolder = $"{Prefix}{meta.Key}/v{meta.Version}/";
            if (!string.Equals(folder, expectedFolder, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"La carpeta {folder} no coincide con meta.json ({expectedFolder}).");
            }

            string? Optional(string file) => resources.TryGetValue(folder + file, out var name) ? Read(assembly, name) : null;
            string Required(string file) => Optional(file) ?? throw new InvalidOperationException($"Falta {folder}{file}");

            prompts.Add(new PromptFile(meta.Key, meta.Version, meta.Description, Required("system.md").Trim(), Required("user.md").Trim(),
                Optional("schema.json"), meta.Model, meta.Effort, meta.MaxTokens, meta.ActivateOnSeed));
        }

        return prompts.OrderBy(p => p.Key, StringComparer.Ordinal).ThenBy(p => p.Version).ToList();
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private sealed record PromptMeta(string Key, int Version, string Description, string Model, string Effort, int MaxTokens, bool ActivateOnSeed);
}

/// <summary>
/// Sincroniza el catálogo embebido con la tabla PromptTemplates al arrancar:
/// inserta versiones nuevas, activa la inicial de cada clave y detecta versiones publicadas que alguien modificó
/// (prohibido: hay que crear v{n+1}).
/// </summary>
public sealed partial class PromptCatalogSynchronizer(SupportAIDbContext db, TimeProvider clock, ILogger<PromptCatalogSynchronizer> logger)
{
    public async Task SyncAsync(CancellationToken ct)
    {
        var existing = await db.PromptTemplates.ToListAsync(ct);
        var now = clock.GetUtcNow();
        var added = 0;

        foreach (var file in PromptCatalog.LoadAll())
        {
            var hash = PromptTemplate.ComputeHash(file.SystemPrompt, file.UserTemplate, file.JsonSchema, file.Model, file.Effort, file.MaxTokens);
            var current = existing.FirstOrDefault(p => p.Key == file.Key && p.Version == file.Version);
            if (current is not null)
            {
                if (current.ContentHash != hash)
                {
                    LogImmutableViolation(logger, file.Key, file.Version);
                }

                continue;
            }

            var template = PromptTemplate.Create(file.Key, file.Version, file.Description, file.SystemPrompt, file.UserTemplate,
                file.JsonSchema, file.Model, file.Effort, file.MaxTokens, now);
            if (file.ActivateOnSeed && !existing.Any(p => p.Key == file.Key && p.IsActive))
            {
                template.Activate(now);
            }

            db.PromptTemplates.Add(template);
            existing.Add(template);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            LogSynced(logger, added);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "El prompt {Key} v{Version} cambió después de publicarse. Las versiones son inmutables: crea una versión nueva.")]
    private static partial void LogImmutableViolation(ILogger logger, string key, int version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Catálogo de prompts sincronizado: {Count} versiones nuevas")]
    private static partial void LogSynced(ILogger logger, int count);
}
