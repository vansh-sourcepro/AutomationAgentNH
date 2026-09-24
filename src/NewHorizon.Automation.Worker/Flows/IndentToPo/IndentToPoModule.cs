using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Endpoints;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Services;
using Serilog;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo;

/// <summary>
/// The Indent → PO flow's share of the host: its options, its scheduler, the orchestrator that
/// composes tracking with the conversion, and its HTTP endpoints.
/// </summary>
/// <remarks>
/// <c>Program.cs</c> calls each method once, at the point where the same lines used to be. A new
/// flow gets a module of its own beside this one and its own calls in <c>Program.cs</c>.
/// </remarks>
public static class IndentToPoModule
{
    /// <summary>
    /// Session values an unattended agent has no session to read: company, site, financial year
    /// and buyer. The user id is not among them — that comes from the ERP login response.
    /// Called by <see cref="Configuration.OptionsRegistration.AddAutomationAgentOptions"/>.
    /// </summary>
    public static IServiceCollection AddIndentToPoOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IndentPoOptions>()
            .Bind(configuration.GetSection($"{AutomationAgentOptions.SectionName}:PurchaseOrder"))
            // FinancialYear is deliberately not required: blank means "the year the purchase order
            // date falls in", which is both what the ERP screen does and the setting that survives
            // an April. A value pins it.
            .Validate(
                options => string.IsNullOrWhiteSpace(options.FinancialYear) || IsFinancialYear(options.FinancialYear),
                "AutomationAgent:PurchaseOrder:FinancialYear must be the ERP's \"yy-yy\" form, e.g. \"26-27\", "
                + "or blank to follow the purchase order date.")
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Indent → PO automation: the daily scheduler that self-calls
    /// <c>POST /api/automation/indent-to-po/convert?trigger=timer</c> for each config whose slot is
    /// due. Needs the database (config rows + tracked runs), so it is registered under the same
    /// usable-database gate as the AutoShop timers. Registered exactly once.
    /// </summary>
    /// <remarks>
    /// It is off by default — all three IndentPoAutomationConfig rows ship inactive with no Schedule
    /// Time — so a fresh install still converts nothing until a person turns PO Automation on and
    /// gives a type a time from the PO Automation screen.
    /// </remarks>
    public static IServiceCollection AddIndentToPoScheduler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(PoAutomationSchedulerService.HttpClientName);
        services.AddHostedService<PoAutomationSchedulerService>();

        return services;
    }

    /// <summary>
    /// The composition of tracking and conversion: tracking lives in Application and knows nothing
    /// about purchase orders, the conversion lives in ErpClient and knows nothing about runs. Only
    /// the host sees both, so this is where they are put together.
    /// </summary>
    public static IServiceCollection AddIndentToPoOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IProcessJobOrchestrator, ProcessJobOrchestrator>();

        return services;
    }

    /// <summary>Maps the flow's endpoints under the same gates the host has always applied.</summary>
    /// <param name="databaseConfigured">A connection string is set.</param>
    /// <param name="databaseUsable">The startup probe could open that database.</param>
    /// <param name="jwtConfigured">The inbound ERP bearer-token scheme is registered.</param>
    public static WebApplication MapIndentToPoEndpoints(
        this WebApplication app,
        bool databaseConfigured,
        bool databaseUsable,
        bool jwtConfigured)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPurchaseOrderEndpoints();

        // Mapped whether or not a database is configured. Without one the handlers answer 503 and
        // say why, which is more use than a 404 that leaves the caller wondering if they have the
        // path wrong — and the conversion endpoints above keep working regardless.
        app.MapProcessJobEndpoints();

        // The PO Automation screen's API needs both the database (config rows, tracked runs) and a
        // usable one at that — its "Run now" starts a tracked conversion. Mapped only when the JWT
        // scheme is configured too, since the whole group requires a bearer token.
        if (databaseConfigured && databaseUsable && jwtConfigured)
        {
            app.MapPoAutomationEndpoints();
        }
        else if (databaseConfigured && databaseUsable)
        {
            Log.Warning(
                "AutomationAgent:InboundJwt:SigningKey is not set, so /api/automation/po-automation "
                + "is not mapped and the ERP frontend cannot manage automation. Set it to the ERP's "
                + "JWT signing secret to enable the screen.");
        }

        return app;
    }

    /// <summary>The ERP's "yy-yy" document year, e.g. "26-27".</summary>
    private static bool IsFinancialYear(string value)
    {
        var year = value.Trim();

        return year.Length == 5
            && year[2] == '-'
            && char.IsAsciiDigit(year[0]) && char.IsAsciiDigit(year[1])
            && char.IsAsciiDigit(year[3]) && char.IsAsciiDigit(year[4]);
    }
}
