using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NewHorizon.Automation.ErpClient.Authentication;

/// <summary>
/// Signs in to the ERP once the agent has finished starting, so the token is already cached before
/// the first cycle needs it and a wrong password shows up in the log at startup rather than in the
/// middle of a job.
/// </summary>
/// <remarks>
/// A <see cref="BackgroundService"/> rather than work done during startup: an ERP that is slow or
/// still booting must not hold up the Windows Service. If this attempt fails the agent carries on —
/// <see cref="ErpTokenProvider"/> signs in on demand, so the next ERP call simply tries again.
/// </remarks>
public sealed class ErpLoginStartupService : BackgroundService
{
    private readonly IErpTokenProvider _tokenProvider;
    private readonly ILogger<ErpLoginStartupService> _logger;

    public ErpLoginStartupService(IErpTokenProvider tokenProvider, ILogger<ErpLoginStartupService> logger)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The provider logs the successful sign-in and its expiry; nothing to add here.
            await _tokenProvider.GetTokenAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down before the ERP answered. Not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not sign in to the ERP at startup; the agent will sign in when the first ERP call is made");
        }
    }
}
