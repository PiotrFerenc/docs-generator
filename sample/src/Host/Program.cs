using BuildingBlocks;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;
using Orders.Api;
using Payments.Api;
using Payments.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var assemblies = new[]
{
    typeof(Orders.Application.SettleOrderCommand).Assembly,
    typeof(Payments.Application.RequestPayoutCommand).Assembly
};

builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblies(assemblies);
    cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
});
builder.Services.AddValidatorsFromAssemblies(assemblies);
builder.Services.AddFeatureManagement();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<PaymentsDbContext>(o => o.UseInMemoryDatabase("payments"));
builder.Services.AddPayments(builder.Configuration);

// Orders persistence (IOrderRepository) and domain event dispatching are not part of this sample.

var app = builder.Build();
app.MapOrderEndpoints();
app.MapPspWebhookEndpoints();
app.Run();
