namespace RFramework
{
    /// <summary>
    /// 压缩文件解压限制。
    /// 第三方解压 Helper 必须遵守路径边界、条目数和解压总大小限制。
    /// </summary>
    public sealed class ArchiveExtractionOptions
    {
        /// <summary>压缩格式；默认由当前辅助器自动识别。</summary>
        public ArchiveFormat Format { get; set; } = ArchiveFormat.Auto;

        /// <summary>压缩文件密码；未加密时为 null。</summary>
        public string Password { get; set; }

        /// <summary>允许的最大条目数；0 表示不限制。</summary>
        public int MaxEntries { get; set; }

        /// <summary>允许的解压后总字节数；0 表示不限制。</summary>
        public long MaxExtractedBytes { get; set; }
    }
}
