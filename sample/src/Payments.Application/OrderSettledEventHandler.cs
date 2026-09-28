using MediatR;
using Microsoft.Extensions.Logging;
using Orders.Domain;

namespace Payments.Application;

public sealed class OrderSettledEventHandler(ISender sender, ILogger<OrderSettledEventHandler> logger)
    : INotificationHandler<OrderSettledEvent>
{
    public async Task Handle(OrderSettledEvent notification, CancellationToken ct)
    {
        // Vouchers are issued by the Vouchers module.
        if (notification.PayoutMethod != PayoutMethod.BankTransfer)
            return;

        var result = await sender.Send(
            new RequestPayoutCommand(notification.OrderId, notification.CustomerId, notification.Amount), ct);

        if (result.IsFailed)
            logger.LogWarning(
                "Payout for order {OrderId} was not requested: {Errors}",
                notification.OrderId,
                string.Join("; ", result.Errors.Select(e => e.Message)));
    }
}
