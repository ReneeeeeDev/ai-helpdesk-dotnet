namespace SupportAI.Application.Abstractions.Messaging;

/// <summary>Tipo "vacío" para comandos que no devuelven datos.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}

/// <summary>Solicitud que produce un <typeparamref name="TResponse"/>.</summary>
public interface IRequest<TResponse>;

/// <summary>Intención de cambiar el estado del sistema.</summary>
public interface ICommand<TResponse> : IRequest<TResponse>;

/// <summary>Comando sin valor de retorno.</summary>
public interface ICommand : ICommand<Unit>;

/// <summary>Lectura sin efectos secundarios.</summary>
public interface IQuery<TResponse> : IRequest<TResponse>;

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>;

public interface ICommandHandler<in TCommand> : ICommandHandler<TCommand, Unit>
    where TCommand : ICommand<Unit>;

public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>;

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>
/// Comportamiento transversal que envuelve a los handlers (logging, autorización, validación...).
/// Se ejecutan en el orden en que se registran.
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

/// <summary>Punto de entrada para ejecutar comandos y queries.</summary>
public interface ISender
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
