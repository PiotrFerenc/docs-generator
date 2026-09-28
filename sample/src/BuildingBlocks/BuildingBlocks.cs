using FluentResults;
using FluentValidation;
using MediatR;

namespace BuildingBlocks;

public interface IDomainEvent : INotification
{
}

public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

// Domain events are published via IPublisher by DispatchDomainEventsInterceptor
// after SaveChanges commits the transaction (not shown in this sample).

public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : ResultBase, new()
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (!validators.Any())
            return await next();

        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct)));
        var failures = results.SelectMany(r => r.Errors).ToList();
        if (failures.Count == 0)
            return await next();

        var response = new TResponse();
        response.Reasons.AddRange(failures.Select(f =>
            new Error(f.ErrorMessage).WithMetadata("Property", f.PropertyName)));
        return response;
    }
}
