using FluentResults;
using FluentValidation;
using MediatR;

namespace Payments.Application;

public sealed record ConfirmPayoutCommand(Guid PayoutId, bool Succeeded, string? PspReference, string? FailureReason)
    : IRequest<Result>;

public sealed class ConfirmPayoutCommandValidator : AbstractValidator<ConfirmPayoutCommand>
{
    public ConfirmPayoutCommandValidator()
    {
        RuleFor(x => x.PayoutId).NotEmpty();
        RuleFor(x => x.PspReference).NotEmpty().When(x => x.Succeeded);
        RuleFor(x => x.FailureReason).NotEmpty().When(x => !x.Succeeded);
    }
}

public sealed class ConfirmPayoutHandler(IPayoutRepository payouts)
    : IRequestHandler<ConfirmPayoutCommand, Result>
{
    public async Task<Result> Handle(ConfirmPayoutCommand command, CancellationToken ct)
    {
        var payout = await payouts.GetByIdAsync(command.PayoutId, ct);
        if (payout is null)
            return Result.Fail(new PayoutNotFoundError(command.PayoutId));

        var result = command.Succeeded
            ? payout.MarkCompleted(command.PspReference!)
            : payout.MarkFailed(command.FailureReason!);
        if (result.IsFailed)
            return result;

        await payouts.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

public sealed class PayoutNotFoundError(Guid payoutId)
    : Error($"Payout {payoutId} was not found.");
