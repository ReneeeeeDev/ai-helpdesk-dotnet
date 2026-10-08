namespace SupportAI.Domain.Common;

/// <summary>Violación de una regla de negocio. La API la traduce a 422 Unprocessable Entity.</summary>
public class DomainException : Exception
{
    public DomainException(string message, string code = "domain_rule_violated")
        : base(message)
    {
        Code = code;
    }

    public DomainException()
        : this("Se violó una regla de negocio.")
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "domain_rule_violated";
    }

    /// <summary>Código estable para clientes (se expone en ProblemDetails.extensions.code).</summary>
    public string Code { get; } = "domain_rule_violated";
}
