namespace RFramework
{
    /// <summary>
    /// WebRequest 文件流传输进度。
    /// </summary>
    public sealed class WebDownloadProgress
    {
        /// <summary>获取目标临时文件当前已写入的总字节数。</summary>
        public long DownloadedBytes { get; }

        /// <summary>获取服务端声明的文件总字节数；未知时为 -1。</summary>
        public long TotalBytes { get; }

        /// <summary>获取下载进度；总大小未知时为 0。</summary>
        public float Progress => TotalBytes > 0
            ? (float)DownloadedBytes / TotalBytes
            : 0f;

        /// <summary>
        /// 初始化文件流传输进度。
        /// </summary>
        public WebDownloadProgress(long downloadedBytes, long totalBytes)
        {
            DownloadedBytes = downloadedBytes;
            TotalBytes = totalBytes;
        }
    }
}
