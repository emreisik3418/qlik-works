using System.CommandLine;
using System.Text;
using Microsoft.Extensions.Configuration;
using QlikSheetCleaner.Configuration;
using QlikSheetCleaner.Excel;
using QlikSheetCleaner.Processing;
using QlikSheetCleaner.Processing.Deletion;
using QlikSheetCleaner.Qlik;
using Serilog;
using Serilog.Events;

namespace QlikSheetCleaner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var inputOption = new Option<FileInfo>("--input", "-i")
        {
            Description = "AppId + SheetId listesini içeren Excel dosyası (.xlsx)",
            Required = true
        };
        var outputOption = new Option<FileInfo?>("--output", "-o")
        {
            Description = "Sonuç raporu (.xlsx). Verilmezse girdinin yanına zaman damgalı kopya yazılır."
        };
        var executeOption = new Option<bool>("--execute")
        {
            Description = "Gerçekten sil. Verilmezse DRY-RUN: sadece ön kontrol ve rapor."
        };
        var yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "'EVET' onayını atla (otomasyon için)."
        };
        var configOption = new Option<FileInfo?>("--config", "-c")
        {
            Description = "Alternatif appsettings.json yolu"
        };

        var root = new RootCommand("Qlik Sense (QSEoW) sheet temizleme aracı — Excel'deki AppId + SheetId çiftlerini siler.")
        {
            inputOption, outputOption, executeOption, yesOption, configOption
        };

        root.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(inputOption)!;
            var output = parseResult.GetValue(outputOption);
            var execute = parseResult.GetValue(executeOption);
            var yes = parseResult.GetValue(yesOption);
            var config = parseResult.GetValue(configOption);
            return await RunAsync(input, output, execute, yes, config, ct);
        });

        return await root.Parse(args).InvokeAsync();
    }

    private static async Task<int> RunAsync(FileInfo input, FileInfo? output, bool execute, bool yes, FileInfo? configFile, CancellationToken ct)
    {
        AppSettings settings;
        try
        {
            settings = LoadSettings(configFile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Yapılandırma hatası: {ex.Message}");
            return (int)RunOutcome.Fatal;
        }

        ConfigureLogging(settings.Logging, execute);

        // Ctrl+C → nazik iptal (System.CommandLine'ın token'ı ile birleştir)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Console.CancelKeyPress += (_, e) =>
        {
            if (cts.IsCancellationRequested) return; // ikinci Ctrl+C → süreci sonlandır
            e.Cancel = true;
            Log.Warning("İptal istendi; devam eden çağrılar tamamlanınca durulacak (zorla çıkmak için tekrar Ctrl+C).");
            cts.Cancel();
        };

        try
        {
            if (!input.Exists)
            {
                Log.Error("Girdi dosyası bulunamadı: {Input}", input.FullName);
                return (int)RunOutcome.Fatal;
            }

            var outputPath = output?.FullName ?? ReportWriter.DefaultOutputPath(input.FullName, execute);

            Log.Information("QlikSheetCleaner başladı. Mod={Mode}, Girdi={Input}, Çıktı={Output}, Sunucu={Host}, Kullanıcı={Dir}\\{User}",
                execute ? "EXECUTE" : "DRY-RUN", input.FullName, outputPath, settings.Qlik.Host,
                settings.Qlik.UserDirectory, settings.Qlik.UserId);

            using var certificates = new CertificateProvider(settings.Qlik);
            using var qrs = new QrsClient(settings.Qlik, certificates);
            var engine = new EngineClient(settings.Qlik, certificates);
            var classifier = new SheetClassifier(settings.Processing.AllowCommunitySheetDelete);
            var backup = new BackupService(qrs, settings.Qlik, settings.Processing);

            var deleter = new SheetDeleter(
            [
                new EngineSheetDeletionStrategy(engine, backup, settings.Qlik),
                new QrsSheetDeletionStrategy(qrs, engine, backup, settings.Qlik.BackupOpenWithoutData)
            ]);

            var runner = new CleanupRunner(
                settings,
                new ExcelReader(settings.Excel),
                new PreflightService(qrs, classifier, settings.Processing),
                deleter,
                new ReportWriter(settings.Excel));

            var outcome = await runner.RunAsync(new RunOptions(input.FullName, outputPath, execute, yes), cts.Token);
            Log.Information("Bitti: {Outcome}", outcome);
            return (int)outcome;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Beklenmeyen hata");
            return (int)RunOutcome.Fatal;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static AppSettings LoadSettings(FileInfo? configFile)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory);

        if (configFile is not null)
            builder.AddJsonFile(configFile.FullName, optional: false, reloadOnChange: false);
        else
            builder.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        // Gizli bilgiler için ortam değişkenleri: QSC_Qlik__ClientCertificatePassword=...
        builder.AddEnvironmentVariables("QSC_");

        var settings = builder.Build().Get<AppSettings>() ?? new AppSettings();
        settings.Validate();
        return settings;
    }

    private static void ConfigureLogging(LoggingSettings logging, bool execute)
    {
        var level = Enum.TryParse<LogEventLevel>(logging.MinimumLevel, ignoreCase: true, out var l) ? l : LogEventLevel.Information;
        var folder = Path.GetFullPath(logging.LogFolder);
        Directory.CreateDirectory(folder);
        var prefix = execute ? "execute" : "dryrun";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(folder, $"qlik-sheet-cleaner-{prefix}-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 60,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {AppId} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
