using System.Security.Cryptography;

namespace MCModCoder.Core.Remote;

/// <summary>文件下载服务（临时文件 + 重试 + SHA1 校验）</summary>
public static class DownloadService
{
    /// <summary>
    /// 下载文件到指定路径。先下载到 destPath+".part"，成功后改名覆盖；
    /// 最多重试 3 次（指数退避 1s/2s/4s）；expectedSha1 非空时校验，不匹配则删除临时文件并抛出异常。
    /// progress 上报 0~1 进度（未知长度按已收到字节 / 估算值）。
    /// </summary>
    public static async Task DownloadFileAsync(string url, string destPath, string? expectedSha1,
        IProgress<double>? progress, CancellationToken ct = default)
    {
        var tempPath = destPath + ".part";
        try
        {
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await DownloadOnceAsync(url, tempPath, progress, ct);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 最多重试 3 次，指数退避 1s/2s/4s
                    if (attempt >= 3) throw;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), CancellationToken.None);
                }
            }

            // SHA1 校验
            if (!string.IsNullOrEmpty(expectedSha1))
            {
                var actual = await ComputeSha1Async(tempPath, ct);
                if (!string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                    throw new RemoteRequestException($"SHA1 校验失败：{Path.GetFileName(destPath)}（来源文件可能已更新）");
            }

            File.Move(tempPath, destPath, true);
        }
        catch (RemoteRequestException) { TryDelete(tempPath); throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { TryDelete(tempPath); throw; }
        catch (OperationCanceledException) { TryDelete(tempPath); throw new RemoteRequestException("网络请求超时，请检查网络连接"); }
        catch (HttpRequestException ex) { TryDelete(tempPath); throw new RemoteRequestException($"网络请求失败：{ex.Message}"); }
        catch { TryDelete(tempPath); throw; }
    }

    /// <summary>计算文件 SHA1（十六进制大写字符串）</summary>
    public static async Task<string> ComputeSha1Async(string filePath, CancellationToken ct = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA1.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    /// <summary>单次下载尝试（失败时由调用方重试；临时文件每次重建）</summary>
    private static async Task DownloadOnceAsync(string url, string tempPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await RemoteHttp.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        const long unknownEstimate = 16 * 1024 * 1024; // 未知长度时的估算总大小（16MB）

        long received = 0;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            if (progress != null)
            {
                var p = total > 0 ? (double)received / total.Value : (double)received / unknownEstimate;
                progress.Report(Math.Min(p, 0.99));
            }
        }
        progress?.Report(1.0);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
