using System;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// 下载模块的压缩文件解压辅助器。
    /// 可替换为 SharpZipLib、SharpCompress 等第三方实现，但实现必须阻止目录穿越并遵守解压限制。
    /// </summary>
    public interface IArchiveHelper
    {
        /// <summary>
        /// 将压缩文件解压到一个不存在或为空的临时目录。
        /// DownloadModule 负责最终目录的事务式替换。
        /// </summary>
        Task ExtractAsync(
            string archivePath,
            string destinationDirectory,
            ArchiveExtractionOptions options,
            IProgress<ArchiveProgress> progress = null,
            CancellationToken ct = default);
    }
}
