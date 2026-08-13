namespace RFramework
{
    /// <summary>
    /// 可靠文件下载结果。
    /// </summary>
    public sealed class DownloadResult
    {
        /// <summary>获取最终文件路径。</summary>
        public string FilePath { get; }

        /// <summary>获取最终文件大小。</summary>
        public long FileSize { get; }

        /// <summary>获取本次任务是否成功使用了已有分片。</summary>
        public bool Resumed { get; }

        /// <summary>获取实际执行的 HTTP 请求次数。</summary>
        public int RequestCount { get; }

        /// <summary>获取下载文件是否已完成解压。</summary>
        public bool Extracted { get; }

        /// <summary>获取解压目标目录；未解压时为 null。</summary>
        public string ExtractDirectory { get; }

        /// <summary>
        /// 初始化下载结果。
        /// </summary>
        public DownloadResult(
            string filePath,
            long fileSize,
            bool resumed,
            int requestCount,
            bool extracted = false,
            string extractDirectory = null)
        {
            FilePath = filePath;
            FileSize = fileSize;
            Resumed = resumed;
            RequestCount = requestCount;
            Extracted = extracted;
            ExtractDirectory = extractDirectory;
        }
    }
}
