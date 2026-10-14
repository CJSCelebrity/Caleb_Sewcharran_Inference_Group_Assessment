using System.Text.Json;
using Employee360.Integration.Application;
using Employee360.Integration.Application.Abstractions;
using Employee360.Integration.Application.Contracts;
using Employee360.Integration.Application.Messaging;
using Employee360.Integration.Application.Processing;
using Employee360.Integration.Domain.Entities;
using Employee360.Integration.Infrastructure.Messaging;
using Employee360.Integration.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Employee360.Integration.Tests;

/// <summary>
/// Real processor, real EF Core model on in-memory SQLite, fake Employee 360 and bus.
/// Each delivery gets its own DI scope / DbContext, as it does in the running service.
/// </summary>
internal sealed class TestHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public FakeEmployee360Client Downstream { get; } = new();
    public RecordingEventBus Bus { get; } = new();

    public TestHarness(int maxDeliveryCount = 3)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<IntegrationDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<IntegrationDbContext>());
        services.Configure<ReferenceDataOptions>(options =>
        {
            options.Locations["Cape Town"] = ["CPT", "Kaapstad"];
            options.Locations["Johannesburg"] = ["JHB"];
        });
        services.Configure<BusOptions>(options =>
        {
            options.MaxDeliveryCount = maxDeliveryCount;
            options.RedeliveryDelay = TimeSpan.Zero;
        });
        services.AddApplication();
        services.AddSingleton<IEmployee360Client>(Downstream);
        services.AddSingleton<IEventBus>(Bus);

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().Database.EnsureCreated();
    }

    public async Task<ProcessingOutcome> DeliverAsync(string body, int deliveryCount = 1)
    {
        using var scope = _provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<EmployeeEventProcessor>();
        return await processor.ProcessAsync(Message(body, deliveryCount), CancellationToken.None);
    }

    public EventConsumer CreateConsumer() =>
        ActivatorUtilities.CreateInstance<EventConsumer>(_provider, NullLogger<EventConsumer>.Instance);

    public async Task<T> QueryAsync<T>(Func<IntegrationDbContext, Task<T>> query)
    {
        using var scope = _provider.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<IntegrationDbContext>());
    }

    public Task<EventRecord?> GetEventAsync(string eventId) =>
        QueryAsync(db => db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId));

    public Task<List<DeadLetter>> GetDeadLettersAsync() =>
        QueryAsync(db => db.DeadLetters.AsNoTracking().OrderBy(d => d.Id).ToListAsync());

    public static BusMessage Message(string body, int deliveryCount = 1) =>
        new(Guid.NewGuid().ToString("N"), body, deliveryCount, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}

internal static class SampleEvents
{
    public static string EmployeeUpdated(
        string? eventId = "EVT-10452-001",
        string? employeeId = "EMP10452",
        string? location = "Cape Town",
        string? effectiveDate = "2026-10-01T08:30:00Z",
        string? department = "Data & AI") =>
        JsonSerializer.Serialize(new
        {
            eventId,
            eventType = "EmployeeUpdated",
            employeeId,
            firstName = "Amira",
            lastName = "Khan",
            department,
            location,
            effectiveDate
        });
}

internal sealed class FakeEmployee360Client : IEmployee360Client
{
    private readonly Queue<Func<DeliveryResult>> _script = new();

    public List<Employee360UpdateRequest> Requests { get; } = new();

    /// <summary>Queue up results for the next calls; once empty every call succeeds.</summary>
    public void Returns(params DeliveryResult[] results)
    {
        foreach (var result in results)
        {
            _script.Enqueue(() => result);
        }
    }

    public void Throws(Exception exception) => _script.Enqueue(() => throw exception);

    public Task<DeliveryResult> UpsertEmployeeAsync(Employee360UpdateRequest request, string correlationId, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var next = _script.Count > 0 ? _script.Dequeue() : () => DeliveryResult.Success(200);
        return Task.FromResult(next());
    }
}

internal sealed class RecordingEventBus : IEventBus
{
    public List<string> Published { get; } = new();
    public List<BusMessage> Redeliveries { get; } = new();

    public ValueTask<string> PublishAsync(string body, CancellationToken cancellationToken = default)
    {
        Published.Add(body);
        return ValueTask.FromResult(Guid.NewGuid().ToString("N"));
    }

    public void ScheduleRedelivery(BusMessage message, TimeSpan delay) => Redeliveries.Add(message.NextDelivery());

    public IAsyncEnumerable<BusMessage> ReadAllAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Tests drive the consumer directly via HandleAsync.");

    public int PendingCount => 0;
}
