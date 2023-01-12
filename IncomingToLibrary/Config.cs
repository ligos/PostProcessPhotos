using System;
using System.Collections.Generic;
using System.Text;

namespace MurrayGrant.IncomingToLibrary
{
    public class Config
    {
        public string DestinationPath { get; set; }
        public string DestinationSubFolderPattern { get; set; }

        public string MetadataFilename { get; set; }
        public DateTime EffectiveFromLocal { get; set; }

        public IEnumerable<PhotoSource> PhotoSources { get; set; }

        public string PathTo7Zip { get; set; }
        public string PathToFfmpeg { get; set; }

        public bool TranscodeVideos { get; set; }

        public string TranscodeAudioCodec { get; set; }
        public string TranscodeAudioBitrate { get; set; }

        public string TranscodeVideoCodec { get; set; }
        public string TranscodeVideoQualityFactor { get; set; }
        public string TranscodeVideoCpuFactor { get; set; }
        public string TranscodeVideoKeyframeFactor { get; set; }
    }

    public class PhotoSource
    {
        public string SourcePath { get; set; }
        public string FilenamePrefix { get; set; }

        public DateTimeKind? FilesInLocalOrUtc { get; set; }

        public string CopyrightTo { get; set; }
        public string CopyrightLicenseFull { get; set; }
        public string CopyrightLicenseShort { get; set; }
        public string CopyrightUrl { get; set; }
    }
}
