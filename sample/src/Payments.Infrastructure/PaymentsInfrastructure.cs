using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application;
using Payments.Domain;

namespace Payments.Infrastructure;

public static class PaymentsModule
{
    public static IServiceCollection AddPayments(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PayoutOptions>(configuration.GetSection(PayoutOptions.SectionName));
        services.AddScoped<ICustomerAccountRepository, CustomerAccountRepository>();
        services.AddScoped<IPayoutRepository, PayoutRepository>();
        return services;
    }
}

// PaymentsDbContext maps CustomerAccount to table "customer_accounts" and Payout to table "payouts"
// with snake_case column names (not shown in this sample).

internal sealed class CustomerAccountRepository(PaymentsDbContext db) : ICustomerAccountRepository
{
    public Task<CustomerAccount?> GetActiveAsync(Guid customerId, CancellationToken ct) =>
        db.CustomerAccounts
            .Where(a => a.CustomerId == customerId && !a.IsBlocked)
            .FirstOrDefaultAsync(ct);
}

internal sealed class PayoutRepository(PaymentsDbContext db) : IPayoutRepository
{
    // A failed payout does not block a new one for the same order.
    public Task<bool> ExistsForOrderAsync(Guid orderId, CancellationToken ct) =>
        db.Payouts.AnyAsync(p => p.OrderId == orderId && p.Status != PayoutStatus.Failed, ct);

    public Task<Payout?> GetByIdAsync(Guid payoutId, CancellationToken ct) =>
        db.Payouts.FirstOrDefaultAsync(p => p.Id == payoutId, ct);

    public void Add(Payout payout) => db.Payouts.Add(payout);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
