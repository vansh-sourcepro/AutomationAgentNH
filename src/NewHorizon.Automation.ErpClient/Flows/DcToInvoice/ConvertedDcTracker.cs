using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NewHorizon.Automation.ErpClient.Flows.DcToInvoice;

public sealed record ConvertedDcRecord(
    long DcId,
    string DcNo,
    string CustomerCode,
    string InvoiceNumber,
    long InvoiceId,
    int ItemCount,
    decimal TotalQuantity,
    decimal TotalAmount,
    string? RateStructureCode,
    DateTime ConvertedAtUtc,
    string Status = "SUCCESS",
    string? CustomerName = null,
    string TriggerSource = "Api",
    string? Message = null,
    string? FailureReason = null);

public interface IConvertedDcTracker
{
    bool IsDcConverted(long? dcId, string? dcNo, out ConvertedDcRecord? record);
    void RecordConversion(ConvertedDcRecord record);
    void RecordHistory(ConvertedDcRecord record);
    IReadOnlyList<ConvertedDcRecord> GetAllConverted();
    IReadOnlyList<ConvertedDcRecord> GetAllHistory();
    void Clear();
}

public sealed class InMemoryConvertedDcTracker : IConvertedDcTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ConvertedDcRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConvertedDcRecord> _history = new();

    public bool IsDcConverted(long? dcId, string? dcNo, out ConvertedDcRecord? record)
    {
        lock (_lock)
        {
            if (dcId.HasValue && dcId.Value > 0)
            {
                if (_records.TryGetValue($"ID:{dcId.Value}", out record)) return true;
            }

            if (!string.IsNullOrWhiteSpace(dcNo))
            {
                var clean = dcNo.Trim();
                if (_records.TryGetValue($"NO:{clean}", out record)) return true;

                var unpadded = clean.TrimStart('0');
                if (!string.IsNullOrEmpty(unpadded))
                {
                    var match = _records.Values.FirstOrDefault(r =>
                        string.Equals(r.DcNo.TrimStart('0'), unpadded, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        record = match;
                        return true;
                    }
                }
            }

            record = null;
            return false;
        }
    }

    public void RecordConversion(ConvertedDcRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            if (record.DcId > 0) _records[$"ID:{record.DcId}"] = record;
            if (!string.IsNullOrWhiteSpace(record.DcNo)) _records[$"NO:{record.DcNo.Trim()}"] = record;
            _history.Add(record);
        }
    }

    public void RecordHistory(ConvertedDcRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            _history.Add(record);
        }
    }

    public IReadOnlyList<ConvertedDcRecord> GetAllConverted()
    {
        lock (_lock)
        {
            return _records.Values.DistinctBy(r => r.DcId > 0 ? r.DcId.ToString() : r.DcNo).ToList();
        }
    }

    public IReadOnlyList<ConvertedDcRecord> GetAllHistory()
    {
        lock (_lock)
        {
            if (_history.Count > 0)
            {
                return _history.ToList();
            }

            return GetAllConverted();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _records.Clear();
            _history.Clear();
        }
    }
}

public sealed class ConvertedDcTracker : IConvertedDcTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly Dictionary<string, ConvertedDcRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConvertedDcRecord> _history = new();

    public ConvertedDcTracker(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _filePath = customPath;
        }
        else
        {
            var runDir = Path.Combine(Directory.GetCurrentDirectory(), "run");
            if (Directory.Exists(runDir))
            {
                _filePath = Path.Combine(runDir, "ConvertedDeliveryChallans.json");
            }
            else
            {
                _filePath = Path.Combine(AppContext.BaseDirectory, "ConvertedDeliveryChallans.json");
            }
        }

        Load();
    }

    public bool IsDcConverted(long? dcId, string? dcNo, out ConvertedDcRecord? record)
    {
        lock (_lock)
        {
            if (dcId.HasValue && dcId.Value > 0)
            {
                var idKey = $"ID:{dcId.Value}";
                if (_records.TryGetValue(idKey, out record))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(dcNo))
            {
                var cleanNo = dcNo.Trim();
                var noKey = $"NO:{cleanNo}";
                if (_records.TryGetValue(noKey, out record))
                {
                    return true;
                }

                var unpadded = cleanNo.TrimStart('0');
                if (!string.IsNullOrEmpty(unpadded))
                {
                    var match = _records.Values.FirstOrDefault(r =>
                        string.Equals(r.DcNo.TrimStart('0'), unpadded, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        record = match;
                        return true;
                    }
                }
            }

            record = null;
            return false;
        }
    }

    public void RecordConversion(ConvertedDcRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            if (record.DcId > 0)
            {
                _records[$"ID:{record.DcId}"] = record;
            }
            if (!string.IsNullOrWhiteSpace(record.DcNo))
            {
                _records[$"NO:{record.DcNo.Trim()}"] = record;
            }

            _history.Add(record);
            Save();
        }
    }

    public void RecordHistory(ConvertedDcRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            _history.Add(record);
            Save();
        }
    }

    public IReadOnlyList<ConvertedDcRecord> GetAllConverted()
    {
        lock (_lock)
        {
            return _records.Values.DistinctBy(r => r.DcId > 0 ? r.DcId.ToString() : r.DcNo).ToList();
        }
    }

    public IReadOnlyList<ConvertedDcRecord> GetAllHistory()
    {
        lock (_lock)
        {
            if (_history.Count > 0)
            {
                return _history.ToList();
            }

            return GetAllConverted();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _records.Clear();
            _history.Clear();
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var items = JsonSerializer.Deserialize<List<ConvertedDcRecord>>(json, JsonOptions);
                if (items != null)
                {
                    _history.Clear();
                    _records.Clear();
                    foreach (var itm in items)
                    {
                        _history.Add(itm);
                        if (itm.DcId > 0) _records[$"ID:{itm.DcId}"] = itm;
                        if (!string.IsNullOrWhiteSpace(itm.DcNo)) _records[$"NO:{itm.DcNo.Trim()}"] = itm;
                    }
                }
            }
        }
        catch
        {
            // Ignore load errors and proceed
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var toSave = _history.Count > 0
                ? _history
                : _records.Values.DistinctBy(r => r.DcId > 0 ? r.DcId.ToString() : r.DcNo).ToList();
            var json = JsonSerializer.Serialize(toSave, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Ignore persistence write failures
        }
    }
}
