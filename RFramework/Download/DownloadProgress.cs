using System;

namespace RFramework
{
    /// <summary>
    /// 可靠文件下载进度。
    /// </summary>
    public sealed class DownloadProgress
    {
        /// <summary>获取当前任务阶段。</summary>
        public DownloadStage Stage { get; }

        /// <summary>获取当前已写入临时文件的总字节数。</summary>
        public long DownloadedBytes { get; }

        /// <summary>获取文件总字节数；未知时为 -1。</summary>
        public long TotalBytes { get; }

        /// <summary>获取 0 到 1 的进度；总大小未知时为 0。</summary>
        public float Progress => TotalBytes > 0
            ? (float)DownloadedBytes / TotalBytes
            : 0f;

        /// <summary>获取本次下载的平均速度，单位为字节每秒。</summary>
        public double BytesPerSecond { get; }

        /// <summary>获取预计剩余时间；无法估算时为 null。</summary>
        public TimeSpan? EstimatedRemaining { get; }

        /// <summary>获取当前是否正在续传已有分片。</summary>
        public bool IsResuming { get; }

        /// <summary>获取当前正在处理的压缩条目；非解压阶段为 null。</summary>
        public string CurrentEntry { get; }

        /// <summary>
        /// 初始化下载进度。
        /// </summary>
        public DownloadProgress(
            long downloadedBytes,
            long totalBytes,
            double bytesPerSecond,
            TimeSpan? estimatedRemaining,
            bool isResuming,
            DownloadStage stage = DownloadStage.Downloading,
            string currentEntry = null)
        {
            Stage = stage;
            DownloadedBytes = downloadedBytes;
            TotalBytes = totalBytes;
            BytesPerSecond = bytesPerSecond;
            EstimatedRemaining = estimatedRemaining;
            IsResuming = isResuming;
            CurrentEntry = currentEntry;
        }
    }
}
