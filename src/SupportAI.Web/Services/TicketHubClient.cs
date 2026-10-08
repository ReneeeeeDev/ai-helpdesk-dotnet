using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using SupportAI.Contracts.Tickets;

namespace SupportAI.Web.Services;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public Uri BaseUrl { get; set; } = new("http://localhost:5180/");
}

/// <summary>
/// Conexión SignalR del circuito al hub de la API. Los componentes se suscriben a <see cref="TicketChanged"/>
/// y vuelven a pedir los datos (la API aplica la autorización en cada lectura).
/// </summary>
public sealed class TicketHubClient(SessionStore session, IOptions<ApiOptions> options, ILogger<TicketHubClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;

    public event Func<TicketChangedNotification, Task>? TicketChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task EnsureStartedAsync()
    {
        await session.EnsureLoadedAsync();
        if (session.AccessToken is null || _connection is not null)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                return;
            }

            _connection = new HubConnectionBuilder()
                .WithUrl(new Uri(options.Value.BaseUrl, "hubs/tickets"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(session.AccessToken))
                .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
                .WithAutomaticReconnect()
                .Build();

            _connection.On<TicketChangedNotification>("TicketChanged", async notification =>
            {
                if (TicketChanged is { } handlers)
                {
                    foreach (var handler in handlers.GetInvocationList().Cast<Func<TicketChangedNotification, Task>>())
                    {
                        await handler(notification);
                    }
                }
            });

            await _connection.StartAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // Sin tiempo real la app sigue funcionando (los datos se recargan al navegar).
            logger.LogWarning(ex, "No se pudo conectar al hub de tickets");
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
