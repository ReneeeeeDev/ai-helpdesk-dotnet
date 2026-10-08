using System.Net;
using System.Net.Http.Headers;
using SupportAI.Api.IntegrationTests.Infrastructure;
using SupportAI.Contracts.Auth;
using SupportAI.Contracts.Common;

namespace SupportAI.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthTests(SupportAIApiFactory factory)
{
    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokensAndProfile()
    {
        var client = factory.CreateClient();

        var response = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(DemoUsers.Agent, SupportAIApiFactory.DemoPassword));
        var tokens = await response.ReadAsync<TokenResponse>();

        tokens.AccessToken.ShouldNotBeNullOrWhiteSpace();
        tokens.RefreshToken.ShouldNotBeNullOrWhiteSpace();
        tokens.User.Role.ShouldBe(UserRole.Agent);
        tokens.User.TenantName.ShouldBe("Acme Cloud");
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401Problem()
    {
        var response = await factory.CreateClient().PostJsonAsync("/api/v1/auth/login", new LoginRequest(DemoUsers.Admin, "incorrecta"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ProblemCodeAsync()).ShouldBe("unauthorized");
    }

    [Fact]
    public async Task Login_FiveFailures_LocksAccount()
    {
        var client = factory.CreateClient();
        var email = $"lock-{Guid.NewGuid():N}@acme.test";
        (await client.PostJsonAsync("/api/v1/auth/register", new RegisterCustomerRequest("acme", email, "Bloqueo", "Passw0rd!"))).EnsureSuccessStatusCode();

        for (var i = 0; i < 5; i++)
        {
            await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, "mala"));
        }

        var response = await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Passw0rd!"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).ShouldContain("bloqueada");
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndReuseRevokesWholeSession()
    {
        var client = factory.CreateClient();
        var login = await (await client.PostJsonAsync("/api/v1/auth/login", new LoginRequest(DemoUsers.BillingAgent, SupportAIApiFactory.DemoPassword))).ReadAsync<TokenResponse>();

        var rotated = await (await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequest(login.RefreshToken))).ReadAsync<TokenResponse>();
        rotated.RefreshToken.ShouldNotBe(login.RefreshToken);

        // Reutilizar el token viejo = posible robo → 401 y se revoca también el token nuevo.
        (await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequest(login.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequest(rotated.RefreshToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_NewCustomer_CanCallMe()
    {
        var client = factory.CreateClient();
        var email = $"nuevo-{Guid.NewGuid():N}@acme.test";

        var response = await client.PostJsonAsync("/api/v1/auth/register", new RegisterCustomerRequest("acme", email, "Nuevo Cliente", "Passw0rd!"));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var tokens = await response.ReadAsync<TokenResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await (await client.GetAsync("/api/v1/auth/me")).ReadAsync<UserInfoDto>();
        me.Email.ShouldBe(email);
        me.Role.ShouldBe(UserRole.Customer);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var response = await factory.CreateClient().PostJsonAsync("/api/v1/auth/register", new RegisterCustomerRequest("acme", DemoUsers.Customer, "Dup", "Passw0rd!"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ProblemCodeAsync()).ShouldBe("email_taken");
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        (await factory.CreateClient().GetAsync("/api/v1/tickets")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
