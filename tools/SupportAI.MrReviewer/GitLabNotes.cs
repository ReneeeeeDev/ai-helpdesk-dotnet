using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SupportAI.MrReviewer;

/// <summary>
/// Publica la revisión como UNA nota en el MR (la actualiza si ya existe) usando la API REST de GitLab.
/// Requiere un project access token con scope "api" en GITLAB_REVIEW_TOKEN (CI_JOB_TOKEN no puede escribir notas).
/// </summary>
public sealed class GitLabNotes(HttpClient http, string apiUrl, string projectId, string mergeRequestIid)
{
    public const string Marker = "<!-- supportai-ai-review -->";

    public static GitLabNotes? FromEnvironment()
    {
        var api = Environment.GetEnvironmentVariable("CI_API_V4_URL");
        var project = Environment.GetEnvironmentVariable("CI_PROJECT_ID");
        var iid = Environment.GetEnvironmentVariable("CI_MERGE_REQUEST_IID");
        var token = Environment.GetEnvironmentVariable("GITLAB_REVIEW_TOKEN");
        if (string.IsNullOrEmpty(api) || string.IsNullOrEmpty(project) || string.IsNullOrEmpty(iid) || string.IsNullOrEmpty(token))
        {
            return null;
        }

        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
        return new GitLabNotes(client, api.TrimEnd('/'), Uri.EscapeDataString(project), iid);
    }

    public async Task UpsertAsync(string markdown, CancellationToken ct)
    {
        var body = $"{Marker}\n{markdown}";
        var notesUrl = $"{apiUrl}/projects/{projectId}/merge_requests/{mergeRequestIid}/notes";

        var existing = (await http.GetFromJsonAsync<List<Note>>($"{notesUrl}?per_page=100&sort=desc", ct) ?? [])
            .FirstOrDefault(n => n.Body?.Contains(Marker, StringComparison.Ordinal) == true);

        using var response = existing is null
            ? await http.PostAsJsonAsync(notesUrl, new { body }, ct)
            : await http.PutAsJsonAsync($"{notesUrl}/{existing.Id}", new { body }, ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed record Note([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("body")] string? Body);
}
