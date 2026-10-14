using Employee360.Integration.Application.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Employee360.Integration.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<EventValidator>();
        services.AddSingleton<LocationNormaliser>();
        services.AddScoped<EmployeeEventProcessor>();
        return services;
    }
}
