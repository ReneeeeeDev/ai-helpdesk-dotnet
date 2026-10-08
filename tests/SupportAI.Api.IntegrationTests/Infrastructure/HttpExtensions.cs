using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SupportAI.Application.Features.Tickets;
using SupportAI.Contracts.Common;
using SupportAI.Contracts.Tickets;

namespace SupportAI.Api.IntegrationTests.Infrastructure;

internal static class HttpExtensions
{
    private static JsonSerializerOptions Json => SupportAIApiFactory.Json;

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
        }

        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) => client.PostAsJsonAsync(url, body, Json);

    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string url, T body) => client.PutAsJsonAsync(url, body, Json);

    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task<TicketCreatedResult> CreateTicketAsync(this HttpClient client, string subject = "Ticket de prueba", string description = "Descripción de prueba")
    {
        var response = await client.PostJsonAsync("/api/v1/tickets", new CreateTicketRequest(subject, description));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.ReadAsync<TicketCreatedResult>();
    }

    public static async Task<TicketDetailDto> GetTicketAsync(this HttpClient client, Guid id) =>
        await (await client.GetAsync($"/api/v1/tickets/{id}")).ReadAsync<TicketDetailDto>();

    public static async Task<CursorPage<TicketSummaryDto>> ListTicketsAsync(this HttpClient client, string query = "") =>
        await (await client.GetAsync($"/api/v1/tickets{query}")).ReadAsync<CursorPage<TicketSummaryDto>>();
}
