using zb_sender_info;
using System.Runtime.InteropServices;

var builder = Host.CreateDefaultBuilder(args);

builder.UseWindowsService(static options =>
{
    // Nome do serviço exibido no Event Viewer em:
    // Windows Logs > Application > Source = "ZabbixSenderInfo"
    options.ServiceName = "ZabbixSenderInfo";
});

builder.ConfigureLogging(static logging =>
{
    // Remove providers padrão (console, debug) — desnecessários em serviço Windows
    logging.ClearProviders();

    // Direciona todos os logs para o Event Viewer do Windows
    logging.AddEventLog(static settings =>
    {
        // Source: identifica a origem nas colunas do Event Viewer
        // Aparece em: Windows Logs > Application > coluna "Source"
        settings.SourceName = "ZabbixSenderInfo";

        // LogName: qual log do Event Viewer receberá as entradas
        // "Application" é o padrão — troque por "ZabbixSenderInfo" para um log dedicado
        // (log dedicado requer criação prévia via PowerShell — veja comentário abaixo)
        settings.LogName = "Application";
    });

    // Nível mínimo global: Information e acima (Warning, Error, Critical)
    // Troque por LogLevel.Debug durante diagnósticos
    logging.SetMinimumLevel(LogLevel.Information);
});

builder.ConfigureServices(static services =>
{
    services.AddHostedService<Worker>();
});

await builder.Build().RunAsync();

public class PdhMultiCounterReader : IDisposable
{
    private IntPtr _queryHandle = IntPtr.Zero;
    private readonly Dictionary<string, IntPtr> _counters = new();
    private readonly ILogger<PdhMultiCounterReader> _logger;

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint ERROR_SUCCESS = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhOpenQueryA(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhAddCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCloseQuery(IntPtr query);

    // ILogger injetado via construtor — compatível com o sistema de DI do Host
    public PdhMultiCounterReader(IEnumerable<string> counterPaths, ILogger<PdhMultiCounterReader> logger)
    {
        _logger = logger;

        uint status = PdhOpenQueryA(null, IntPtr.Zero, out _queryHandle);
        if (status != ERROR_SUCCESS)
            throw new InvalidOperationException($"PdhOpenQuery failed: 0x{status:X}");

        foreach (var path in counterPaths)
        {
            status = PdhAddCounter(_queryHandle, path, IntPtr.Zero, out IntPtr counterHandle);
            if (status != ERROR_SUCCESS)
            {
                // Warning: contador não foi registrado — será ignorado nas coletas
                // 0x{status:X} é o código PDH (ex: 0xC0000BB8 = contador não encontrado)
                _logger.LogWarning(
                    "PDH: falha ao adicionar contador (código 0x{Status:X}). Caminho: {Path}",
                    status, path);
            }
            else
            {
                _counters[path] = counterHandle;
            }
        }
    }

    public async Task<Dictionary<string, double>> GetValuesAsync(int warmups = 1000)
    {
        var results = new Dictionary<string, double>(_counters.Count);
        PdhCollectQueryData(_queryHandle);
        await Task.Delay(warmups);
        PdhCollectQueryData(_queryHandle);

        foreach (var kvp in _counters)
        {
            uint type;
            PDH_FMT_COUNTERVALUE value;
            uint status = PdhGetFormattedCounterValue(kvp.Value, PDH_FMT_DOUBLE, out type, out value);
            if (status == ERROR_SUCCESS)
            {
                results[kvp.Key] = value.doubleValue;
            }
            else
            {
                // Debug: acontece normalmente na 1ª coleta de contadores /sec (sem baseline ainda)
                _logger.LogDebug(
                    "PDH: falha ao ler valor do contador (código 0x{Status:X}). Caminho: {Path}",
                    status, kvp.Key);

                results[kvp.Key] = double.NaN;
            }
        }

        return results;
    }

    public void Dispose()
    {
        if (_queryHandle != IntPtr.Zero)
        {
            PdhCloseQuery(_queryHandle);
            _queryHandle = IntPtr.Zero;
        }
    }
}