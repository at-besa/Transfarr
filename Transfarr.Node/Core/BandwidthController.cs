using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Transfarr.Node.Core;

public class BandwidthController
{
    private readonly ILogger<BandwidthController> logger;

    // Token Bucket state
    private long uploadTokens;
    private long downloadTokens;
    private long lastUploadRefillTicks;
    private long lastDownloadRefillTicks;

    // Limits in Bytes per second
    private long uploadLimitBps;
    private long downloadLimitBps;

    // Speed Tracking (Rolling counters)
    private long uploadBytesSinceLastReport;
    private long downloadBytesSinceLastReport;
    private long lastReportTicks;
    
    // Calculated speeds for UI
    public double InstantaneousUploadMBps { get; private set; }
    public double InstantaneousDownloadMBps { get; private set; }

    private readonly object uploadLock = new();
    private readonly object downloadLock = new();

    public BandwidthController(ILogger<BandwidthController> logger)
    {
        this.logger = logger;
        long currentTicks = Stopwatch.GetTimestamp();
        lastUploadRefillTicks = currentTicks;
        lastDownloadRefillTicks = currentTicks;
        lastReportTicks = currentTicks;
    }

    public void SetUploadLimitMBps(int mbps)
    {
        lock (uploadLock)
        {
            uploadLimitBps = mbps * 1024L * 1024L;
            uploadTokens = uploadLimitBps; // Reset tokens to full capacity
            lastUploadRefillTicks = Stopwatch.GetTimestamp();
            logger.LogInformation("Upload limit set to {Limit} MB/s", mbps);
        }
    }

    public void SetDownloadLimitMBps(int mbps)
    {
        lock (downloadLock)
        {
            downloadLimitBps = mbps * 1024L * 1024L;
            downloadTokens = downloadLimitBps; // Reset tokens to full capacity
            lastDownloadRefillTicks = Stopwatch.GetTimestamp();
            logger.LogInformation("Download limit set to {Limit} MB/s", mbps);
        }
    }

    public async Task ConsumeUploadAsync(int bytes, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref uploadBytesSinceLastReport, bytes);
        if (uploadLimitBps <= 0) return; // Unlimited

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay = TimeSpan.Zero;
            
            lock (uploadLock)
            {
                RefillTokens(ref uploadTokens, ref lastUploadRefillTicks, uploadLimitBps);

                if (uploadTokens >= bytes || uploadTokens == uploadLimitBps)
                {
                    // If we have enough tokens, or the bucket is fully saturated (to avoid permanent blocking if bytes > max bucket size)
                    long tokensToConsume = Math.Min(bytes, uploadTokens);
                    uploadTokens -= tokensToConsume;
                    return;
                }
                
                // Calculate delay required to generate sufficient tokens
                long tokensNeeded = bytes - uploadTokens;
                double secondsToWait = (double)tokensNeeded / uploadLimitBps;
                delay = TimeSpan.FromSeconds(secondsToWait);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public async Task ConsumeDownloadAsync(int bytes, CancellationToken cancellationToken = default)
    {
        Interlocked.Add(ref downloadBytesSinceLastReport, bytes);
        if (downloadLimitBps <= 0) return; // Unlimited

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay = TimeSpan.Zero;
            
            lock (downloadLock)
            {
                RefillTokens(ref downloadTokens, ref lastDownloadRefillTicks, downloadLimitBps);

                if (downloadTokens >= bytes || downloadTokens == downloadLimitBps)
                {
                    long tokensToConsume = Math.Min(bytes, downloadTokens);
                    downloadTokens -= tokensToConsume;
                    return;
                }
                
                long tokensNeeded = bytes - downloadTokens;
                double secondsToWait = (double)tokensNeeded / downloadLimitBps;
                delay = TimeSpan.FromSeconds(secondsToWait);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private void RefillTokens(ref long tokens, ref long lastRefillTicks, long limitBps)
    {
        long currentTicks = Stopwatch.GetTimestamp();
        double elapsedSeconds = (double)(currentTicks - lastRefillTicks) / Stopwatch.Frequency;
        
        if (elapsedSeconds > 0)
        {
            long newTokens = (long)(elapsedSeconds * limitBps);
            if (newTokens > 0)
            {
                tokens = Math.Min(limitBps, tokens + newTokens); // Max capacity is 1 second worth of tokens
                lastRefillTicks = currentTicks;
            }
        }
    }

    /// <summary>
    /// Calculates the speed over the last tick window and resets the counters.
    /// Should be called exactly once per second by a background timer.
    /// </summary>
    public void TickReport()
    {
        long currentTicks = Stopwatch.GetTimestamp();
        double elapsedSeconds = (double)(currentTicks - lastReportTicks) / Stopwatch.Frequency;
        lastReportTicks = currentTicks;

        long upBytes = Interlocked.Exchange(ref uploadBytesSinceLastReport, 0);
        long downBytes = Interlocked.Exchange(ref downloadBytesSinceLastReport, 0);

        if (elapsedSeconds > 0)
        {
            InstantaneousUploadMBps = (upBytes / elapsedSeconds) / (1024.0 * 1024.0);
            InstantaneousDownloadMBps = (downBytes / elapsedSeconds) / (1024.0 * 1024.0);
        }
        else
        {
            InstantaneousUploadMBps = 0;
            InstantaneousDownloadMBps = 0;
        }
    }
}
