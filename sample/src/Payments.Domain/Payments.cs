using BuildingBlocks;
using FluentResults;

namespace Payments.Domain;

public enum KycStatus
{
    Pending,
    Verified,
    Rejected
}

public sealed class CustomerAccount
{
    public Guid CustomerId { get; private set; }
    public string? Iban { get; private set; }
    public KycStatus KycStatus { get; private set; }
    public bool IsBlocked { get; private set; }
}

public enum PayoutStatus
{
    Pending,
    Completed,
    Failed
}

public sealed class Payout : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid CustomerId { get; private set; }
    public decimal Amount { get; private set; }
    public string Iban { get; private set; } = null!;
    public PayoutStatus Status { get; private set; }
    public string? PspReference { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Payout()
    {
    }

    public static Result<Payout> Create(
        Guid orderId, Guid customerId, decimal amount, string iban, decimal maxAmount, DateTime now)
    {
        if (amount > maxAmount)
            return Result.Fail<Payout>(new PayoutLimitExceededError(orderId, amount, maxAmount));

        var payout = new Payout
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CustomerId = customerId,
            Amount = amount,
            Iban = iban,
            Status = PayoutStatus.Pending,
            CreatedAt = now
        };
        payout.Raise(new PayoutRequestedEvent(payout.Id));
        return Result.Ok(payout);
    }

    public Result MarkCompleted(string pspReference)
    {
        if (Status != PayoutStatus.Pending)
            return Result.Fail(new PayoutAlreadyFinalizedError(Id, Status));

        Status = PayoutStatus.Completed;
        PspReference = pspReference;
        return Result.Ok();
    }

    public Result MarkFailed(string reason)
    {
        if (Status != PayoutStatus.Pending)
            return Result.Fail(new PayoutAlreadyFinalizedError(Id, Status));

        Status = PayoutStatus.Failed;
        FailureReason = reason;
        return Result.Ok();
    }
}

public sealed record PayoutRequestedEvent(Guid PayoutId) : IDomainEvent;

public sealed class PayoutLimitExceededError(Guid orderId, decimal amount, decimal maxAmount)
    : Error($"Payout for order {orderId} of {amount} exceeds the limit of {maxAmount}.");

public sealed class PayoutAlreadyFinalizedError(Guid payoutId, PayoutStatus status)
    : Error($"Payout {payoutId} is already {status}.");
