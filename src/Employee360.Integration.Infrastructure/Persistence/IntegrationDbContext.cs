using Employee360.Integration.Application.Abstractions;
using Employee360.Integration.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Employee360.Integration.Infrastructure.Persistence;

public sealed class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options) : DbContext(options), IApplicationDbContext
{
    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();
    public DbSet<EmployeeState> Employees => Set<EmployeeState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecord>(entity =>
        {
            // The primary key on eventId is what makes duplicate detection safe even if
            // two consumers ever raced: the second insert fails instead of double-processing.
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.HasIndex(e => e.EmployeeId);
        });

        modelBuilder.Entity<DeadLetter>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Reason).HasConversion<string>();
            entity.HasIndex(d => d.EventId);
        });

        modelBuilder.Entity<EmployeeState>(entity => entity.HasKey(e => e.EmployeeId));
    }
}
