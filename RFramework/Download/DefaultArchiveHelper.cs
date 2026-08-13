using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// 基于 System.IO.Compression 的默认压缩文件解压辅助器。
    /// 仅支持未加密 ZIP；Auto 按 ZIP 处理，其他格式明确拒绝。
    /// 纯文件和解压工作在线程池执行，避免逐块续接 Unity 主线程。
    /// </summary>
    public sealed class DefaultArchiveHelper : IArchiveHelper
    {
        private const int BufferSize = 1024 * 1024;

        /// <inheritdoc />
        public Task ExtractAsync(
            string archivePath,
            string destinationDirectory,
            ArchiveExtractionOptions options,
            IProgress<ArchiveProgress> progress = null,
            CancellationToken ct = default)
        {
            if (options == null)
            {
                throw new RFrameworkException("DefaultArchiveHelper: options cannot be null.");
            }

            if (options.Format != ArchiveFormat.Auto && options.Format != ArchiveFormat.Zip)
            {
                throw new RFrameworkException(
                    $"DefaultArchiveHelper: archive format '{options.Format}' is not supported.");
            }

            if (!string.IsNullOrEmpty(options.Password))
            {
                throw new RFrameworkException(
                    "DefaultArchiveHelper: encrypted ZIP is not supported.");
            }

            return Task.Run(
                () => ExtractCore(archivePath, destinationDirectory, options, progress, ct),
                ct);
        }

        private static void ExtractCore(
            string archivePath,
            string destinationDirectory,
            ArchiveExtractionOptions options,
            IProgress<ArchiveProgress> progress,
            CancellationToken ct)
        {
            Directory.CreateDirectory(destinationDirectory);
            string root = EnsureTrailingSeparator(Path.GetFullPath(destinationDirectory));
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            using (FileStream archiveStream = new FileStream(
                archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, false))
            {
                int totalEntries = archive.Entries.Count;
                if (options.MaxEntries > 0 && totalEntries > options.MaxEntries)
                {
                    throw new RFrameworkException(
                        $"DefaultArchiveHelper: ZIP contains too many entries ({totalEntries}).");
                }

                long totalBytes = 0L;
                for (int i = 0; i < totalEntries; i++)
                {
                    totalBytes = checked(totalBytes + archive.Entries[i].Length);
                    if (options.MaxExtractedBytes > 0 && totalBytes > options.MaxExtractedBytes)
                    {
                        throw new RFrameworkException(
                            "DefaultArchiveHelper: ZIP extracted size exceeds the configured limit.");
                    }
                }

                long processedBytes = 0L;
                byte[] buffer = new byte[BufferSize];
                for (int entryIndex = 0; entryIndex < totalEntries; entryIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = archive.Entries[entryIndex];
                    string normalized = NormalizeEntryName(entry.FullName);
                    string entryPath = Path.GetFullPath(Path.Combine(root, normalized));
                    if (!entryPath.StartsWith(root, comparison))
                    {
                        throw new RFrameworkException(
                            $"DefaultArchiveHelper: unsafe ZIP entry path '{entry.FullName}'.");
                    }

                    bool isDirectory = string.IsNullOrEmpty(entry.Name)
                        || entry.FullName.EndsWith("/", StringComparison.Ordinal)
                        || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(entryPath);
                        progress?.Report(new ArchiveProgress(
                            processedBytes, totalBytes, entryIndex + 1, totalEntries, entry.FullName));
                        continue;
                    }

                    string parent = Path.GetDirectoryName(entryPath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(
                        entryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        BufferSize, FileOptions.SequentialScan))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            processedBytes += read;
                            progress?.Report(new ArchiveProgress(
                                processedBytes, totalBytes, entryIndex, totalEntries, entry.FullName));
                        }
                    }

                    progress?.Report(new ArchiveProgress(
                        processedBytes, totalBytes, entryIndex + 1, totalEntries, entry.FullName));
                }
            }
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static string NormalizeEntryName(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                throw new RFrameworkException("DefaultArchiveHelper: ZIP entry name is empty.");
            }

            string normalized = entryName
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            if (normalized.IndexOf(':') >= 0)
            {
                throw new RFrameworkException(
                    $"DefaultArchiveHelper: unsafe ZIP entry path '{entryName}'.");
            }

            return normalized;
        }
    }
}
