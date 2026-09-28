using Payments.Domain;

namespace Payments.Application;

public interface ICustomerAccountRepository
{
    Task<CustomerAccount?> GetActiveAsync(Guid customerId, CancellationToken ct);
}

public interface IPayoutRepository
{
    Task<bool> ExistsForOrderAsync(Guid orderId, CancellationToken ct);
    Task<Payout?> GetByIdAsync(Guid payoutId, CancellationToken ct);
    void Add(Payout payout);
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed class PayoutOptions
{
    public const string SectionName = "Payouts";

    public decimal MaxAmount { get; init; } = 20_000m;
}
