using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Diagnostics;

using Microsoft.Extensions.Configuration;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace MurrayGrant.IncomingToLibrary
{
    class Program
    {
        private static CancellationTokenSource CancellationTokenSource;
        private const double OneMB = 1024.0 * 1024.0;

        public static async Task Main(string[] args)
        {
            Console.WriteLine("Murray Grant - PostProcessPhotos - IncomingToLibrary");
            CancellationTokenSource = new CancellationTokenSource();
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
                    var fileData = new ProcessFileData()
                    {
                        Source = fi,
                        SourceConfig = s,
                        Config = config,
                        MetadataByDestinationFolder = metadataByPath,
                    };

                    // Process file strategy based on file type.
                    if (String.Equals(fi.Extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                        || String.Equals(fi.Extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
                       fileData.ProcessFile = ProcessJpg;
                    else if (String.Equals(fi.Extension, ".dng", StringComparison.OrdinalIgnoreCase))
                        fileData.ProcessFile = ProcessDng;
                    else if (String.Equals(fi.Extension, ".mp4", StringComparison.OrdinalIgnoreCase))
                        fileData.ProcessFile = ProcessMp4;
                    else
                        fileData.ProcessFile = ProcessUnknown;

                    // Some file types end up with a different extension, required for determining if the file has already been processed.
                    if (String.Equals(fi.Extension, ".dng", StringComparison.OrdinalIgnoreCase)
                        && !String.IsNullOrEmpty(config.PathTo7Zip))
                    {
                        fileData.DestinationExtension = "7z";
                    }
                    else if (String.Equals(fi.Extension, ".mp4", StringComparison.OrdinalIgnoreCase)
                        && config.TranscodeVideos
                        && !String.IsNullOrEmpty(config.PathToFfmpeg))
                    {
                        fileData.DestinationExtension = "mkv";
                    }

                    // Process File!
                    await ProcessFile(fileData);
                    if (CancellationTokenSource.IsCancellationRequested)
                        break;
                }

                Console.WriteLine("Finished processing incoming photos from: {0}, in {1:N2} seconds.", s.SourcePath, sw.Elapsed.TotalSeconds);
                if (CancellationTokenSource.IsCancellationRequested)
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
            CancellationTokenSource.Cancel();
            e.Cancel = true;        // Co-operative shutdown rather than immediate.
        }

        private static bool IsTempFile(FileInfo fi)
            => fi.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase);

        private static async Task ProcessFile(ProcessFileData fileData)
        {
            Console.Write(fileData.Source.Name + ": ");
            Metadata metadataForException = null;
            MetadataRecord mrForException = null;
            string destPathForException = null;

            try
            {
                var sw = Stopwatch.StartNew();
                // Read date for file.
                var imgDate = GetDateForFile(fileData.Source, fileData.SourceConfig.FilesInLocalOrUtc.GetValueOrDefault(DateTimeKind.Local));
                fileData.FileDateLocal = imgDate.LocalDateTime;

                // Check if the date means we'll import it.
                if (fileData.FileDateLocal < fileData.Config.EffectiveFromLocal)
                {
                    Console.WriteLine("Older than {0:yyyy-MM-dd}, not processing ({1:N0}ms).", fileData.Config.EffectiveFromLocal, sw.Elapsed.TotalMilliseconds);
                    return;
                }

                // Determine destination folder and filename.
                var destFolder = Path.Combine(fileData.Config.DestinationPath, fileData.FileDateLocal.ToString(fileData.Config.DestinationSubFolderPattern));
                var destName = fileData.SourceConfig.FilenamePrefix + fileData.Source.Name;
                if (!String.IsNullOrEmpty(fileData.DestinationExtension))
                    destName = Path.ChangeExtension(destName, fileData.DestinationExtension);
                fileData.DestinationPath = Path.Combine(destFolder, destName);
                destPathForException = fileData.DestinationPath;

                // Preload the destination metadata.
                if (!fileData.MetadataByDestinationFolder.TryGetValue(destFolder, out var metadata))
                {
                    metadata = await Metadata.LoadFromFile(Path.Combine(destFolder, fileData.Config.MetadataFilename));
                    fileData.MetadataByDestinationFolder.Add(destFolder, metadata);
                }
                metadataForException = metadata;

                // Check if file is already there (via metadata, or on disk).
                var destFi = new FileInfo(fileData.DestinationPath);
                var destLength = destFi.Exists ? destFi.Length : 0L;
                var alreadyExistsOnDisk = destFi.Exists && destLength > 1024;
                var (mr, alreadyExistsInMetadata) = metadata.GetOrAddFile(
                    destName, 
                    () => new MetadataRecord() { 
                        DestinationFilename = destName, 
                        SourceFilename = fileData.Source.Name, 
                        OriginalLength = fileData.Source.Length, 
                        Prefix = fileData.SourceConfig.FilenamePrefix, 
                        ProcessingDatestamp = DateTimeOffset.Now 
                    }
                );
                var previousProcessingError = alreadyExistsOnDisk && alreadyExistsInMetadata && destLength <= 1024 && mr.OriginalLength > 1024;
                mrForException = mr;

                if (previousProcessingError || (!alreadyExistsOnDisk && !alreadyExistsInMetadata))
                {
                    if (previousProcessingError)
                        Console.Write("Error during previous processing run - attempting to reprocess: ");

                    EnsureFolderFor(fileData.DestinationPath);
                    if (File.Exists(fileData.DestinationPath))
                        File.Delete(fileData.DestinationPath);

                    var processResult = await fileData.ProcessFile(fileData);

                    sw.Stop();
                    Console.WriteLine("Processed OK ({0:N0}ms). Source {1:N2}MB, Dest {2:N2}MB.", sw.Elapsed.TotalMilliseconds, fileData.Source.Length / OneMB, processResult.Destination.Length / OneMB);
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
                Console.Error.WriteLine("Error processing '{0}': {1}", fileData.Source.FullName, ex);

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
            public PhotoSource SourceConfig { get; set; }
            public Config Config { get; set; }
            public Dictionary<string, Metadata> MetadataByDestinationFolder { get; set; }
            public string DestinationExtension { get; set; }

            public DateTime FileDateLocal { get; set; }
            public string DestinationPath { get; set; }

            public Func<ProcessFileData, Task<ProcessFileResult>> ProcessFile { get; set; }
        }
        private class ProcessFileResult
        {
            public FileInfo Destination { get; set; }
        }

        private static async Task<ProcessFileResult> ProcessJpg(ProcessFileData data)
        {
            // Add author and copyright EXIF.
            using (var img = Image.Load(data.Source.FullName))
            {
                img.Metadata.ExifProfile.SetValue(ExifTag.Copyright, $"Copyright (c) {data.SourceConfig.CopyrightTo}, {data.FileDateLocal.Year}. {data.SourceConfig.CopyrightLicenseShort}");
                img.Metadata.ExifProfile.SetValue(ExifTag.XPAuthor, data.SourceConfig.CopyrightTo);
                img.Metadata.ExifProfile.SetValue(ExifTag.XPComment, $"Copyright (c) {data.SourceConfig.CopyrightTo}, {data.FileDateLocal.Year}. {data.SourceConfig.CopyrightLicenseFull}. {data.SourceConfig.CopyrightUrl}");

                // Save file.
                await img.SaveAsync(data.DestinationPath);
            }

            return new ProcessFileResult() 
            { 
                Destination = new FileInfo(data.DestinationPath) 
            };
        }

        private static async Task<ProcessFileResult> ProcessMp4(ProcessFileData data)
        {
            if (data.Config.TranscodeVideos)
            {
                // Use ffmpeg to transcode to AV1
                var args = new[]
                {
                    "-hwaccel", "auto",
                    "-i", data.Source.FullName,
                    "-y",
                    "-acodec", data.Config.TranscodeAudioCodec,
                    "-b:a", data.Config.TranscodeAudioBitrate,
                    "-vcodec", data.Config.TranscodeVideoCodec,
                    "-crf", data.Config.TranscodeVideoQualityFactor,
                    "-preset", data.Config.TranscodeVideoCpuFactor,
                    "-g", data.Config.TranscodeVideoKeyframeFactor,
                    data.DestinationPath,
                };
                await ExecAsync(data.Config.PathToFfmpeg, args);
            }
            else
            {
                // Streight copy when not transacoding.
                await CopyFileAsync(data.Source.FullName, data.DestinationPath);
            }

            // Then update metadata.
            using (var video = TagLib.File.Create(data.DestinationPath))
            {
                video.Tag.Copyright = $"Copyright (c) {data.SourceConfig.CopyrightTo}, {data.FileDateLocal.Year}. {data.SourceConfig.CopyrightLicenseShort}";
                video.Tag.Comment = $"Copyright (c) {data.SourceConfig.CopyrightTo}, {data.FileDateLocal.Year}. {data.SourceConfig.CopyrightLicenseFull}. {data.SourceConfig.CopyrightUrl}";
                video.Save();
            }

            return new ProcessFileResult()
            {
                Destination = new FileInfo(data.DestinationPath)
            };
        }

        private static async Task<ProcessFileResult> ProcessDng(ProcessFileData data)
        {
            // Compress the file with 7zip.
            var args = new[]
            {
                "a",
                "-mx5",
                data.DestinationPath,
                data.Source.FullName,
            };
            await ExecAsync(data.Config.PathTo7Zip, args);
            return new ProcessFileResult()
            {
                Destination = new FileInfo(data.DestinationPath)
            };
        }

        private static async Task<ProcessFileResult> ProcessUnknown(ProcessFileData data)
        {
            // Just copy.
            await CopyFileAsync(data.Source.FullName, data.DestinationPath);
            return new ProcessFileResult()
            {
                Destination = new FileInfo(data.DestinationPath)
            };
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

        private static string Identity(string s) => s;

        // https://stackoverflow.com/a/35467471/117070
        private static async Task CopyFileAsync(string sourceFile, string destinationFile)
        {
            using (var sourceStream = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var destinationStream = new FileStream(destinationFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await sourceStream.CopyToAsync(destinationStream);
        }

        private static async Task ExecAsync(string pathToExe, IEnumerable<string> args, int failureErrorCode = 1)
        {
            using (var p = new Process())
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();

                p.StartInfo = new ProcessStartInfo()
                {
                    FileName = pathToExe,
                    CreateNoWindow = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var arg in args)
                    p.StartInfo.ArgumentList.Add(arg);
                p.ErrorDataReceived += (sender, e) => DataReceived(stderr, e);
                p.OutputDataReceived += (sender, e) => DataReceived(stdout, e);
                p.Start();
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                p.PriorityClass = ProcessPriorityClass.BelowNormal;

                await p.WaitForExitAsync(CancellationTokenSource.Token);
                if (p.ExitCode >= failureErrorCode)
                    throw new Exception($"Exec of '{pathToExe} {string.Join(" ", args)}' failed with error code {p.ExitCode}. \n\nStdout: {stdout}\n\nStderr:\n\n{stderr}");

                void DataReceived(StringBuilder sb, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                        sb.AppendLine(e.Data);
                }
            }
        }

        private static void EnsureFolderFor(string destinationPathAndFilename) 
            => Directory.CreateDirectory(Path.GetDirectoryName(destinationPathAndFilename));

    }
}

