using MediatR;
using Orders.Application;

namespace Orders.Api;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        // Called by a valuation specialist after the customer accepted the price.
        app.MapPost("/orders/{orderId:guid}/settle", async (Guid orderId, ISender sender, CancellationToken ct) =>
        {
            var result = await sender.Send(new SettleOrderCommand(orderId), ct);
            return result.IsSuccess
                ? Results.NoContent()
                : Results.UnprocessableEntity(result.Errors.Select(e => e.Message));
        });

        return app;
    }
}
