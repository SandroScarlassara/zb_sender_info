namespace zb_sender_info;

using System.Text.Json;
using System.Text.Json.Serialization;

public class Worker : BackgroundService
{
    private static double ToDouble(object? raw)
    {
        var value = raw switch
        {
            double d  => d,
            float f   => f,
            int i     => i,
            long l    => l,
            string s  => double.TryParse(s, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                            ? parsed : 0d,
            null      => 0d,
            _         => Convert.ToDouble(raw)
        };
        return double.IsFinite(value) ? value : 0d;
    }
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private readonly string DtbCounterJson      = @"C:\Program Files\Zabbix Agent\conf\database_counter.json";
    private readonly string DtbCounterTxt       = @"C:\Program Files\Zabbix Agent\conf\database_counter.txt";
    private readonly string outputDtbJson       = @"C:\Program Files\Zabbix Agent\conf\Database_online.json";
    private readonly string instanceJson        = @"C:\Program Files\Zabbix Agent\conf\instance.json";
    private readonly string InstanceCounterJson = @"C:\Program Files\Zabbix Agent\conf\instance_counter.json";
    private readonly string InstanceCounterTxt  = @"C:\Program Files\Zabbix Agent\conf\instance_counter.txt";
    private readonly string outputInstanceJson  = @"C:\Program Files\Zabbix Agent\conf\instance_online.json";

    // que não é registrado no container de DI (instanciado manualmente)
    public Worker(ILogger<Worker> logger, ILoggerFactory loggerFactory)
    {
        _logger        = logger;
        _loggerFactory = loggerFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Leitura única dos arquivos de configuração (não mudam em runtime) ─────────

        var counters = File.ReadAllLines(InstanceCounterTxt)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        var instanceConfig = JsonSerializer.Deserialize<InstanceRoot>(
            File.ReadAllText(instanceJson))!;

        var counterConfig = JsonSerializer.Deserialize<CounterRoot>(
            File.ReadAllText(InstanceCounterJson))!;

        var dbCounters = File.ReadAllLines(DtbCounterTxt)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        var dbCounterConfig = JsonSerializer.Deserialize<DbCounterRoot>(
            File.ReadAllText(DtbCounterJson))!;

        var insCountersPorInstancia = instanceConfig.Instances
            .ToDictionary(
                inst => inst.Service,
                inst => counterConfig.InstanceCounter
                    .Where(c => c.Language == inst.Language)
                    .ToList()
            );

        var dbCountersPorInstancia = instanceConfig.Instances
            .ToDictionary(
                inst => inst.Service,
                inst => dbCounterConfig.DatabaseCounter
                    .Where(c => c.Language == inst.Language)
                    .ToList()
            );
        
        var pdhLogger = _loggerFactory.CreateLogger<PdhMultiCounterReader>();
        using var reader   = new PdhMultiCounterReader(counters,   pdhLogger);
        using var dbReader = new PdhMultiCounterReader(dbCounters, pdhLogger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ── Contadores de instância ───────────────────────────────────────────────

                var counterValues = await reader.GetValuesAsync();
                var instancias    = new List<Dictionary<string, object>>();

                foreach (var instance in instanceConfig.Instances)
                {
                    var metrics = new Dictionary<string, object>();

                    // Filtro pré-calculado — sem LINQ no loop de ciclo
                    foreach (var counter in insCountersPorInstancia[instance.Service])
                    {
                        var resolvedKey = counter.Type == "process"
                            ? counter.Key.Replace("#SQLINSTANCE", instance.Process, StringComparison.OrdinalIgnoreCase)
                            : counter.Key.Replace("#SQLINSTANCE", instance.Service, StringComparison.OrdinalIgnoreCase);

                        if (counterValues.TryGetValue(resolvedKey, out var rawValue))
                            metrics.TryAdd(counter.Items, ToDouble(rawValue));
                    }

                    instancias.Add(new Dictionary<string, object>
                    {
                        [instance.Service] = metrics
                    });
                }

                var json = JsonSerializer.Serialize(new { instancias }, new JsonSerializerOptions
                {
                    WriteIndented    = true,
                    NumberHandling   = JsonNumberHandling.AllowNamedFloatingPointLiterals
                });

                await File.WriteAllTextAsync(outputInstanceJson, json, stoppingToken);
                _logger.LogInformation("instance_online.json atualizado com sucesso");

                // ── Contadores de database ────────────────────────────────────────────────

                var dbCounterValues = await dbReader.GetValuesAsync();
                var dbInstancias    = new Dictionary<string, List<Dictionary<string, object>>>();

                foreach (var instance in instanceConfig.Instances)
                {
                    // Filtro pré-calculado — sem LINQ no loop de ciclo
                    var dbCountersFiltrados = dbCountersPorInstancia[instance.Service];

                    var dbNames = dbCounterValues.Keys
                        .Where(k => k.StartsWith($@"\{instance.Service}:Databases(", StringComparison.OrdinalIgnoreCase))
                        .Select(k =>
                        {
                            var start = k.IndexOf('(') + 1;
                            var end   = k.IndexOf(')');
                            return k.Substring(start, end - start);
                        })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var dbList = new List<Dictionary<string, object>>();

                    foreach (var dbName in dbNames)
                    {
                        var dbMetrics = new Dictionary<string, object>();

                        foreach (var counter in dbCountersFiltrados)
                        {
                            var resolvedKey = counter.Key
                                .Replace("SQLINSTANCE", instance.Service, StringComparison.OrdinalIgnoreCase)
                                .Replace("DBNAME",      dbName,           StringComparison.OrdinalIgnoreCase);

                            if (dbCounterValues.TryGetValue(resolvedKey, out var rawValue))
                                dbMetrics.TryAdd(counter.Items, ToDouble(rawValue));
                        }

                        dbList.Add(new Dictionary<string, object>
                        {
                            ["bd"]    = dbName,
                            ["items"] = dbMetrics
                        });
                    }

                    dbInstancias[instance.Service] = dbList;
                }

                var dbJson = JsonSerializer.Serialize(new { databases = new[] { dbInstancias } }, new JsonSerializerOptions
                {
                    WriteIndented  = false,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
                });

                await File.WriteAllTextAsync(outputDtbJson, dbJson, stoppingToken);
                _logger.LogInformation("database_online.json atualizado com sucesso");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no processamento");
            }

            await Task.Delay(60000, stoppingToken);
        }
    }
}

// ─── Models ───────────────────────────────────────────────────────────────────

public class InstanceRoot
{
    [JsonPropertyName("Instance")]
    public List<InstanceJson> Instances { get; set; } = new();
}

public class InstanceJson
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("service")]
    public string Service { get; set; } = "";

    [JsonPropertyName("process")]
    public string Process { get; set; } = "";

    [JsonPropertyName("lng")]
    public string Language { get; set; } = "";
}

public class CounterRoot
{
    [JsonPropertyName("Instance_Counter")]
    public List<InstanceCounterJson> InstanceCounter { get; set; } = new();
}

public class InstanceCounterJson
{
    [JsonPropertyName("lng")]
    public string Language { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("items")]
    public string Items { get; set; } = "";
}

// ─── Models de Database ───────────────────────────────────────────────────────

public class DbCounterRoot
{
    [JsonPropertyName("Database_Counter")]
    public List<DbCounterJson> DatabaseCounter { get; set; } = new();
}

public class DbCounterJson
{
    [JsonPropertyName("lng")]
    public string Language { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("items")]
    public string Items { get; set; } = "";
}