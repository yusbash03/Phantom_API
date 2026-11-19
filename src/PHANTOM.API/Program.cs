using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using PHANTOM.API.Infrastructure;
using PHANTOM.API.Middleware;
using PHANTOM.Application;
using PHANTOM.Application.Abstractions;
using PHANTOM.Infrastructure;
using PHANTOM.Infrastructure.Identity;
using PHANTOM.Infrastructure.Persistence;
using PHANTOM.Infrastructure.Seeding;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// The build-time OpenAPI generator (GetDocument.Insider) executes this file to describe the API.
// It must not touch the database, and it has no secrets, so it gets an inert placeholder key.
var isOpenApiGeneration = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
if (isOpenApiGeneration)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Jwt:SigningKey"] = "openapi-generation-placeholder-never-used-to-sign",
    });
}

// ---- Logging -------------------------------------------------------------------------------
builder.Host.UseSerilog((context, configuration) =>
{
    configuration.ReadFrom.Configuration(context.Configuration).Enrich.FromLogContext();
    if (context.HostingEnvironment.IsDevelopment())
    {
        configuration.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
            formatProvider: System.Globalization.CultureInfo.InvariantCulture);
    }
    else
    {
        configuration.WriteTo.Console(new RenderedCompactJsonFormatter());
    }
});

// ---- Application & infrastructure ----------------------------------------------------------
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

// ---- Authentication & authorisation --------------------------------------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtSigningKeys.Create(jwt.SigningKey),
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = PhantomClaimTypes.Name,
            RoleClaimType = PhantomClaimTypes.Role,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };
    });
builder.Services.AddAuthorization(Policies.Configure);

// ---- HTTP pipeline services ----------------------------------------------------------------
// Strict numbers and string enums: the OpenAPI contract (and so the generated frontend types) stay precise.
static void ConfigureJson(System.Text.Json.JsonSerializerOptions json)
{
    json.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
    json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
}

builder.Services.AddControllers().AddJsonOptions(o => ConfigureJson(o.JsonSerializerOptions));
builder.Services.ConfigureHttpJsonOptions(o => ConfigureJson(o.SerializerOptions));
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.Items[CorrelationIdMiddleware.ItemKey] ?? ctx.HttpContext.TraceIdentifier;
    });
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddRateLimiter(options => RateLimitPolicies.Configure(options, builder.Configuration));

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Render terminates TLS at a single load balancer hop; only then may proxies be trusted wholesale.
    if (builder.Configuration.GetValue<bool>("ForwardedHeaders:TrustPlatformProxy"))
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.ForwardLimit = 1;
    }
});

builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "PHANTOM API",
            Version = "v1",
            Description = "Incident intelligence and response coordination API. AI output is advisory; operational decisions are made by authorised users.",
        };
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        };
        document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];

        // A nullable enum property (e.g. Severity?) makes the generator add null to the shared enum
        // component. Nullability is already expressed per property (oneOf null/$ref), so keep enums pure.
        foreach (var schema in document.Components.Schemas?.Values.OfType<OpenApiSchema>() ?? [])
        {
            if (schema.Enum is { Count: > 0 } values && values.Any(v => v is null))
            {
                schema.Enum = values.Where(v => v is not null).ToList();
            }
        }

        return Task.CompletedTask;
    });
});

var app = builder.Build();

// ---- Middleware order matters --------------------------------------------------------------
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
}

var exposeApiDocs = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:ExposeUi");
if (exposeApiDocs)
{
    // Static UI files are served before authorisation; the API it calls still enforces auth.
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "PHANTOM API v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseSerilogRequestLogging();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
if (exposeApiDocs)
{
    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();


// ---- Startup tasks ---------------------------------------------------------------------------
if (!isOpenApiGeneration)
{
    await using var scope = app.Services.CreateAsyncScope();
    if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
    {
        await scope.ServiceProvider.GetRequiredService<PhantomDbContext>().Database.MigrateAsync();
    }

    await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(CancellationToken.None);
}

await app.RunAsync();

/// <summary>Entry point; public so integration tests can use WebApplicationFactory.</summary>
public partial class Program;
