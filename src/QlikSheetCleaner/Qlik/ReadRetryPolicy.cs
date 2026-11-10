using System.Net;
using System.Net.WebSockets;
using Polly;
using Polly.Retry;
using Serilog;

namespace QlikSheetCleaner.Qlik;

/// <summary>
/// Sadece OKUMA çağrıları için retry hattı. Silme, unpublish, DestroyObject ve DoSave bu hattan GEÇMEZ.
/// </summary>
public static class ReadRetryPolicy
{
    public static ResiliencePipeline Create(int retryCount, int baseDelaySeconds, ILogger log)
    {
        if (retryCount <= 0) return ResiliencePipeline.Empty;

        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = retryCount,
                Delay = TimeSpan.FromSeconds(Math.Max(1, baseDelaySeconds)),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome.Exception, args.Context.CancellationToken)),
                OnRetry = args =>
                {
                    log.Warning("Okuma çağrısı tekrar deneniyor ({Attempt}. deneme, {Delay:n1} sn sonra): {Error}",
                        args.AttemptNumber + 1, args.RetryDelay.TotalSeconds, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    private static bool IsTransient(Exception? ex, CancellationToken ct)
    {
        if (ex is null) return false;
        if (ct.IsCancellationRequested) return false; // kullanıcı iptali → retry yok

        return ex switch
        {
            QrsException q => IsTransientStatus(q.StatusCode),
            HttpRequestException h => h.StatusCode is null || IsTransientStatus(h.StatusCode.Value),
            TaskCanceledException or TimeoutException => true, // istek zaman aşımı
            WebSocketException or IOException => true,
            _ => false
        };
    }

    public static bool IsTransientStatus(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)code >= 500;
}
