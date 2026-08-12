using System;

namespace RFramework
{
    /// <summary>
    /// 压缩文件解压进度。
    /// </summary>
    public sealed class ArchiveProgress
    {
        /// <summary>获取已处理的解压后字节数。</summary>
        public long ProcessedBytes { get; }

        /// <summary>获取预计解压后总字节数；未知时为 -1。</summary>
        public long TotalBytes { get; }

        /// <summary>获取当前已处理条目数。</summary>
        public int ProcessedEntries { get; }

        /// <summary>获取压缩包总条目数；未知时为 -1。</summary>
        public int TotalEntries { get; }

        /// <summary>获取当前条目名称。</summary>
        public string EntryName { get; }

        /// <summary>获取 0 到 1 的解压进度；总大小未知时按条目数估算。</summary>
        public float Progress => TotalBytes > 0
            ? Math.Min(1f, (float)ProcessedBytes / TotalBytes)
            : TotalEntries > 0
                ? Math.Min(1f, (float)ProcessedEntries / TotalEntries)
                : 0f;

        /// <summary>
        /// 初始化解压进度。
        /// </summary>
        public ArchiveProgress(
            long processedBytes,
            long totalBytes,
            int processedEntries,
            int totalEntries,
            string entryName)
        {
            ProcessedBytes = processedBytes;
            TotalBytes = totalBytes;
            ProcessedEntries = processedEntries;
            TotalEntries = totalEntries;
            EntryName = entryName;
        }
    }
}
