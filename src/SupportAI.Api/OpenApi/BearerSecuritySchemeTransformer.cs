using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace SupportAI.Api.OpenApi;

/// <summary>Declara el esquema Bearer (JWT) en el documento OpenAPI para que Scalar permita autenticarse.</summary>
internal sealed class BearerSecuritySchemeTransformer(IAuthenticationSchemeProvider schemeProvider) : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "SupportAI API",
            Version = context.DocumentName,
            Description = "Mesa de ayuda multi-tenant con triage, respuestas sugeridas y copiloto impulsados por Claude.",
        };

        var schemes = await schemeProvider.GetAllSchemesAsync();
        if (!schemes.Any(s => s.Name == JwtBearerDefaults.AuthenticationScheme))
        {
            return;
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Access token obtenido en POST /api/v1/auth/login",
        };

        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Id = "Bearer", Type = ReferenceType.SecurityScheme } }] = [],
        };

        foreach (var operation in document.Paths.Values.SelectMany(p => p.Operations.Values))
        {
            operation.Security.Add(requirement);
        }
    }
}
