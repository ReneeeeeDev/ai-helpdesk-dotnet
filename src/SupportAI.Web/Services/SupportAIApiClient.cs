using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupportAI.Contracts.Ai;
using SupportAI.Contracts.Auth;
using SupportAI.Contracts.Common;
using SupportAI.Contracts.Knowledge;
using SupportAI.Contracts.Organization;
using SupportAI.Contracts.Tickets;

namespace SupportAI.Web.Services;

/// <summary>Error de la API con su ProblemDetails (código estable para decidir qué mostrar).</summary>
public sealed class ApiException(HttpStatusCode status, ApiProblem? problem)
    : Exception(problem?.Detail ?? problem?.Title ?? $"Error HTTP {(int)status}")
{
    public HttpStatusCode Status { get; } = status;

    public ApiProblem? Problem { get; } = problem;

    public string? Code => Problem?.Code;
}

/// <summary>Tickets creados: id + número correlativo.</summary>
public sealed record TicketCreated(Guid Id, long Number);

/// <summary>
/// Cliente tipado de la API (scoped por circuito). Adjunta el JWT de la sesión, y ante un 401 intenta una vez
/// renovar con el refresh token; si falla, cierra la sesión.
/// </summary>
public sealed class SupportAIApiClient(IHttpClientFactory factory, SessionStore session)
{
    public const string HttpClientName = "supportai-api";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // ───── Auth ─────
    public async Task<TokenResponse> LoginAsync(string email, string password)
    {
        var tokens = await SendAsync<TokenResponse>(HttpMethod.Post, "api/v1/auth/login", new LoginRequest(email, password), authenticated: false);
        await session.SetAsync(tokens);
        return tokens;
    }

    public async Task LogoutAsync()
    {
        if (session.Session?.RefreshToken is { } refresh)
        {
            try
            {
                await SendAsync<object>(HttpMethod.Post, "api/v1/auth/logout", new RefreshTokenRequest(refresh), authenticated: false);
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                // El logout local procede aunque la API no responda.
            }
        }

        await session.ClearAsync();
    }

    // ───── Tickets ─────
    public Task<CursorPage<TicketSummaryDto>> GetTicketsAsync(TicketListQuery query) => SendAsync<CursorPage<TicketSummaryDto>>(HttpMethod.Get, "api/v1/tickets" + ToQueryString(query));

    public Task<TicketDetailDto> GetTicketAsync(Guid id) => SendAsync<TicketDetailDto>(HttpMethod.Get, $"api/v1/tickets/{id}");

    public Task<TicketCreated> CreateTicketAsync(CreateTicketRequest request, string idempotencyKey) =>
        SendAsync<TicketCreated>(HttpMethod.Post, "api/v1/tickets", request, headers: new() { ["Idempotency-Key"] = idempotencyKey });

    public Task<TicketMessageDto> AddMessageAsync(Guid id, AddMessageRequest request) => SendAsync<TicketMessageDto>(HttpMethod.Post, $"api/v1/tickets/{id}/messages", request);

    public Task ChangeStatusAsync(Guid id, TicketStatus status, string rowVersion) =>
        SendAsync<object>(HttpMethod.Put, $"api/v1/tickets/{id}/status", new ChangeStatusRequest(status), headers: IfMatch(rowVersion));

    public Task ChangePriorityAsync(Guid id, TicketPriority priority, string rowVersion) =>
        SendAsync<object>(HttpMethod.Put, $"api/v1/tickets/{id}/priority", new ChangePriorityRequest(priority), headers: IfMatch(rowVersion));

    public Task AssignAsync(Guid id, Guid? assigneeId, Guid? teamId, string rowVersion) =>
        SendAsync<object>(HttpMethod.Put, $"api/v1/tickets/{id}/assignment", new AssignTicketRequest(assigneeId, teamId), headers: IfMatch(rowVersion));

    // ───── IA ─────
    public Task<TriageResultDto> RetriageAsync(Guid id) => SendAsync<TriageResultDto>(HttpMethod.Post, $"api/v1/tickets/{id}/ai/triage");

    public Task<TriageResultDto> AcceptTriageAsync(Guid id, Guid resultId) => SendAsync<TriageResultDto>(HttpMethod.Post, $"api/v1/tickets/{id}/ai/triage/{resultId}/accept");

    public Task<TriageResultDto> OverrideTriageAsync(Guid id, Guid resultId, OverrideTriageRequest request) =>
        SendAsync<TriageResultDto>(HttpMethod.Post, $"api/v1/tickets/{id}/ai/triage/{resultId}/override", request);

    public Task<SuggestedReplyDto> SuggestReplyAsync(Guid id) => SendAsync<SuggestedReplyDto>(HttpMethod.Post, $"api/v1/tickets/{id}/ai/reply-suggestions");

    public Task ResolveSuggestedReplyAsync(Guid id, Guid replyId, bool used) =>
        SendAsync<object>(HttpMethod.Post, $"api/v1/tickets/{id}/ai/reply-suggestions/{replyId}/{(used ? "used" : "discard")}");

    public IAsyncEnumerable<SseEvent> StreamSummaryAsync(Guid id, CancellationToken ct) =>
        StreamAsync(HttpMethod.Get, $"api/v1/tickets/{id}/ai/summary/stream", body: null, ct);

    public IAsyncEnumerable<SseEvent> AskCopilotAsync(CopilotRequest request, CancellationToken ct) =>
        StreamAsync(HttpMethod.Post, "api/v1/ai/copilot", request, ct);

    public Task<AiUsageReportDto> GetAiUsageAsync(DateOnly from, DateOnly to) =>
        SendAsync<AiUsageReportDto>(HttpMethod.Get, $"api/v1/ai/usage?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<PromptTemplateDto>> GetPromptsAsync() => SendAsync<List<PromptTemplateDto>>(HttpMethod.Get, "api/v1/ai/prompts");

    public Task<PromptTemplateDetailDto> GetPromptAsync(Guid id) => SendAsync<PromptTemplateDetailDto>(HttpMethod.Get, $"api/v1/ai/prompts/{id}");

    public Task<PromptTemplateDto> ActivatePromptAsync(string key, int version) =>
        SendAsync<PromptTemplateDto>(HttpMethod.Post, $"api/v1/ai/prompts/{Uri.EscapeDataString(key)}/versions/{version}/activate");

    // ───── Organización y KB ─────
    public Task<List<TeamDto>> GetTeamsAsync() => SendAsync<List<TeamDto>>(HttpMethod.Get, "api/v1/teams");

    public Task<List<MemberDto>> GetAgentsAsync() => SendAsync<List<MemberDto>>(HttpMethod.Get, "api/v1/members");

    public Task<List<string>> GetCategoriesAsync() => SendAsync<List<string>>(HttpMethod.Get, "api/v1/categories");

    public Task<TenantSettingsDto> GetSettingsAsync() => SendAsync<TenantSettingsDto>(HttpMethod.Get, "api/v1/settings");

    public Task<TenantSettingsDto> UpdateSettingsAsync(UpdateTenantSettingsRequest request) => SendAsync<TenantSettingsDto>(HttpMethod.Put, "api/v1/settings", request);

    public Task<SlaPolicyDto> GetSlaPolicyAsync() => SendAsync<SlaPolicyDto>(HttpMethod.Get, "api/v1/sla-policy");

    public Task<SlaPolicyDto> UpdateSlaPolicyAsync(IReadOnlyList<SlaTargetDto> targets) => SendAsync<SlaPolicyDto>(HttpMethod.Put, "api/v1/sla-policy", targets);

    public Task<List<KnowledgeArticleSummaryDto>> GetArticlesAsync(string? search, bool includeUnpublished) =>
        SendAsync<List<KnowledgeArticleSummaryDto>>(HttpMethod.Get,
            $"api/v1/knowledge-articles?includeUnpublished={includeUnpublished.ToString().ToLowerInvariant()}{(string.IsNullOrWhiteSpace(search) ? "" : "&search=" + Uri.EscapeDataString(search))}");

    public Task<KnowledgeArticleDto> GetArticleAsync(Guid id) => SendAsync<KnowledgeArticleDto>(HttpMethod.Get, $"api/v1/knowledge-articles/{id}");

    public Task<KnowledgeArticleDto> SaveArticleAsync(Guid? id, UpsertArticleRequest request) => id is { } existing
        ? SendAsync<KnowledgeArticleDto>(HttpMethod.Put, $"api/v1/knowledge-articles/{existing}", request)
        : SendAsync<KnowledgeArticleDto>(HttpMethod.Post, "api/v1/knowledge-articles", request);

    // ───── Infraestructura HTTP ─────
    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body = null, bool authenticated = true, Dictionary<string, string>? headers = null)
    {
        using var response = await SendRawAsync(method, url, body, authenticated, headers, HttpCompletionOption.ResponseContentRead, CancellationToken.None);
        if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
        {
            return default!;
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async IAsyncEnumerable<SseEvent> StreamAsync(HttpMethod method, string url, object? body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendRawAsync(method, url, body, authenticated: true, headers: null, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var sse in SseReader.ReadAsync(stream, ct))
        {
            yield return sse;
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method, string url, object? body, bool authenticated, Dictionary<string, string>? headers, HttpCompletionOption completion, CancellationToken ct)
    {
        var client = factory.CreateClient(HttpClientName);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            }

            foreach (var (name, value) in headers ?? [])
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            if (authenticated)
            {
                await session.EnsureLoadedAsync();
                if (session.AccessToken is { } token)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }

            var response = await client.SendAsync(request, completion, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated && attempt == 0 && await TryRefreshAsync())
            {
                response.Dispose();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                ApiProblem? problem = null;
                try
                {
                    problem = await response.Content.ReadFromJsonAsync<ApiProblem>(Json, ct);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    // Respuesta sin cuerpo ProblemDetails.
                }

                var status = response.StatusCode;
                response.Dispose();
                if (status == HttpStatusCode.Unauthorized && authenticated)
                {
                    await session.ClearAsync();
                }

                throw new ApiException(status, problem);
            }

            return response;
        }
    }

    private async Task<bool> TryRefreshAsync()
    {
        var refreshToken = session.Session?.RefreshToken;
        if (refreshToken is null)
        {
            return false;
        }

        await _refreshLock.WaitAsync();
        try
        {
            // Otro request concurrente pudo haber renovado ya la sesión.
            if (session.Session?.RefreshToken != refreshToken)
            {
                return session.Session is not null;
            }

            var client = factory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync("api/v1/auth/refresh", new RefreshTokenRequest(refreshToken), Json);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await session.SetAsync((await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!);
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static Dictionary<string, string> IfMatch(string rowVersion) => new() { ["If-Match"] = $"\"{rowVersion}\"" };

    private static string ToQueryString(TicketListQuery q)
    {
        var parts = new List<string> { $"pageSize={q.PageSize}", $"sort={q.Sort}" };
        void Add(string name, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } text)
            {
                parts.Add($"{name}={Uri.EscapeDataString(text)}");
            }
        }

        Add("status", q.Status);
        Add("priority", q.Priority);
        Add("assigneeId", q.AssigneeId);
        Add("teamId", q.TeamId);
        Add("category", q.Category);
        Add("search", q.Search);
        Add("needsReview", q.NeedsReview?.ToString().ToLowerInvariant());
        Add("slaBreached", q.SlaBreached?.ToString().ToLowerInvariant());
        Add("cursor", q.Cursor);
        if (q.OnlyMine)
        {
            parts.Add("onlyMine=true");
        }

        if (q.IncludeClosed)
        {
            parts.Add("includeClosed=true");
        }

        return "?" + string.Join('&', parts);
    }
}
