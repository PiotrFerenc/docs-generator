using FluentResults;
using FluentValidation;
using MediatR;
using Orders.Domain;

namespace Orders.Application;

public sealed record SettleOrderCommand(Guid OrderId) : IRequest<Result>;

public sealed class SettleOrderCommandValidator : AbstractValidator<SettleOrderCommand>
{
    public SettleOrderCommandValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
    }
}

public sealed class SettleOrderHandler(IOrderRepository orders, TimeProvider clock)
    : IRequestHandler<SettleOrderCommand, Result>
{
    public async Task<Result> Handle(SettleOrderCommand command, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(command.OrderId, ct);
        if (order is null)
            return Result.Fail(new OrderNotFoundError(command.OrderId));

        var result = order.Settle(clock.GetUtcNow().UtcDateTime);
        if (result.IsFailed)
            return result;

        await orders.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
