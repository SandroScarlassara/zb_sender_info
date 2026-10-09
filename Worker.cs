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

    private async Task EnviarZabbixAsync(string exe, string ipServer, string arquivo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exe) || string.IsNullOrWhiteSpace(ipServer))
        {
            _logger.LogWarning("ZabbixSenderExe ou ZabbixIpServer não configurado no Server.json");
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = exe,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            // ArgumentList cuida das aspas e espaços nos caminhos
            psi.ArgumentList.Add("-z");
            psi.ArgumentList.Add(ipServer);
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(arquivo);

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Não foi possível iniciar o zabbix_sender");

            // Timeout de 30s para o sender nunca travar o ciclo
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                _logger.LogWarning("zabbix_sender excedeu 30s e foi encerrado. Arquivo: {Arquivo}", arquivo);
                return;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode == 0)
                _logger.LogInformation("zabbix_sender OK ({Arquivo}): {Saida}", Path.GetFileName(arquivo), stdout.Trim());
            else
                _logger.LogWarning("zabbix_sender código {Codigo} ({Arquivo}). Saída: {Saida} Erro: {Erro}",
                    proc.ExitCode, Path.GetFileName(arquivo), stdout.Trim(), stderr.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // serviço sendo parado, ignora
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao executar o zabbix_sender");
        }
    }


    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;

    static readonly string baseDir           = @"C:\Program Files\Zabbix Agent\conf";
    private readonly string DtbCounterJson      = baseDir + @"\database_counter.json";
    private readonly string DtbCounterTxt       = baseDir + @"\database_counter.txt";
    private readonly string outputDtbJson       = baseDir + @"\Database_online.json";
    private readonly string instanceJson        = baseDir + @"\instance.json";
    private readonly string InstanceCounterJson = baseDir + @"\instance_counter.json";
    private readonly string InstanceCounterTxt  = baseDir + @"\instance_counter.txt";
    private readonly string outputInstanceJson  = baseDir + @"\instance_online.json";
    private readonly string ServerJson          = baseDir + @"\Server.json";

    // que não é registrado no container de DI (instanciado manualmente)
    public Worker(ILogger<Worker> logger, ILoggerFactory loggerFactory)
    {   _logger        = logger;
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

        var server = JsonSerializer.Deserialize<ServerRoot>(
            File.ReadAllText(ServerJson))!;

        var serverName = server.Servers.FirstOrDefault()?.ServerName ?? "UnknownServer";

        var timeUpdate = server.Servers.FirstOrDefault()?.TimeUpdate ?? 0;
        var zabbixSenderExe = server.Servers.FirstOrDefault()?.ZabbixSenderExe ?? "";
        var zabbixIpServer  = server.Servers.FirstOrDefault()?.ZabbixIpServer ?? "";

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
                    WriteIndented    = false,
                    NumberHandling   = JsonNumberHandling.AllowNamedFloatingPointLiterals
                });
                var instanceRaw = $"\"{serverName}\" consulta.instances.raw ";
                
                await File.WriteAllTextAsync(outputInstanceJson, instanceRaw + json, stoppingToken);
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
                                .Replace("#SQLINSTANCE", instance.Service, StringComparison.OrdinalIgnoreCase)
                                .Replace("#DBNAME",      dbName,           StringComparison.OrdinalIgnoreCase);

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

                var databasesRaw = $"\"{serverName}\" consulta.databases.raw ";

                await File.WriteAllTextAsync(outputDtbJson, databasesRaw + dbJson, stoppingToken);
                _logger.LogInformation("database_online.json atualizado com sucesso");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no processamento");
            }

            await EnviarZabbixAsync(zabbixSenderExe, zabbixIpServer, outputInstanceJson, stoppingToken);
            await EnviarZabbixAsync(zabbixSenderExe, zabbixIpServer, outputDtbJson, stoppingToken);

            await Task.Delay(timeUpdate*1000, stoppingToken);
        }
    }
}
// ─── Models ───────────────────────────────────────────────────────────────────

public class ServerRoot
{
    [JsonPropertyName("Server")]
    public List<ServerJson> Servers { get; set; } = new();
}
public class ServerJson
{
    [JsonPropertyName("ServerName")]
    public string ServerName { get; set; } = "";

    [JsonPropertyName("TimeUpdate")]
    public int   TimeUpdate { get; set; } = 0;

    [JsonPropertyName("ZabbixSenderExe")]
    public string ZabbixSenderExe { get; set; } = "";

    [JsonPropertyName("ZabbixIpServer")]
    public string ZabbixIpServer { get; set; } = "";
}
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