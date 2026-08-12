namespace RFramework
{
    /// <summary>
    /// 下载任务当前阶段。
    /// </summary>
    public enum DownloadStage
    {
        /// <summary>正在预检远端文件信息。</summary>
        Preflight,

        /// <summary>正在传输文件。</summary>
        Downloading,

        /// <summary>正在校验已下载文件。</summary>
        Verifying,

        /// <summary>正在解压已下载文件。</summary>
        Extracting
    }
}
