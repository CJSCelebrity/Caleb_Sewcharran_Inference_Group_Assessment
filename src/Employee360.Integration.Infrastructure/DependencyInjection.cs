using Employee360.Integration.Application.Abstractions;
using Employee360.Integration.Infrastructure.Downstream;
using Employee360.Integration.Infrastructure.Messaging;
using Employee360.Integration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Employee360.Integration.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddDbContext<IntegrationDbContext>(options =>
            options.UseSqlite(configuration.GetConnectionString("IntegrationDb")));
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<IntegrationDbContext>());

        services.Configure<BusOptions>(configuration.GetSection(BusOptions.SectionName));
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddHostedService<EventConsumer>();

        services.AddEmployee360Client(configuration);

        return services;
    }

    /// <summary>
    /// Simplification for the exercise: create the schema on startup instead of running migrations.
    /// </summary>
    public static void EnsureIntegrationDatabaseCreated(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().Database.EnsureCreated();
    }
}
