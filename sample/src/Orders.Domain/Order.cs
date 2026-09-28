using BuildingBlocks;
using FluentResults;

namespace Orders.Domain;

public enum OrderStatus
{
    Received,
    Valued,
    Settled,
    Cancelled
}

public enum PayoutMethod
{
    BankTransfer,
    Voucher
}

public sealed class Order : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public PayoutMethod PayoutMethod { get; private set; }
    public decimal? AcceptedPrice { get; private set; }
    public DateTime? SettledAt { get; private set; }

    public Result Settle(DateTime now)
    {
        if (Status != OrderStatus.Valued)
            return Result.Fail(new OrderNotValuedError(Id, Status));

        if (AcceptedPrice is null or <= 0m)
            return Result.Fail(new MissingAcceptedPriceError(Id));

        Status = OrderStatus.Settled;
        SettledAt = now;
        Raise(new OrderSettledEvent(Id, CustomerId, AcceptedPrice.Value, PayoutMethod));
        return Result.Ok();
    }
}

public sealed record OrderSettledEvent(Guid OrderId, Guid CustomerId, decimal Amount, PayoutMethod PayoutMethod)
    : IDomainEvent;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid orderId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed class OrderNotFoundError(Guid orderId)
    : Error($"Order {orderId} was not found.");

public sealed class OrderNotValuedError(Guid orderId, OrderStatus status)
    : Error($"Order {orderId} cannot be settled in status {status}.");

public sealed class MissingAcceptedPriceError(Guid orderId)
    : Error($"Order {orderId} has no accepted price.");
