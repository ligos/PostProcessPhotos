using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Text;
using System.Diagnostics;

using Microsoft.Extensions.Configuration;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.MetaData;
using SixLabors.ImageSharp.MetaData.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace MurrayGrant.IncomingToLibrary
{
    class Program
    {
        private static bool ReceivedCancelSignal;

        public static async Task Main(string[] args)
        {
            Console.WriteLine("Murray Grant - PostProcessPhotos - IncomingToLibrary");
            Console.CancelKeyPress += Console_CancelKeyPress;

            // Read config.
            var conf = new ConfigurationBuilder()
                        .SetBasePath(Path.Combine(AppContext.BaseDirectory))
                        .AddJsonFile("appsettings.json", optional: false)
                        .Build();
            var config = conf.GetSection("PostProcessPhotos").Get<Config>();

            // These get created for each destination folder.
            var metadataByPath = new Dictionary<string, Metadata>(StringComparer.OrdinalIgnoreCase);

            // Check the target folder exists (in case the mount point isn't valid, that is, Loki is off).
            if (!Directory.Exists(config.DestinationPath))
            {
                Console.WriteLine("Destination path '{0}' doesn't exist, exiting.", config.DestinationPath);
                return;
            }

            // Don't grab all the CPU.
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

            // For each source
            foreach (var s in config.PhotoSources)
            {
                Console.WriteLine("Processing incoming photos from: {0}", s.SourcePath);

                // Get all files recursively.
                var sw = Stopwatch.StartNew();
                var sourceFiles = new DirectoryInfo(s.SourcePath).EnumerateFiles("*", SearchOption.AllDirectories)
                                    .Where(fi => !IsTempFile(fi));
                foreach (var fi in sourceFiles)
                {
                    // Process file based on file type.
                    if (String.Equals(fi.Extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                        || String.Equals(fi.Extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
                        await ProcessFile(fi, ProcessJpg, s, config, metadataByPath);
                    else if (String.Equals(fi.Extension, ".mp4", StringComparison.OrdinalIgnoreCase))
                        await ProcessFile(fi, ProcessMp4, s, config, metadataByPath);
                    else
                        await ProcessFile(fi, ProcessUnknown, s, config, metadataByPath);

                    if (ReceivedCancelSignal)
                        break;
                }

                Console.WriteLine("Finished processing incoming photos from: {0}, in {1:N2} seconds.", s.SourcePath, sw.Elapsed.TotalSeconds);
                if (ReceivedCancelSignal)
                    break;
            }


            // Save all metadata.
            Console.Write("Updating {0:N0} metadata files...", metadataByPath.Count);
            foreach (var m in metadataByPath.Values)
                await m.SaveToFile();
            Console.WriteLine(" Done");
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            ReceivedCancelSignal = true;
            e.Cancel = true;        // Co-operative shutdown rather than immediate.
        }

        private static bool IsTempFile(FileInfo fi)
            => fi.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase);

        private static async Task ProcessFile(FileInfo fi, Func<ProcessFileData, Task> fileTypeProcessor, PhotoSource srcCfg, Config cfg, Dictionary<string, Metadata> metadataByDestinationFolder)
        {
            Console.Write(fi.Name + ": ");
            Metadata metadataForException = null;
            MetadataRecord mrForException = null;
            string destPathForException = null;

            try
            {
                var sw = Stopwatch.StartNew();
                // Read date for file.
                var imgDate = GetDateForFile(fi, srcCfg.FilesInLocalOrUtc.GetValueOrDefault(DateTimeKind.Local));
                var imgDateLocal = imgDate.LocalDateTime;

                // Check if the date means we'll import it.
                if (imgDateLocal < cfg.EffectiveFromLocal)
                {
                    Console.WriteLine("Older than {0:yyyy-MM-dd}, not processing ({1:N0}ms).", cfg.EffectiveFromLocal, sw.Elapsed.TotalMilliseconds);
                    return;
                }

                // Determine destination folder and filename.
                var destFolder = Path.Combine(cfg.DestinationPath, imgDateLocal.ToString(cfg.DestinationSubFolderPattern));
                var destName = srcCfg.FilenamePrefix + fi.Name;
                var destPath = Path.Combine(destFolder, destName);
                destPathForException = destPath;

                // Preload the destination metadata.
                if (!metadataByDestinationFolder.TryGetValue(destFolder, out var metadata))
                {
                    metadata = await Metadata.LoadFromFile(Path.Combine(destFolder, cfg.MetadataFilename));
                    metadataByDestinationFolder.Add(destFolder, metadata);
                }
                metadataForException = metadata;

                // Check if file is already there (via metadata, or on disk).
                var destFi = new FileInfo(destPath);
                var destLength = destFi.Exists ? destFi.Length : 0L;
                var alreadyExistsOnDisk = destFi.Exists && destLength > 1024;
                var (mr, alreadyExistsInMetadata) = metadata.GetOrAddFile(destName, () => new MetadataRecord() { DestinationFilename = destName, SourceFilename = fi.Name, OriginalLength = fi.Length, Prefix = srcCfg.FilenamePrefix, ProcessingDatestamp = DateTimeOffset.Now });
                var previousProcessingError = alreadyExistsOnDisk && alreadyExistsInMetadata && destLength <= 1024 && mr.OriginalLength > 1024;
                mrForException = mr;

                if (previousProcessingError || (!alreadyExistsOnDisk && !alreadyExistsInMetadata))
                {
                    if (previousProcessingError)
                        Console.Write("Error during previous processing run - attempting to reprocess: ");

                    EnsureFolderFor(destPath);
                    if (File.Exists(destPath))
                        File.Delete(destPath);

                    await fileTypeProcessor(new ProcessFileData()
                    {
                        Source = fi,
                        DestinationPath = destPath,
                        SourceConfig = srcCfg,
                        Config = cfg,
                        FileDateLocal = imgDateLocal,
                        MetadataByDestinationFolder = metadataByDestinationFolder,
                    });

                    sw.Stop();
                    Console.WriteLine("Processed OK ({0:N0}ms).", sw.Elapsed.TotalMilliseconds);
                }

                sw.Stop();
                if (alreadyExistsOnDisk)
                    Console.WriteLine("Already copied ({0:N0}ms).", sw.Elapsed.TotalMilliseconds);
                else if (alreadyExistsInMetadata)
                    Console.WriteLine("Previously processed, but deleted from library ({0:N0}ms).", sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error - {0}: {1}. See stderr for stack trace.", ex.GetType().Name, ex.Message);
                Console.Error.WriteLine("Error processing '{0}': {1}", fi.FullName, ex);

                // Undo any partial work.
                if (metadataForException != null && mrForException != null)
                    metadataForException.Remove(mrForException);
                if (destPathForException != null && File.Exists(destPathForException))
                    File.Delete(destPathForException);
            }
        }

        private class ProcessFileData
        {
            public FileInfo Source { get; set; }
            public string DestinationPath { get; set; }
            public PhotoSource SourceConfig { get; set; }
            public Config Config { get; set; }
            public DateTime FileDateLocal { get; set; }
            public Dictionary<string, Metadata> MetadataByDestinationFolder { get; set; }
        }
        private static Task ProcessJpg(ProcessFileData data)
        {
            // Add author and copyright EXIF.
            using (var img = Image.Load(data.Source.FullName))
            {
                img.MetaData.ExifProfile.SetValue(ExifTag.Copyright, $"Copyright (c) {data.SourceConfig.CopyrightTo}, {data.FileDateLocal.Year}. {data.SourceConfig.CopyrightLicenseShort}");
                img.MetaData.ExifProfile.SetValue(ExifTag.XPAuthor, Encoding.Unicode.GetBytes(data.SourceConfig.CopyrightTo));     // Encoded in UCS2 / Unicdoe.
                img.MetaData.ExifProfile.SetValue(ExifTag.XPComment, Encoding.Unicode.GetBytes($"Copyright (c) {data.SourceConfig.CopyrightTo}, {data.FileDateLocal.Year}. {data.SourceConfig.CopyrightLicenseFull}. {data.SourceConfig.CopyrightUrl}"));     // Encoded in UCS2 / Unicdoe.

                // Save file.
                img.Save(data.DestinationPath);
            }
            return Task.FromResult<object>(null);
        }

        private static async Task ProcessMp4(ProcessFileData data)
        {
            // Copy.
            await CopyFileAsync(data.Source.FullName, data.DestinationPath);

            // Then update metadata.
            using (var mp4 = TagLib.File.Create(data.DestinationPath))
            {
                mp4.Tag.Copyright = $"Copyright (c) {data.SourceConfig.CopyrightTo}, {data.FileDateLocal.Year}. {data.SourceConfig.CopyrightLicenseShort}";
                mp4.Tag.Comment = $"Copyright (c) {data.SourceConfig.CopyrightTo}, {data.FileDateLocal.Year}. {data.SourceConfig.CopyrightLicenseFull}. {data.SourceConfig.CopyrightUrl}";
                mp4.Save();
            }
        }

        private static async Task ProcessUnknown(ProcessFileData data)
        {
            // Just copy.
            await CopyFileAsync(data.Source.FullName, data.DestinationPath);
        }

        private static DateTimeOffset GetDateForFile(FileInfo fi, DateTimeKind assumeDatesAre)
        {
            DateTime? maybeDateTime = null;
            try
            {
                using (var maybeTagFile = TagLib.File.Create(fi.FullName))
                {
                    // We prefer EXIF datestamps over file.
                    if (maybeTagFile != null)
                    {
                        if (maybeTagFile.Tag is TagLib.Image.CombinedImageTag)
                        {
                            var exifTags = (TagLib.Image.CombinedImageTag)maybeTagFile.Tag;
                            if (!maybeDateTime.HasValue)
                                maybeDateTime = exifTags.Exif.DateTimeDigitized;
                            if (!maybeDateTime.HasValue)
                                maybeDateTime = exifTags.Exif.DateTimeOriginal;
                            if (!maybeDateTime.HasValue)
                                maybeDateTime = exifTags.Exif.DateTime;
                            if (!maybeDateTime.HasValue)
                                maybeDateTime = exifTags.DateTime;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // We'll fall back to the file date if any error happens.
            }

            // Change to UTC if configured.
            if (assumeDatesAre == DateTimeKind.Utc && maybeDateTime.HasValue)
                maybeDateTime = new DateTime(maybeDateTime.Value.Ticks, DateTimeKind.Utc);
            if (maybeDateTime.HasValue)
                return maybeDateTime.Value;

            // Using file date if no EXIF info available.
            return assumeDatesAre == DateTimeKind.Utc ? fi.LastWriteTimeUtc : fi.LastWriteTime;
        }

        // https://stackoverflow.com/a/35467471/117070
        private static async Task CopyFileAsync(string sourceFile, string destinationFile)
        {
            using (var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var destinationStream = new FileStream(destinationFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await sourceStream.CopyToAsync(destinationStream);
        }

        private static void EnsureFolderFor(string destinationPathAndFilename) 
            => Directory.CreateDirectory(Path.GetDirectoryName(destinationPathAndFilename));
    }
}

