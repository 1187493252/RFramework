namespace RFramework
{
    /// <summary>
    /// 压缩文件格式。
    /// Auto 表示由当前解压辅助器根据文件签名自动识别。
    /// </summary>
    public enum ArchiveFormat
    {
        /// <summary>自动识别压缩格式。</summary>
        Auto,

        /// <summary>ZIP 压缩格式。</summary>
        Zip,

        /// <summary>RAR 压缩格式。</summary>
        Rar,

        /// <summary>7-Zip 压缩格式。</summary>
        SevenZip,

        /// <summary>TAR 归档格式。</summary>
        Tar,

        /// <summary>GZip 压缩格式。</summary>
        GZip,

        /// <summary>BZip2 压缩格式。</summary>
        BZip2
    }
}
