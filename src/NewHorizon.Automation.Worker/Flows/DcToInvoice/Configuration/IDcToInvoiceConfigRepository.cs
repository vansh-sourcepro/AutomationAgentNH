using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NewHorizon.Automation.Worker.Flows.DcToInvoice.Configuration;

public interface IDcToInvoiceConfigRepository
{
    Task<DcToInvoiceAutomationConfig> GetConfigAsync(CancellationToken cancellationToken = default);
    Task SaveConfigAsync(DcToInvoiceAutomationConfig config, CancellationToken cancellationToken = default);
}

public class JsonDcToInvoiceConfigRepository : IDcToInvoiceConfigRepository
{
    private readonly string _filePath;

    public JsonDcToInvoiceConfigRepository()
    {
        _filePath = Path.Combine(AppContext.BaseDirectory, "DcToInvoiceAutomationConfig.json");
    }

    public async Task<DcToInvoiceAutomationConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return new DcToInvoiceAutomationConfig();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            return JsonSerializer.Deserialize<DcToInvoiceAutomationConfig>(json) ?? new DcToInvoiceAutomationConfig();
        }
        catch
        {
            return new DcToInvoiceAutomationConfig();
        }
    }

    public async Task SaveConfigAsync(DcToInvoiceAutomationConfig config, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }
}
