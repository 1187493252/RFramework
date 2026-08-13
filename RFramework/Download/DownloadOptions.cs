using System.Collections.Generic;

namespace RFramework
{
    /// <summary>
    /// 单次可靠文件下载选项。
    /// </summary>
    public sealed class DownloadOptions
    {
        /// <summary>是否使用 .part 文件断点续传，默认为 true。</summary>
        public bool Resume { get; set; } = true;

        /// <summary>最终文件已存在时是否允许替换，默认为 true。</summary>
        public bool OverwriteExisting { get; set; } = true;

        /// <summary>网络失败后的最大重试次数，默认为 2。</summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>首次重试等待毫秒数，后续按 2 倍退避，默认为 500。</summary>
        public int RetryDelayMilliseconds { get; set; } = 500;

        /// <summary>单次 HTTP 请求超时毫秒数；0 表示不限制总时长。</summary>
        public int RequestTimeoutMilliseconds { get; set; }

        /// <summary>预期文件大小；小于 0 表示不校验。</summary>
        public long ExpectedSize { get; set; } = -1;

        /// <summary>
        /// 有预期大小时是否先通过 HEAD 尝试预检远端大小，默认为 true。
        /// 远端不提供长度或不支持 HEAD 时继续下载，完成后仍执行实际文件校验。
        /// </summary>
        public bool PreflightRemoteSize { get; set; } = true;

        /// <summary>预期 SHA-256 十六进制字符串；空表示不校验。</summary>
        public string ExpectedSha256 { get; set; }

        /// <summary>自定义 HTTP 请求头。</summary>
        public Dictionary<string, string> Headers { get; set; }

        /// <summary>传递给 WebRequest 的请求标签。</summary>
        public string Tag { get; set; }

        /// <summary>传递给 WebRequest 并发队列的优先级。</summary>
        public uint Priority { get; set; }

        /// <summary>下载成功后是否解压压缩文件。</summary>
        public bool ExtractArchive { get; set; }

        /// <summary>压缩格式；默认由当前解压辅助器根据文件签名自动识别。</summary>
        public ArchiveFormat ArchiveFormat { get; set; } = ArchiveFormat.Auto;

        /// <summary>压缩文件密码；未加密或不需要密码时为 null。</summary>
        public string ArchivePassword { get; set; }

        /// <summary>解压目标目录；启用解压时不能为空。</summary>
        public string ExtractDirectory { get; set; }

        /// <summary>解压成功后是否删除下载的压缩文件。</summary>
        public bool DeleteArchiveAfterExtraction { get; set; }

        /// <summary>允许的最大条目数；0 表示不限制，默认 10000。</summary>
        public int MaxArchiveEntries { get; set; } = 10000;

        /// <summary>允许的解压后总字节数；0 表示不限制，默认 8 GiB。</summary>
        public long MaxExtractedBytes { get; set; } = 8L * 1024L * 1024L * 1024L;
    }
}
