using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// 可靠文件下载模块实现。
    /// </summary>
    internal sealed class DownloadModule : RFrameworkModule, IDownloadModule
    {
        private const string PartialFileSuffix = ".part";

        private readonly object syncRoot = new object();
        private readonly Dictionary<string, ActiveDownload> activeDownloads =
            new Dictionary<string, ActiveDownload>(
                Path.DirectorySeparatorChar == '\\'
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
        private readonly CancellationTokenSource stopCts = new CancellationTokenSource();
        private IArchiveHelper archiveHelper = new DefaultArchiveHelper();
        private bool stopped;

        /// <inheritdoc />
        internal override int Order => 14;

        /// <inheritdoc />
        public int ActiveDownloadCount
        {
            get
            {
                lock (syncRoot)
                {
                    return activeDownloads.Count;
                }
            }
        }

        /// <inheritdoc />
        public long ActiveDownloadedBytes
        {
            get
            {
                lock (syncRoot)
                {
                    long total = 0;
                    foreach (ActiveDownload active in activeDownloads.Values)
                    {
                        total += active.DownloadedBytes;
                    }

                    return total;
                }
            }
        }

        /// <inheritdoc />
        public void SetArchiveHelper(IArchiveHelper helper)
        {
            lock (syncRoot)
            {
                if (stopped)
                {
                    throw new RFrameworkException("DownloadModule: module is stopped.");
                }

                archiveHelper = helper ?? new DefaultArchiveHelper();
            }
        }

        /// <inheritdoc />
        public async Task<DownloadResult> DownloadAsync(
            string url,
            string savePath,
            DownloadOptions options = null,
            IProgress<DownloadProgress> progress = null,
            CancellationToken ct = default)
        {
            ValidateArguments(url, savePath, options);
            options ??= new DownloadOptions();

            string fullPath = Path.GetFullPath(savePath);
            string partialPath = fullPath + PartialFileSuffix;
            if (File.Exists(fullPath) && !options.OverwriteExisting)
            {
                throw new RFrameworkException($"DownloadModule: target file already exists: '{fullPath}'.");
            }

            CancellationTokenSource operationCts;
            ActiveDownload active;
            lock (syncRoot)
            {
                if (stopped)
                {
                    throw new RFrameworkException("DownloadModule: module is stopped.");
                }

                if (activeDownloads.ContainsKey(fullPath))
                {
                    throw new RFrameworkException(
                        $"DownloadModule: target path is already being downloaded: '{fullPath}'.");
                }

                operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct, stopCts.Token);
                active = new ActiveDownload(operationCts);
                activeDownloads.Add(fullPath, active);
            }

            try
            {
                EnsureParentDirectory(fullPath);
                if (!options.Resume && File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }

                return await DownloadCoreAsync(
                    url, fullPath, partialPath, options, progress, active, operationCts.Token);
            }
            finally
            {
                lock (syncRoot)
                {
                    activeDownloads.Remove(fullPath);
                }

                operationCts.Dispose();
            }
        }

        /// <inheritdoc />
        public void CancelAll()
        {
            ActiveDownload[] snapshot;
            lock (syncRoot)
            {
                snapshot = new ActiveDownload[activeDownloads.Count];
                activeDownloads.Values.CopyTo(snapshot, 0);
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i].Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 已结束任务可能刚完成释放。
                }
            }
        }

        internal override void Tick(float deltaTime, float unscaledDeltaTime)
        {
            // 下载由 Task 和底层 WebRequest 驱动。
        }

        internal override void Stop()
        {
            lock (syncRoot)
            {
                if (stopped)
                {
                    return;
                }

                stopped = true;
            }

            stopCts.Cancel();
            CancelAll();
            stopCts.Dispose();
        }

        private async Task<DownloadResult> DownloadCoreAsync(
            string url,
            string fullPath,
            string partialPath,
            DownloadOptions options,
            IProgress<DownloadProgress> progress,
            ActiveDownload active,
            CancellationToken ct)
        {
            IWebRequestModule webRequest = RFrameworkModuleHost.Get<IWebRequestModule>();
            await ValidateRemoteSizeAsync(url, options, progress, webRequest, ct);
            int requestCount = 0;
            int remainingRetries = options.MaxRetries;
            bool resumed = false;
            bool restartedAfterIgnoredRange = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long offset = options.Resume && File.Exists(partialPath)
                    ? new FileInfo(partialPath).Length
                    : 0L;
                bool requestingRange = offset > 0;
                resumed |= requestingRange;
                requestCount++;

                Stopwatch stopwatch = Stopwatch.StartNew();
                ProgressAdapter adapter = new ProgressAdapter(
                    progress, active, offset, options.ExpectedSize, requestingRange, stopwatch);

                try
                {
                    WebResponse response = await webRequest.DownloadFileRangeAsync(
                        url,
                        partialPath,
                        offset,
                        adapter,
                        options.Headers,
                        options.Tag,
                        options.Priority,
                        options.RequestTimeoutMilliseconds,
                        ct);

                    if (requestingRange && response.StatusCode == 200)
                    {
                        if (restartedAfterIgnoredRange)
                        {
                            throw new RFrameworkException(
                                "DownloadModule: server repeatedly ignored the Range request.");
                        }

                        File.Delete(partialPath);
                        restartedAfterIgnoredRange = true;
                        resumed = false;
                        continue;
                    }

                    if (response.StatusCode == 416)
                    {
                        if (offset == 0)
                        {
                            throw new RFrameworkException(
                                "DownloadModule: server rejected a full download request with HTTP 416.");
                        }

                        RestorePartialLength(partialPath, offset);
                        if (await IsValidFileAsync(partialPath, options, ct))
                        {
                            CommitPartialFile(partialPath, fullPath, options.OverwriteExisting);
                            return await CompleteDownloadAsync(
                                fullPath, options, resumed, requestCount, progress, ct);
                        }

                        File.Delete(partialPath);
                        resumed = false;
                        continue;
                    }

                    if (!response.IsSuccess)
                    {
                        RestorePartialLength(partialPath, offset);
                        throw new RFrameworkException(string.Format(
                            "DownloadModule: request failed, status={0}, error={1}",
                            response.StatusCode,
                            response.ErrorMessage));
                    }

                    if (requestingRange && !IsValidContentRange(response, offset))
                    {
                        File.Delete(partialPath);
                        resumed = false;
                        continue;
                    }

                    try
                    {
                        progress?.Report(new DownloadProgress(
                            0L,
                            options.ExpectedSize,
                            0d,
                            null,
                            resumed,
                            DownloadStage.Verifying));
                        await ValidateFileAsync(partialPath, options, ct);
                    }
                    catch (RFrameworkException)
                    {
                        File.Delete(partialPath);
                        throw;
                    }
                    CommitPartialFile(partialPath, fullPath, options.OverwriteExisting);
                    return await CompleteDownloadAsync(
                        fullPath, options, resumed, requestCount, progress, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (!(ex is RFrameworkException
                    && ex.Message.IndexOf("verification failed", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (remainingRetries <= 0)
                    {
                        throw new RFrameworkException(
                            $"DownloadModule: download failed after {requestCount} request(s).", ex);
                    }

                    int retryIndex = options.MaxRetries - remainingRetries;
                    remainingRetries--;
                    int delay = options.RetryDelayMilliseconds * (1 << Math.Min(retryIndex, 10));
                    if (delay > 0)
                    {
                        await Task.Delay(delay, ct);
                    }
                }
            }
        }

        private static bool IsValidContentRange(WebResponse response, long expectedStart)
        {
            if (response.StatusCode != 206)
            {
                return false;
            }

            string value = response.GetHeader("Content-Range");
            if (string.IsNullOrWhiteSpace(value)
                || !value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int dash = value.IndexOf('-', 6);
            return dash > 6
                && long.TryParse(value.Substring(6, dash - 6), out long actualStart)
                && actualStart == expectedStart;
        }

        private static async Task ValidateRemoteSizeAsync(
            string url,
            DownloadOptions options,
            IProgress<DownloadProgress> progress,
            IWebRequestModule webRequest,
            CancellationToken ct)
        {
            if (!options.PreflightRemoteSize || options.ExpectedSize < 0)
            {
                return;
            }

            progress?.Report(new DownloadProgress(
                0L,
                options.ExpectedSize,
                0d,
                null,
                false,
                DownloadStage.Preflight));

            try
            {
                WebResponse response = await webRequest.SendAsync(
                    new WebRequestData
                    {
                        Url = url,
                        Method = HttpMethod.Head,
                        Headers = options.Headers == null
                            ? null
                            : new Dictionary<string, string>(options.Headers),
                        TimeoutMs = options.RequestTimeoutMilliseconds,
                        Tag = options.Tag,
                        Priority = options.Priority
                    },
                    ct: ct);

                string contentEncoding = response.GetHeader("Content-Encoding");
                if (!response.IsSuccess
                    || (!string.IsNullOrWhiteSpace(contentEncoding)
                        && !string.Equals(contentEncoding, "identity", StringComparison.OrdinalIgnoreCase))
                    || !long.TryParse(response.GetHeader("Content-Length"), out long remoteSize))
                {
                    return;
                }

                if (remoteSize != options.ExpectedSize)
                {
                    throw new RFrameworkException(string.Format(
                        "DownloadModule: remote size verification failed before download. Expected size {0}, remote size {1}.",
                        options.ExpectedSize,
                        remoteSize));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RFrameworkException ex) when (
                ex.Message.IndexOf("remote size verification failed", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // HEAD 预检不受支持或失败时，降级为下载完成后的实际文件校验。
            }
            catch (RFrameworkException)
            {
                throw;
            }
            catch (Exception)
            {
                // HEAD 仅用于提前失败优化，不能成为下载的额外单点依赖。
            }
        }

        private static async Task ValidateFileAsync(
            string path,
            DownloadOptions options,
            CancellationToken ct)
        {
            if (!File.Exists(path))
            {
                throw new RFrameworkException("DownloadModule: verification failed because the partial file is missing.");
            }

            long length = new FileInfo(path).Length;
            if (options.ExpectedSize >= 0 && length != options.ExpectedSize)
            {
                throw new RFrameworkException(string.Format(
                    "DownloadModule: verification failed. Expected size {0}, actual size {1}.",
                    options.ExpectedSize,
                    length));
            }

            if (!string.IsNullOrWhiteSpace(options.ExpectedSha256))
            {
                string actual = await ComputeSha256Async(path, ct);
                string expected = NormalizeHash(options.ExpectedSha256);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RFrameworkException(string.Format(
                        "DownloadModule: verification failed. Expected SHA-256 {0}, actual {1}.",
                        expected,
                        actual));
                }
            }
        }

        private static async Task<bool> IsValidFileAsync(
            string path,
            DownloadOptions options,
            CancellationToken ct)
        {
            try
            {
                await ValidateFileAsync(path, options, ct);
                return options.ExpectedSize >= 0 || !string.IsNullOrWhiteSpace(options.ExpectedSha256);
            }
            catch (RFrameworkException)
            {
                return false;
            }
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                }

                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                StringBuilder builder = new StringBuilder(64);
                foreach (byte value in sha256.Hash)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static string NormalizeHash(string value)
        {
            return value.Replace("-", string.Empty).Replace(" ", string.Empty).Trim();
        }

        private static void CommitPartialFile(string partialPath, string targetPath, bool overwrite)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(partialPath, targetPath);
                return;
            }

            if (!overwrite)
            {
                throw new RFrameworkException($"DownloadModule: target file already exists: '{targetPath}'.");
            }

            try
            {
                File.Replace(partialPath, targetPath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(targetPath);
                File.Move(partialPath, targetPath);
            }
            catch (IOException)
            {
                File.Delete(targetPath);
                File.Move(partialPath, targetPath);
            }
        }

        private static void RestorePartialLength(string partialPath, long length)
        {
            if (!File.Exists(partialPath))
            {
                return;
            }

            using (FileStream stream = new FileStream(partialPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(Math.Max(0L, length));
            }
        }

        private async Task<DownloadResult> CompleteDownloadAsync(
            string path,
            DownloadOptions options,
            bool resumed,
            int requestCount,
            IProgress<DownloadProgress> progress,
            CancellationToken ct)
        {
            long fileSize = new FileInfo(path).Length;
            if (!options.ExtractArchive)
            {
                return new DownloadResult(path, fileSize, resumed, requestCount);
            }

            string extractDirectory = Path.GetFullPath(options.ExtractDirectory);
            IArchiveHelper helper;
            lock (syncRoot)
            {
                helper = archiveHelper;
            }

            await ExtractArchiveAsync(path, extractDirectory, options, helper, progress, ct);
            if (options.DeleteArchiveAfterExtraction)
            {
                File.Delete(path);
            }

            return new DownloadResult(
                path, fileSize, resumed, requestCount, true, extractDirectory);
        }

        private static async Task ExtractArchiveAsync(
            string archivePath,
            string destinationDirectory,
            DownloadOptions options,
            IArchiveHelper helper,
            IProgress<DownloadProgress> progress,
            CancellationToken ct)
        {
            string temporaryDirectory = destinationDirectory + ".extracting." + Guid.NewGuid().ToString("N");
            string backupDirectory = destinationDirectory + ".backup." + Guid.NewGuid().ToString("N");
            bool destinationMoved = false;
            try
            {
                IProgress<ArchiveProgress> archiveProgress = progress == null
                    ? null
                    : new ArchiveProgressAdapter(progress);
                await helper.ExtractAsync(
                    archivePath,
                    temporaryDirectory,
                    new ArchiveExtractionOptions
                    {
                        Format = options.ArchiveFormat,
                        Password = options.ArchivePassword,
                        MaxEntries = options.MaxArchiveEntries,
                        MaxExtractedBytes = options.MaxExtractedBytes
                    },
                    archiveProgress,
                    ct);

                string destinationParent = Path.GetDirectoryName(destinationDirectory);
                if (!string.IsNullOrEmpty(destinationParent))
                {
                    Directory.CreateDirectory(destinationParent);
                }

                if (Directory.Exists(destinationDirectory))
                {
                    Directory.Move(destinationDirectory, backupDirectory);
                    destinationMoved = true;
                }

                Directory.Move(temporaryDirectory, destinationDirectory);
                if (destinationMoved)
                {
                    Directory.Delete(backupDirectory, true);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                RestoreExtractionDirectory(destinationDirectory, temporaryDirectory, backupDirectory, destinationMoved);
                throw ex is RFrameworkException
                    ? ex
                    : new RFrameworkException("DownloadModule: archive extraction failed.", ex);
            }
            catch
            {
                RestoreExtractionDirectory(destinationDirectory, temporaryDirectory, backupDirectory, destinationMoved);
                throw;
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
                if (Directory.Exists(destinationDirectory))
                {
                    TryDeleteDirectory(backupDirectory);
                }
            }
        }

        private static void RestoreExtractionDirectory(
            string destinationDirectory,
            string temporaryDirectory,
            string backupDirectory,
            bool destinationMoved)
        {
            TryDeleteDirectory(temporaryDirectory);
            if (!destinationMoved || !Directory.Exists(backupDirectory))
            {
                return;
            }

            TryDeleteDirectory(destinationDirectory);
            Directory.Move(backupDirectory, destinationDirectory);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // 清理失败不覆盖原始下载或解压异常。
            }
        }

        private static void EnsureParentDirectory(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void ValidateArguments(string url, string savePath, DownloadOptions options)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new RFrameworkException("DownloadModule: url is invalid.");
            }

            if (string.IsNullOrWhiteSpace(savePath))
            {
                throw new RFrameworkException("DownloadModule: savePath is invalid.");
            }

            if (options == null)
            {
                return;
            }

            if (options.MaxRetries < 0 || options.RetryDelayMilliseconds < 0
                || options.RequestTimeoutMilliseconds < 0 || options.ExpectedSize < -1
                || options.MaxArchiveEntries < 0 || options.MaxExtractedBytes < 0)
            {
                throw new RFrameworkException("DownloadModule: numeric options cannot be negative.");
            }

            if (options.ExtractArchive && string.IsNullOrWhiteSpace(options.ExtractDirectory))
            {
                throw new RFrameworkException(
                    "DownloadModule: ExtractDirectory is required when ExtractArchive is enabled.");
            }
        }

        private sealed class ActiveDownload
        {
            public CancellationTokenSource Cancellation { get; }
            public long DownloadedBytes { get; set; }

            public ActiveDownload(CancellationTokenSource cancellation)
            {
                Cancellation = cancellation;
            }
        }

        private sealed class ProgressAdapter : IProgress<WebDownloadProgress>
        {
            private readonly IProgress<DownloadProgress> progress;
            private readonly ActiveDownload active;
            private readonly long initialBytes;
            private readonly long expectedSize;
            private readonly bool isResuming;
            private readonly Stopwatch stopwatch;

            public ProgressAdapter(
                IProgress<DownloadProgress> progress,
                ActiveDownload active,
                long initialBytes,
                long expectedSize,
                bool isResuming,
                Stopwatch stopwatch)
            {
                this.progress = progress;
                this.active = active;
                this.initialBytes = initialBytes;
                this.expectedSize = expectedSize;
                this.isResuming = isResuming;
                this.stopwatch = stopwatch;
                active.DownloadedBytes = initialBytes;
            }

            public void Report(WebDownloadProgress value)
            {
                if (value == null)
                {
                    return;
                }

                active.DownloadedBytes = value.DownloadedBytes;
                long transferred = Math.Max(0L, value.DownloadedBytes - initialBytes);
                double seconds = stopwatch.Elapsed.TotalSeconds;
                double speed = seconds > 0d ? transferred / seconds : 0d;
                long total = value.TotalBytes > 0 ? value.TotalBytes : expectedSize;
                TimeSpan? remaining = total > value.DownloadedBytes && speed > 0d
                    ? TimeSpan.FromSeconds((total - value.DownloadedBytes) / speed)
                    : (TimeSpan?)null;
                progress?.Report(new DownloadProgress(
                    value.DownloadedBytes, total, speed, remaining, isResuming));
            }
        }

        private sealed class ArchiveProgressAdapter : IProgress<ArchiveProgress>
        {
            private readonly IProgress<DownloadProgress> progress;

            public ArchiveProgressAdapter(IProgress<DownloadProgress> progress)
            {
                this.progress = progress;
            }

            public void Report(ArchiveProgress value)
            {
                if (value == null)
                {
                    return;
                }

                progress.Report(new DownloadProgress(
                    value.ProcessedBytes,
                    value.TotalBytes,
                    0d,
                    null,
                    false,
                    DownloadStage.Extracting,
                    value.EntryName));
            }
        }
    }
}
