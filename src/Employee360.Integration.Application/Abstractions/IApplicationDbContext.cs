using Employee360.Integration.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Employee360.Integration.Application.Abstractions;

/// <summary>
/// Persistence port. Exposes DbSets rather than hand-written repositories: the processor needs
/// little more than find/add/save, and EF Core already provides a unit of work.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<EventRecord> Events { get; }
    DbSet<DeadLetter> DeadLetters { get; }
    DbSet<EmployeeState> Employees { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
