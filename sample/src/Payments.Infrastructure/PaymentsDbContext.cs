using Microsoft.EntityFrameworkCore;
using Payments.Domain;

namespace Payments.Infrastructure;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<CustomerAccount> CustomerAccounts => Set<CustomerAccount>();
    public DbSet<Payout> Payouts => Set<Payout>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerAccount>(b =>
        {
            b.ToTable("customer_accounts");
            b.HasKey(a => a.CustomerId);
            b.Property(a => a.CustomerId).HasColumnName("customer_id");
            b.Property(a => a.Iban).HasColumnName("iban");
            b.Property(a => a.KycStatus).HasColumnName("kyc_status");
            b.Property(a => a.IsBlocked).HasColumnName("is_blocked");
        });

        modelBuilder.Entity<Payout>(b =>
        {
            b.ToTable("payouts");
            b.HasKey(p => p.Id);
            b.Ignore(p => p.DomainEvents);
            b.Property(p => p.Id).HasColumnName("id");
            b.Property(p => p.OrderId).HasColumnName("order_id");
            b.Property(p => p.CustomerId).HasColumnName("customer_id");
            b.Property(p => p.Amount).HasColumnName("amount");
            b.Property(p => p.Iban).HasColumnName("iban");
            b.Property(p => p.Status).HasColumnName("status");
            b.Property(p => p.PspReference).HasColumnName("psp_reference");
            b.Property(p => p.FailureReason).HasColumnName("failure_reason");
            b.Property(p => p.CreatedAt).HasColumnName("created_at");
        });
    }
}
