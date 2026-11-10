using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace FabriFrogeLauncher;

public sealed class WebpBackgroundPlayer : IDisposable
{
    private CancellationTokenSource? _cts;

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    public void Dispose() => Stop();

    public Task StartAsync(
        string webpPath,
        Action<int, int, byte[]> onFrameDecoded,
        Action<string> log,
        CancellationToken externalCt)
    {
        Stop();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;

        if (!File.Exists(webpPath))
        {
            log($"WebP background not found: {webpPath}");
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                using var fs = File.OpenRead(webpPath);
                using var codec = SKCodec.Create(fs);

                if (codec == null)
                {
                    log("Skia: Failed to create codec for WebP.");
                    return;
                }

                var info = codec.Info;
                var frameInfos = codec.FrameInfo;

                if (frameInfos == null || frameInfos.Length == 0)
                {
                    log("Skia: No frame info found (cannot animate).");
                    return;
                }

                log($"Skia: WebP {info.Width}x{info.Height} frames={frameInfos.Length}");

                using var skBitmap = new SKBitmap(
                    info.Width,
                    info.Height,
                    SKColorType.Bgra8888,
                    SKAlphaType.Premul);

                var bufferSize = info.Width * info.Height * 4;
                var bytes = new byte[bufferSize];

                int idx = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long nextAt = 0;

                while (!ct.IsCancellationRequested)
                {
                    // Animated WebP frames are often partial rectangles. Ask Skia to
                    // reconstruct the required prior frame into the destination bitmap.
                    var options = new SKCodecOptions(idx, -1);
                    var result = codec.GetPixels(skBitmap.Info, skBitmap.GetPixels(), options);

                    if (result != SKCodecResult.Success &&
                        result != SKCodecResult.IncompleteInput)
                    {
                        log($"Skia: Frame decode failed at {idx} ({result})");
                        return;
                    }

                    // SAFE copy from Skia memory into managed array
                    Marshal.Copy(skBitmap.GetPixels(), bytes, 0, bufferSize);

                    onFrameDecoded(info.Width, info.Height, bytes);

                    int delayMs = frameInfos[idx].Duration;
                    if (delayMs <= 0) delayMs = 40;

                    idx++;
                    if (idx >= frameInfos.Length)
                        idx = 0;

                    nextAt += delayMs;

                    var remaining = nextAt - sw.ElapsedMilliseconds;
                    if (remaining > 0)
                        await Task.Delay((int)remaining, ct);
                    else
                        await Task.Yield();
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                log($"WebP background error: {ex}");
            }
        }, ct);
    }
}
