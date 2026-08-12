using System;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// 可靠文件下载模块。
    /// 在 WebRequest 流式传输之上提供断点续传、重试、校验和临时文件提交。
    /// </summary>
    public interface IDownloadModule
    {
        /// <summary>
        /// 设置压缩文件解压辅助器。传入 null 时恢复默认 ZIP 实现。
        /// </summary>
        void SetArchiveHelper(IArchiveHelper helper);

        /// <summary>
        /// 下载文件到指定路径。
        /// 取消或网络失败时保留 .part 文件，后续调用可继续下载。
        /// </summary>
        Task<DownloadResult> DownloadAsync(
            string url,
            string savePath,
            DownloadOptions options = null,
            IProgress<DownloadProgress> progress = null,
            CancellationToken ct = default);

        /// <summary>取消当前全部下载任务。</summary>
        void CancelAll();

        /// <summary>获取当前下载中的任务数量。</summary>
        int ActiveDownloadCount { get; }

        /// <summary>获取当前所有临时分片的已下载字节总数。</summary>
        long ActiveDownloadedBytes { get; }
    }
}
