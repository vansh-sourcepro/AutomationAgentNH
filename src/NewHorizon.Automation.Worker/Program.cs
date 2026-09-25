using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NewHorizon.Automation.Application;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.ErpClient;
using NewHorizon.Automation.Infrastructure;
using NewHorizon.Automation.Infrastructure.Time;
using NewHorizon.Automation.Worker.Configuration;
using NewHorizon.Automation.Worker.Diagnostics;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IndentToPo;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor;
using NewHorizon.Automation.Worker.Logging;
using NewHorizon.Automation.Worker.Services;
using Serilog;

// Bootstrap logger: captures failures that happen before the host (and its configured logger) exists.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    // ContentRootPath is pinned to the binary location: as a Windows Service the working directory
    // is %WINDIR%\System32, which would otherwise hide appsettings.json.
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Host.UseWindowsService(options => options.ServiceName = "NewHorizon Automation Agent");

    builder.Host.UseSerilog((context, _, loggerConfiguration) =>
        SerilogConfigurator.Configure(loggerConfiguration, context.Configuration));

    builder.Services.AddAutomationAgentOptions(builder.Configuration);
    builder.Services.AddSingleton(new AgentRuntimeInfo(DateTimeOffset.UtcNow));
    builder.Services.AddProblemDetails();

    // The ERP frontend (WebApp2) reaches the management API straight from the browser, carrying the
    // same bearer token the ERP issued it at login. We validate that token with the ERP's own JWT
    // settings — mirrored, not shared at runtime. Blank signing key ⇒ the scheme is not registered
    // and the browser-facing endpoints stay unavailable; the API-key machine callers are unaffected.
    var inboundJwt = builder.Configuration.GetSection(InboundJwtOptions.SectionName).Get<InboundJwtOptions>()
        ?? new InboundJwtOptions();

    // When the JWT is configured, Bearer is the default scheme so UseAuthentication() populates
    // HttpContext.User automatically and RequireAuthorization() has a scheme to challenge with.
    var authentication = inboundJwt.IsConfigured
        ? builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        : builder.Services.AddAuthentication();

    if (inboundJwt.IsConfigured)
    {
        // The ERP signs its tokens HS256 with Encoding.ASCII.GetBytes("sourcepro-secret-key") —
        // 160 bits. Its own (older) IdentityModel accepts that; Microsoft.IdentityModel 8.x here
        // hard-rejects HS256 keys below 256 bits (IDX10720), so the built-in
        // SymmetricSignatureProvider cannot use the key at all and every token comes back
        // "signature key was not found". We verify the HMAC ourselves against the raw bytes —
        // identical crypto, nothing weakened — and let the library still check iss/aud/exp.
        var signingKeyBytes = Encoding.ASCII.GetBytes(inboundJwt.SigningKey);

        authentication.AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidIssuer = inboundJwt.Issuer,
                ValidAudience = inboundJwt.Audience,

                RequireSignedTokens = true,
                ValidateIssuerSigningKey = false,
                SignatureValidator = (token, _) =>
                {
                    var parts = token.Split('.');
                    if (parts.Length != 3)
                    {
                        throw new SecurityTokenInvalidSignatureException("The token is not a signed JWS.");
                    }

                    string alg;
                    try
                    {
                        alg = JsonSerializer.Deserialize<JsonElement>(Base64UrlEncoder.Decode(parts[0]))
                            .GetProperty("alg").GetString() ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        throw new SecurityTokenInvalidSignatureException("The token header could not be read.", ex);
                    }

                    if (!string.Equals(alg, "HS256", StringComparison.Ordinal))
                    {
                        throw new SecurityTokenInvalidSignatureException($"Unexpected signing algorithm '{alg}'.");
                    }

                    var provided = Base64UrlEncoder.DecodeBytes(parts[2]);
                    var computed = HMACSHA256.HashData(signingKeyBytes, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));

                    if (!CryptographicOperations.FixedTimeEquals(provided, computed))
                    {
                        throw new SecurityTokenInvalidSignatureException("The token signature does not match.");
                    }

                    return new JsonWebToken(token);
                },
            };

            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    // The Serilog config drops Microsoft.AspNetCore below Warning, which hides the
                    // built-in "Bearer was not authenticated" line — so the reason is logged here
                    // under a category that survives.
                    context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("InboundJwt")
                        .LogWarning(
                            context.Exception,
                            "An inbound ERP bearer token was rejected: {Message}",
                            context.Exception.Message);

                    return Task.CompletedTask;
                },
            };
        });
    }

    builder.Services.AddAuthorization();

    var corsOrigins = builder.Configuration.GetSection(AgentCorsOptions.SectionName).Get<AgentCorsOptions>()?.AllowedOrigins
        ?? [];

    builder.Services.AddCors(cors => cors.AddPolicy("webapp", policy =>
    {
        if (corsOrigins.Count > 0)
        {
            policy.WithOrigins([.. corsOrigins])
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    }));

    // The database is optional. Without it the agent still signs in to the ERP and serves the
    // request-driven endpoints; what it loses is everything that needs to remember state across a
    // restart — the job queue, job history and the runtime config table — so the timer-driven
    // cycle is switched off with them rather than left running with nowhere to checkpoint.
    var connectionString =
        builder.Configuration[
            $"{AutomationAgentOptions.SectionName}:{nameof(AutomationAgentOptions.Database)}:{nameof(DatabaseOptions.ConnectionString)}"];

    var databaseConfigured = !string.IsNullOrWhiteSpace(connectionString);

    // Configured is not the same as usable. A connection string naming a database the agent's
    // login cannot open never recovers, and EF's retrying execution strategy classes that SQL
    // error as transient — so every scheduler tick, dispatcher pass and orphan sweep would retry
    // it three times and log a stack trace each, for as long as the process runs. Probed once
    // here instead, and the three timers left unstarted if the answer is no: the endpoints and
    // the health check stay in place, so the database still reports its own state honestly.
    var database = DatabaseAvailability.Probe(connectionString);

    builder.Services.AddAutomationApplication();

    if (databaseConfigured)
    {
        builder.Services.AddAutomationInfrastructure(connectionString!);

        // The engine and the enqueue funnel both need IJobRepository, which the infrastructure
        // above supplies. Without a database there is nowhere to keep a job, so neither is
        // registered — and the host no longer fails its service validation at startup.
        builder.Services.AddAutomationJobProcessing();

        if (database.Usable)
        {
            // Tracking writes for the same reason the timers below are gated: against a database
            // that cannot be opened, every write retries three times with backoff before it fails,
            // and a conversion would pay that on every indent. Left unregistered, the no-op
            // fallback stands and the conversion runs at full speed with no history.
            builder.Services.AddProcessTracking();

            // The timer, dispatcher and orphan sweep. Registered here rather than inside
            // AddAutomationInfrastructure so a test host can compose the same application without them.
            builder.Services.AddAutomationHostedServices();

            // Each automation flow's own timers, under the same usable-database gate.
            builder.Services.AddIndentToPoScheduler();
        }
    }
    else
    {
        // SystemClock normally arrives with the infrastructure. ErpTokenProvider needs it to decide
        // when its token has expired, so it is registered on its own here.
        builder.Services.AddSingleton<IClock, SystemClock>();
    }

    builder.Services.AddErpClient();

    // The browser-facing PO Automation endpoints are authorised against Role Management forms
    // 011171 / 011172. The ERP JWT carries no rights, so the agent asks the ERP — with the
    // caller's own bearer token (this client has no auth handler of its own) — and caches the
    // answer for a minute so the screen's 30s auto-refresh does not hammer it.
    builder.Services.AddMemoryCache();
    builder.Services.AddHttpClient(ErpUserRightsService.HttpClientName, (sp, client) =>
    {
        client.BaseAddress = new Uri(
            sp.GetRequiredService<AutomationAgentOptions>().ErpApi.BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(15);
    });
    builder.Services.AddSingleton<IErpUserRightsService, ErpUserRightsService>();

    // Each automation flow's host-side composition. A new flow adds its own line here.
    builder.Services.AddIndentToPoOrchestration();

    // Sign in to the ERP at startup so the token is cached before the first cycle runs.
    builder.Services.AddErpLoginStartup();

    // Indent → PO conversion has a background trigger: PoAutomationSchedulerService, registered
    // above under the usable-database gate by AddIndentToPoScheduler. It is off by default, so a
    // fresh install still converts nothing until a person turns it on from the PO Automation
    // screen. See docs/flows/indent-to-po/README.md.

    // The management/read API. Loopback binding is the outer boundary for the API-key callers; the
    // browser-facing endpoints additionally require a valid ERP bearer token and a CORS allow-list.
    var hostOptions = builder.Configuration
        .GetSection($"{AutomationAgentOptions.SectionName}:{nameof(AutomationAgentOptions.Host)}")
        .Get<AgentHostOptions>() ?? new AgentHostOptions();

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        if (hostOptions.BindToLoopbackOnly)
        {
            kestrel.ListenLocalhost(hostOptions.ManagementApiPort);
        }
        else
        {
            kestrel.ListenAnyIP(hostOptions.ManagementApiPort);
        }

        kestrel.AddServerHeader = false;
    });

    var app = builder.Build();

    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();

    app.UseCors("webapp");
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthEndpoints();

    // Each automation flow maps its own endpoints and applies its own database/JWT gates.
    app.MapIndentToPoEndpoints(databaseConfigured, database.Usable, inboundJwt.IsConfigured);
    app.MapIssueToShopFloorEndpoints();

    // The startup verdict on the database, logged after the host is built so it reaches the
    // configured sinks rather than only the bootstrap console.
    if (databaseConfigured)
    {
        app.MapJobEndpoints();
        app.MapConfigEndpoints();

        if (!database.Usable)
        {
            Log.Error(
                "The automation database cannot be opened, so the AutoShop cycle timer, the job "
                + "dispatcher and the orphan sweep are not running; the health check reports the "
                + "database Unhealthy. ERP sign-in and /api/automation/indent-to-po are unaffected. "
                + "{Reason}",
                database.Reason);
        }
        else if (database.Transient)
        {
            Log.Warning(
                "The automation database did not answer at startup. The cycle timer, dispatcher and "
                + "orphan sweep are running and will retry. {Reason}",
                database.Reason);
        }
    }
    else
    {
        // Not mapped rather than mapped-and-failing: every one of these endpoints reads or writes
        // the job tables, so offering them would be offering a control that cannot work.
        Log.Warning(
            "AutomationAgent:Database:ConnectionString is not set. The agent is running without a "
            + "database: the AutoShop cycle timer, /api/automation/jobs and /api/automation/config "
            + "are disabled. ERP sign-in and /api/automation/indent-to-po are unaffected.");
    }

    Log.Information(
        "Automation agent starting on port {Port} (loopbackOnly={LoopbackOnly}, database={DatabaseState})",
        hostOptions.ManagementApiPort,
        hostOptions.BindToLoopbackOnly,
        !databaseConfigured ? "disabled"
            : database.Usable ? "configured"
            : "configured but unreachable");

    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Automation agent terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// Exposed so the integration tests can host the agent through <c>WebApplicationFactory</c>.
/// </summary>
public partial class Program;
