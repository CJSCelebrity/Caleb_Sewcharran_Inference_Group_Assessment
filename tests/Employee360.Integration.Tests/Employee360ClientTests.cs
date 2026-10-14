using System.Net;
using Employee360.Integration.Application.Abstractions;
using Employee360.Integration.Application.Contracts;
using Employee360.Integration.Infrastructure.Downstream;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Employee360.Integration.Tests;

/// <summary>
/// Exercises the real HttpClient registration, including the resilience pipeline,
/// against a stubbed HTTP handler.
/// </summary>
public sealed class Employee360ClientTests
{
    private static readonly Employee360UpdateRequest Request = new(
        "EMP10452", "Amira", "Khan", "Data & AI", "Cape Town",
        new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero), "D365-HR", "EVT-10452-001");

    [Fact]
    public async Task Retries_transient_5xx_responses_with_the_same_idempotency_key_then_succeeds()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var result = await CreateClient(handler).UpsertEmployeeAsync(Request, "corr-1", CancellationToken.None);

        Assert.Equal(DeliveryOutcome.Success, result.Outcome);
        Assert.Equal(3, handler.IdempotencyKeys.Count);
        Assert.All(handler.IdempotencyKeys, key => Assert.Equal("EVT-10452-001", key));
    }

    [Fact]
    public async Task Does_not_retry_a_bad_request()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest);

        var result = await CreateClient(handler).UpsertEmployeeAsync(Request, "corr-1", CancellationToken.None);

        Assert.Equal(DeliveryOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(400, result.StatusCode);
        Assert.Single(handler.IdempotencyKeys);
    }

    [Fact]
    public async Task Reports_a_transient_failure_once_retries_are_exhausted()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);

        var result = await CreateClient(handler).UpsertEmployeeAsync(Request, "corr-1", CancellationToken.None);

        Assert.Equal(DeliveryOutcome.TransientFailure, result.Outcome);
        Assert.Equal(4, handler.IdempotencyKeys.Count); // 1 attempt + 3 retries
    }

    private static IEmployee360Client CreateClient(StubHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Employee360:BaseUrl"] = "http://employee360.test/",
                ["Employee360:Resilience:Retry:MaxRetryAttempts"] = "3",
                ["Employee360:Resilience:Retry:Delay"] = "00:00:00.010",
                ["Employee360:Resilience:AttemptTimeout:Timeout"] = "00:00:01",
                ["Employee360:Resilience:TotalRequestTimeout:Timeout"] = "00:00:10"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmployee360Client(configuration).ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider().GetRequiredService<IEmployee360Client>();
    }

    /// <summary>Returns the given status codes in order, repeating the last one.</summary>
    private sealed class StubHandler(params HttpStatusCode[] responses) : HttpMessageHandler
    {
        public List<string> IdempotencyKeys { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IdempotencyKeys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            var status = responses[Math.Min(IdempotencyKeys.Count - 1, responses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }
}
