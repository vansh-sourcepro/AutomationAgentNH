using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Configuration;

public interface ISoAllocationToDcConfigRepository
{
    Task<SoAllocationToDcAutomationConfig> GetConfigAsync(CancellationToken cancellationToken = default);
    Task SaveConfigAsync(SoAllocationToDcAutomationConfig config, CancellationToken cancellationToken = default);
}

public class JsonSoAllocationToDcConfigRepository : ISoAllocationToDcConfigRepository
{
    private readonly string _filePath;

    public JsonSoAllocationToDcConfigRepository()
    {
        _filePath = Path.Combine(AppContext.BaseDirectory, "SoAllocationToDcAutomationConfig.json");
    }

    public async Task<SoAllocationToDcAutomationConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return new SoAllocationToDcAutomationConfig();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            return JsonSerializer.Deserialize<SoAllocationToDcAutomationConfig>(json) ?? new SoAllocationToDcAutomationConfig();
        }
        catch
        {
            return new SoAllocationToDcAutomationConfig();
        }
    }

    public async Task SaveConfigAsync(SoAllocationToDcAutomationConfig config, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json, cancellationToken);
    }
}
