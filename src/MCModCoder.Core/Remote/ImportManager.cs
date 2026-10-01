using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCModCoder.Core.Remote;

/// <summary>下载任务状态</summary>
public enum DownloadState { Queued, Downloading, Verifying, Done, Failed, Cancelled }

/// <summary>下载任务</summary>
public class DownloadTask
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string DestPath { get; set; } = "";
    public string? Sha1 { get; set; }
    public long SizeBytes { get; set; }
    public RemotePlatform Platform { get; set; }
    public DownloadState State { get; set; } = DownloadState.Queued;
    /// <summary>进度 0~1</summary>
    public double Progress { get; set; }
    public string Error { get; set; } = "";
    /// <summary>任务取消源（内部使用）</summary>
    internal CancellationTokenSource Cts { get; set; } = new();
}

/// <summary>已导入的整合包记录</summary>
public class ImportedPack
{
    public string Name { get; set; } = "";
    public string Dir { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime ImportTime { get; set; } = DateTime.Now;
}

/// <summary>远程资源导入管理器（下载队列，3 并发后台处理，线程安全）</summary>
public static class ImportManager
{
    /// <summary>队列任何变化（UI 绑定刷新用）</summary>
    public static event Action? Changed;

    private static readonly object Lock = new();
    private static readonly List<DownloadTask> _tasks = new();
    private static readonly List<ImportedPack> _packs = new();
    private static SemaphoreSlim? _gate;
    private static CancellationTokenSource? _pumpCts;
    private static bool _started;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<DownloadTask> Tasks { get { lock (Lock) return _tasks.ToList(); } }
    public static IReadOnlyList<ImportedPack> ImportedPacks { get { lock (Lock) return _packs.ToList(); } }

    /// <summary>加入下载队列（未启动时自动启动后台处理）</summary>
    public static DownloadTask Enqueue(string name, string url, string destPath, string? sha1, long size, RemotePlatform platform)
    {
        var t = new DownloadTask
        {
            Name = name,
            Url = url,
            DestPath = destPath,
            Sha1 = sha1,
            SizeBytes = size,
            Platform = platform,
            State = DownloadState.Queued
        };
        lock (Lock) _tasks.Add(t);
        RaiseChanged();
        Start();
        return t;
    }

    /// <summary>启动 3 并发后台下载处理（幂等）</summary>
    public static void Start()
    {
        lock (Lock)
        {
            if (_started) return;
            _started = true;
            _pumpCts = new CancellationTokenSource();
            _gate = new SemaphoreSlim(3, 3);
        }
        _ = Task.Run(() => PumpLoopAsync(_pumpCts!.Token));
    }

    /// <summary>取消任务</summary>
    public static void Cancel(DownloadTask t)
    {
        bool changed = false;
        lock (Lock)
        {
            if (t.State == DownloadState.Queued) { t.State = DownloadState.Cancelled; changed = true; }
            else if (t.State == DownloadState.Downloading || t.State == DownloadState.Verifying) t.Cts.Cancel();
        }
        if (changed) RaiseChanged();
    }

    /// <summary>重试失败/已取消的任务</summary>
    public static void Retry(DownloadTask t)
    {
        lock (Lock)
        {
            if (t.State != DownloadState.Failed && t.State != DownloadState.Cancelled) return;
            t.State = DownloadState.Queued;
            t.Error = "";
            t.Progress = 0;
            t.Cts = new CancellationTokenSource();
        }
        RaiseChanged();
        Start();
    }

    /// <summary>移除任务（下载中则中断）</summary>
    public static void Remove(DownloadTask t)
    {
        lock (Lock) _tasks.Remove(t);
        t.Cts.Cancel();
        RaiseChanged();
    }

    /// <summary>登记一次整合包导入记录</summary>
    public static void RegisterImport(ModpackPlan plan, string importDir)
    {
        var pack = new ImportedPack
        {
            Name = plan.Name,
            Dir = importDir,
            McVersion = plan.McVersion,
            Loader = plan.Loader,
            Source = plan.Platform == RemotePlatform.Modrinth ? "Modrinth" : "CurseForge",
            ImportTime = DateTime.Now
        };
        lock (Lock) _packs.Add(pack);
        RaiseChanged();
    }

    /// <summary>保存历史到 dir 下（downloads.json + imports.json），失败静默忽略</summary>
    public static void SaveHistory(string dir)
    {
        try
        {
            List<TaskRecord> taskRecords;
            List<PackRecord> packRecords;
            lock (Lock)
            {
                taskRecords = _tasks.Select(t => new TaskRecord
                {
                    Name = t.Name, Url = t.Url, DestPath = t.DestPath, Sha1 = t.Sha1,
                    SizeBytes = t.SizeBytes, Platform = t.Platform, State = t.State,
                    Progress = t.Progress, Error = t.Error
                }).ToList();
                packRecords = _packs.Select(p => new PackRecord
                {
                    Name = p.Name, Dir = p.Dir, McVersion = p.McVersion,
                    Loader = p.Loader, Source = p.Source, ImportTime = p.ImportTime
                }).ToList();
            }
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "downloads.json"), JsonSerializer.Serialize(taskRecords, JsonOpts));
            File.WriteAllText(Path.Combine(dir, "imports.json"), JsonSerializer.Serialize(packRecords, JsonOpts));
        }
        catch { }
    }

    /// <summary>从 dir 恢复历史（downloads.json + imports.json，容错损坏文件）</summary>
    public static void LoadHistory(string dir)
    {
        lock (Lock) { _tasks.Clear(); _packs.Clear(); }

        try
        {
            var file = Path.Combine(dir, "downloads.json");
            if (File.Exists(file))
            {
                var records = JsonSerializer.Deserialize<List<TaskRecord>>(File.ReadAllText(file), JsonOpts);
                if (records != null)
                {
                    lock (Lock)
                        foreach (var r in records)
                        {
                            var state = r.State;
                            // 应用重启导致的中间态视为失败，可手动重试
                            if (state is DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying)
                                state = DownloadState.Failed;
                            _tasks.Add(new DownloadTask
                            {
                                Name = r.Name, Url = r.Url, DestPath = r.DestPath, Sha1 = r.Sha1,
                                SizeBytes = r.SizeBytes, Platform = r.Platform, State = state,
                                Progress = state == DownloadState.Done ? 1 : r.Progress, Error = r.Error
                            });
                        }
                }
            }
        }
        catch { }

        try
        {
            var file = Path.Combine(dir, "imports.json");
            if (File.Exists(file))
            {
                var records = JsonSerializer.Deserialize<List<PackRecord>>(File.ReadAllText(file), JsonOpts);
                if (records != null)
                {
                    lock (Lock)
                        foreach (var r in records)
                            _packs.Add(new ImportedPack
                            {
                                Name = r.Name, Dir = r.Dir, McVersion = r.McVersion,
                                Loader = r.Loader, Source = r.Source, ImportTime = r.ImportTime
                            });
                }
            }
        }
        catch { }

        RaiseChanged();
    }

    /// <summary>后台泵循环：从队列取任务，3 并发处理</summary>
    private static async Task PumpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DownloadTask? next = null;
            try
            {
                lock (Lock)
                {
                    next = _tasks.FirstOrDefault(t => t.State == DownloadState.Queued);
                    if (next != null) next.State = DownloadState.Downloading;
                }
                if (next == null)
                {
                    await Task.Delay(500, ct);
                    continue;
                }
                try
                {
                    await _gate!.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // 队列关闭：把已标记为"下载中"的任务还原为排队，避免重启后状态卡死
                    lock (Lock) { if (next.State == DownloadState.Downloading) next.State = DownloadState.Queued; }
                    return;
                }
                catch
                {
                    lock (Lock) { if (next.State == DownloadState.Downloading) next.State = DownloadState.Queued; }
                    continue;
                }
            }
            catch (OperationCanceledException) { return; }
            catch { continue; }
            _ = Task.Run(() => ProcessTaskAsync(next, ct));
        }
    }

    /// <summary>执行单个下载任务</summary>
    private static async Task ProcessTaskAsync(DownloadTask t, CancellationToken queueCt)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(queueCt, t.Cts.Token);
            double lastReported = 0;
            var progress = new Progress<double>(p =>
            {
                if (p < lastReported) lastReported = p; // 重试后进度回退，重新累计
                if (p - lastReported < 0.02 && p < 1.0) return; // 节流：每 ≥2% 或完成才触发
                lastReported = p;
                t.Progress = Math.Clamp(p, 0, 1);
                if (p >= 1.0 && !string.IsNullOrEmpty(t.Sha1))
                    t.State = DownloadState.Verifying; // 进入校验阶段
                RaiseChanged();
            });

            lock (Lock)
            {
                t.Error = "";
                t.State = DownloadState.Downloading;
            }
            RaiseChanged();

            await DownloadService.DownloadFileAsync(t.Url, t.DestPath, t.Sha1, progress, linked.Token);

            lock (Lock)
            {
                t.Progress = 1;
                t.State = DownloadState.Done;
            }
            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
            lock (Lock)
            {
                t.Error = "已取消";
                t.State = DownloadState.Cancelled;
            }
            RaiseChanged();
        }
        catch (Exception ex)
        {
            lock (Lock)
            {
                t.Error = ex.Message;
                t.Progress = 0;
                t.State = DownloadState.Failed;
            }
            RaiseChanged();
        }
        finally
        {
            _gate?.Release();
        }
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* UI 处理器异常不影响队列运行 */ }
    }

    // ---------- 持久化记录 ----------
    private class TaskRecord
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string DestPath { get; set; } = "";
        public string? Sha1 { get; set; }
        public long SizeBytes { get; set; }
        public RemotePlatform Platform { get; set; }
        public DownloadState State { get; set; }
        public double Progress { get; set; }
        public string Error { get; set; } = "";
    }

    private class PackRecord
    {
        public string Name { get; set; } = "";
        public string Dir { get; set; } = "";
        public string McVersion { get; set; } = "";
        public string Loader { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime ImportTime { get; set; }
    }
}
