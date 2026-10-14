using System.Text.Json.Serialization;
using Employee360.Integration.Api.Endpoints;
using Employee360.Integration.Application;
using Employee360.Integration.Application.Processing;
using Employee360.Integration.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ReferenceDataOptions>(builder.Configuration.GetSection(ReferenceDataOptions.SectionName));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.Services.EnsureIntegrationDatabaseCreated();

app.MapHealthChecks("/health");
app.MapSimulationEndpoints();
app.MapEventEndpoints();
app.MapEmployeeEndpoints();
app.MapDeadLetterEndpoints();
app.MapStatusEndpoints();

app.Run();
