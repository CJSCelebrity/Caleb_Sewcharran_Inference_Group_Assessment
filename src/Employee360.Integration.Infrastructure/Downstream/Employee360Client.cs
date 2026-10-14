using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using Employee360.Integration.Application.Abstractions;
using Employee360.Integration.Application.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Employee360.Integration.Infrastructure.Downstream;

public sealed class Employee360Options
{
    public const string SectionName = "Employee360";

    [Required, Url]
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Typed HttpClient for Employee 360. Retries, per-attempt timeouts and the circuit breaker
/// live in the resilience pipeline (see <see cref="Employee360ServiceCollectionExtensions"/>);
/// this class only turns the final outcome into Success / Transient / Permanent.
/// </summary>
public sealed class Employee360Client(HttpClient httpClient, ILogger<Employee360Client> logger) : IEmployee360Client
{
    public async Task<DeliveryResult> UpsertEmployeeAsync(
        Employee360UpdateRequest request,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, $"api/employees/{Uri.EscapeDataString(request.EmployeeId)}")
        {
            Content = JsonContent.Create(request)
        };

        // The eventId doubles as the downstream idempotency key. It covers the gap where
        // Employee 360 applied the update but our call timed out (or we crashed before
        // recording success): the retry is recognised and not applied twice.
        httpRequest.Headers.Add("Idempotency-Key", request.SourceEventId);
        httpRequest.Headers.Add("X-Correlation-Id", correlationId);

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return DeliveryResult.Success(statusCode);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = $"Employee 360 returned HTTP {statusCode}: {Truncate(body, 500)}";

            return IsTransient(response.StatusCode)
                ? DeliveryResult.Transient(detail, statusCode)
                : DeliveryResult.Permanent(detail, statusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Employee 360 unreachable");
            return DeliveryResult.Transient($"Employee 360 unreachable: {ex.Message}");
        }
        catch (ExecutionRejectedException ex)
        {
            // Polly: attempt/total timeout exceeded or the circuit breaker is open.
            return DeliveryResult.Transient($"Employee 360 call rejected by resilience pipeline: {ex.GetType().Name}: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeliveryResult.Transient("Employee 360 call timed out.");
        }
    }

    /// <summary>
    /// 5xx, 408 and 429 are worth retrying. Any other 4xx means the request itself is wrong,
    /// so retrying would just repeat the failure.
    /// </summary>
    internal static bool IsTransient(HttpStatusCode statusCode) =>
        (int)statusCode >= 500 ||
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests;

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}

public static class Employee360ServiceCollectionExtensions
{
    public static IHttpClientBuilder AddEmployee360Client(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(Employee360Options.SectionName);

        services.AddOptions<Employee360Options>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var builder = services.AddHttpClient<IEmployee360Client, Employee360Client>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<Employee360Options>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        });

        // Standard pipeline: total timeout -> retry (exponential backoff + jitter, transient
        // failures only) -> circuit breaker -> per-attempt timeout. Tuned from configuration.
        builder.AddStandardResilienceHandler(options => section.GetSection("Resilience").Bind(options));

        return builder;
    }
}
