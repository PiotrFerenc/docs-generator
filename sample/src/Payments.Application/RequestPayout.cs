using FluentResults;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using Payments.Domain;

namespace Payments.Application;

public sealed record RequestPayoutCommand(Guid OrderId, Guid CustomerId, decimal Amount) : IRequest<Result>;

public sealed class RequestPayoutCommandValidator : AbstractValidator<RequestPayoutCommand>
{
    public RequestPayoutCommandValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
    }
}

public sealed class RequestPayoutHandler(
    ICustomerAccountRepository accounts,
    IPayoutRepository payouts,
    IFeatureManager features,
    IOptions<PayoutOptions> options,
    TimeProvider clock)
    : IRequestHandler<RequestPayoutCommand, Result>
{
    public async Task<Result> Handle(RequestPayoutCommand command, CancellationToken ct)
    {
        if (!await features.IsEnabledAsync("Payouts.Enabled"))
            return Result.Ok();

        if (await payouts.ExistsForOrderAsync(command.OrderId, ct))
            return Result.Ok();

        var account = await accounts.GetActiveAsync(command.CustomerId, ct);
        if (account is null)
            return Result.Fail(new AccountNotActiveError(command.CustomerId));

        if (account.KycStatus != KycStatus.Verified)
            return Result.Fail(new KycNotVerifiedError(command.CustomerId));

        if (string.IsNullOrWhiteSpace(account.Iban))
            return Result.Fail(new MissingIbanError(command.CustomerId));

        var payout = Payout.Create(
            command.OrderId,
            command.CustomerId,
            command.Amount,
            account.Iban,
            options.Value.MaxAmount,
            clock.GetUtcNow().UtcDateTime);
        if (payout.IsFailed)
            return payout.ToResult();

        payouts.Add(payout.Value);
        await payouts.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public sealed class AccountNotActiveError(Guid customerId)
    : Error($"Account of customer {customerId} does not exist or is blocked.");

public sealed class KycNotVerifiedError(Guid customerId)
    : Error($"Customer {customerId} has not passed KYC verification.");

public sealed class MissingIbanError(Guid customerId)
    : Error($"Customer {customerId} has no bank account number.");
