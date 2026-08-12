using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// 基于 System.IO.Compression 的默认 ZIP 解压辅助器。
    /// 纯文件和解压工作在线程池执行，避免逐块续接 Unity 主线程。
    /// </summary>
    public sealed class DefaultZipArchiveHelper : IArchiveHelper
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
                throw new RFrameworkException("DefaultZipArchiveHelper: options cannot be null.");
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
                        $"DefaultZipArchiveHelper: ZIP contains too many entries ({totalEntries}).");
                }

                long totalBytes = 0L;
                for (int i = 0; i < totalEntries; i++)
                {
                    totalBytes = checked(totalBytes + archive.Entries[i].Length);
                    if (options.MaxExtractedBytes > 0 && totalBytes > options.MaxExtractedBytes)
                    {
                        throw new RFrameworkException(
                            "DefaultZipArchiveHelper: ZIP extracted size exceeds the configured limit.");
                    }
                }

                long processedBytes = 0L;
                byte[] buffer = new byte[BufferSize];
                for (int entryIndex = 0; entryIndex < totalEntries; entryIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = archive.Entries[entryIndex];
                    string entryPath = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!entryPath.StartsWith(root, comparison))
                    {
                        throw new RFrameworkException(
                            $"DefaultZipArchiveHelper: unsafe ZIP entry path '{entry.FullName}'.");
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
    }
}
