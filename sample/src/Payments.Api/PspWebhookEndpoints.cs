using MediatR;
using Payments.Application;

namespace Payments.Api;

public sealed record PspPayoutNotification(Guid PayoutId, string Status, string? Reference, string? Reason);

public static class PspWebhookEndpoints
{
    public static IEndpointRouteBuilder MapPspWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // Called by the payment service provider after the bank transfer is executed or rejected.
        // Request signature is verified by PspSignatureMiddleware (not shown in this sample).
        app.MapPost("/webhooks/psp/payouts", async (PspPayoutNotification body, ISender sender, CancellationToken ct) =>
        {
            var command = new ConfirmPayoutCommand(
                body.PayoutId,
                Succeeded: body.Status == "executed",
                PspReference: body.Reference,
                FailureReason: body.Reason);

            var result = await sender.Send(command, ct);
            return result.IsSuccess ? Results.Ok() : Results.UnprocessableEntity(result.Errors.Select(e => e.Message));
        });

        return app;
    }
}
