using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace MurrayGrant.IncomingToLibrary
{
    public class Metadata
    {
        private List<MetadataRecord> _Photos = new List<MetadataRecord>();
        private Dictionary<string, MetadataRecord> _PhotosByName = new Dictionary<string, MetadataRecord>(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<MetadataRecord> Photos => _Photos.OrderBy(x => x.SourceFilename);

        public string PathAndFilename { get; set; }

        public Metadata() { }
        private Metadata(IEnumerable<MetadataRecord> records)
        {
            _Photos = new List<MetadataRecord>(records);
            _PhotosByName = records.ToDictionary(x => x.DestinationFilename, StringComparer.OrdinalIgnoreCase);
        }

        public void Add(MetadataRecord m) => _Photos.Add(m);
        public (MetadataRecord, bool) GetOrAddFile(string destinationName, Func<MetadataRecord> recordMaker)
        {
            if (_PhotosByName.TryGetValue(destinationName, out var result))
                return (result, true);

            var mr = recordMaker();
            _Photos.Add(mr);
            _PhotosByName.Add(mr.DestinationFilename, mr);
            return (mr, false);
        }

        public void Remove(MetadataRecord mr)
        {
            _Photos.Remove(mr);
            _PhotosByName.Remove(mr.DestinationFilename);
        }

        public static async Task<Metadata> LoadFromFile(string path)
        {
            if (!File.Exists(path))
                return new Metadata() { PathAndFilename = path };
            var content = await File.ReadAllTextAsync(path, Encoding.UTF8);
            var records = JsonSerializer.Deserialize<List<MetadataRecord>>(content);
            var result = new Metadata(records);
            result.PathAndFilename = path;
            return result;
        }
        public async Task SaveToFile()
        {
            var content = JsonSerializer.Serialize(this.Photos.ToList(), options: new JsonSerializerOptions() { WriteIndented = true });
            var tmpPath = this.PathAndFilename + ".tmp";
            var tmpPath2 = this.PathAndFilename + ".tmp2";
            await File.WriteAllTextAsync(tmpPath, content, Encoding.UTF8);
            if (File.Exists(PathAndFilename))
                File.Move(PathAndFilename, tmpPath2);
            File.Move(tmpPath, PathAndFilename);
            if (File.Exists(tmpPath2))
                File.Delete(tmpPath2);
        }
    }
    public sealed class MetadataRecord
    {
        public string DestinationFilename { get; set; }
        public string SourceFilename { get; set; }
        public string Prefix { get; set; }
        public long OriginalLength { get; set; }
        public DateTimeOffset? ProcessingDatestamp { get; set; }

        public override string ToString() => DestinationFilename;
    }
}
