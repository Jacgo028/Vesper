using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Vesper.Packaging
{
    public sealed class BundleEntry
    {
        public string Path { get; set; } = "";
        public string RawPath { get; set; } = "";
        public long Offset { get; set; }
        public long Size { get; set; }
        public byte Type { get; set; }
        public string Sha256 { get; set; } = "";
    }
    public sealed class BundleInventory
    {
        public string ExeSha256 { get; set; } = "";
        public long HeaderOffset { get; set; }
        public List<BundleEntry> Entries { get; set; } = new List<BundleEntry>();
        // Deliberately limited to the SDK 8.0.425 Windows x64, 6.0 uncompressed format.
        // Source: dotnet/runtime 1219a42122cf5190c7f512850557e38c422430dc HostModel/Bundle.
        public static BundleInventory Read(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true), true))
                {
                    Require(stream.Length >= 256 && stream.Length <= 2L * 1024 * 1024 * 1024, "Input size");
                    Require(reader.ReadUInt16() == 0x5a4d, "DOS signature");
                    stream.Position = 0x3c;
                    var pe = reader.ReadInt32();
                    Require(pe >= 64 && pe <= stream.Length - 26, "PE offset");
                    stream.Position = pe;
                    Require(reader.ReadUInt32() == 0x4550 && reader.ReadUInt16() == 0x8664, "Windows x64 PE required");
                    stream.Position = pe + 24;
                    Require(reader.ReadUInt16() == 0x20b, "PE32+ required");
                    var marker = FindMarker(stream);
                    Require(marker >= 8, "Bundle marker missing");
                    stream.Position = marker - 8;
                    var header = reader.ReadInt64();
                    Require(header > marker + 32 && header <= stream.Length - 56, "Bundle header bounds");
                    stream.Position = header;
                    Require(reader.ReadUInt32() == 6 && reader.ReadUInt32() == 0, "Only bundle 6.0 supported");
                    var count = reader.ReadInt32();
                    Require(count > 0 && count <= 10000, "Entry count");
                    ReadString(reader);
                    var depsOffset = reader.ReadInt64(); var depsSize = reader.ReadInt64();
                    var configOffset = reader.ReadInt64(); var configSize = reader.ReadInt64();
                    Require((reader.ReadUInt64() & ~1UL) == 0, "Unknown flags");
                    var result = new BundleInventory { HeaderOffset = header };
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < count; i++)
                    {
                        var entry = new BundleEntry { Offset = reader.ReadInt64(), Size = reader.ReadInt64() };
                        Require(reader.ReadInt64() == 0, "Compressed entries unsupported");
                        entry.Type = reader.ReadByte();
                        Require(entry.Type <= 5, "Unknown entry type");
                        entry.RawPath = ReadString(reader);
                        entry.Path = NormalizePath(entry.RawPath);
                        Require(names.Add(entry.Path), "Duplicate Windows path");
                        Require(entry.Offset > marker + 32 && entry.Size >= 0 && entry.Offset <= header && entry.Size <= header - entry.Offset, "Payload range");
                        result.Entries.Add(entry);
                        Require(stream.Position - header <= 4 * 1024 * 1024, "Manifest too large");
                    }
                    long end = 0;
                    foreach (var entry in result.Entries.OrderBy(e => e.Offset))
                    {
                        Require(entry.Offset >= end, "Overlapping payloads");
                        end = entry.Offset + entry.Size;
                    }
                    CheckCached(result.Entries, 3, depsOffset, depsSize);
                    CheckCached(result.Entries, 4, configOffset, configSize);
                    foreach (var entry in result.Entries)
                        entry.Sha256 = HashRange(stream, entry.Offset, entry.Size);
                    result.ExeSha256 = HashRange(stream, 0, stream.Length);
                    return result;
                }
            }
            catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated bundle", ex); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid UTF-8", ex); }
        }

        public static string NormalizePath(string raw)
        {
            var path = raw.Replace('\\', '/');
            Require(path.Length > 0 && !path.StartsWith("/") && path.IndexOfAny(new[] { ':', '\0', '<', '>', '"', '|', '?', '*' }) < 0, "Unsafe path");
            foreach (var segment in path.Split('/'))
            {
                Require(segment.Length > 0 && segment != "." && segment != ".." && !segment.EndsWith(".") && !segment.EndsWith(" ") && !segment.Any(char.IsControl), "Path alias");
                Require(!Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "Reserved Windows name");
            }
            return path;
        }

        private static void CheckCached(List<BundleEntry> entries, byte type, long offset, long size)
        {
            var typed = entries.Where(e => e.Type == type).ToList();
            Require(typed.Count <= 1, "Duplicate JSON type");
            if (typed.Count == 0) Require(offset == 0 && size == 0, "Cached JSON absent");
            else Require(typed[0].Offset == offset && typed[0].Size == size, "Cached JSON mismatch");
        }

        private static string ReadString(BinaryReader reader)
        {
            var first = reader.ReadByte();
            var length = (int)first;
            if (first >= 128)
            {
                var second = reader.ReadByte();
                Require(second > 0 && second < 128, "Noncanonical string length");
                length = (first & 127) | (second << 7);
            }
            Require(length > 0 && length <= 260, "String length");
            var bytes = reader.ReadBytes(length);
            Require(bytes.Length == length, "Truncated string");
            var value = new UTF8Encoding(false, true).GetString(bytes);
            Require(value.IndexOf('\0') < 0, "NUL string");
            return value;
        }

        private static long FindMarker(Stream stream)
        {
            var marker = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
            // Streaming KMP keeps memory and scan time bounded even for adversarial input.
            var prefix = new int[marker.Length];
            for (int i = 1, j = 0; i < marker.Length; i++)
            { while (j > 0 && marker[i] != marker[j]) j = prefix[j - 1]; if (marker[i] == marker[j]) j++; prefix[i] = j; }
            stream.Position = 0;
            var buffer = new byte[65536]; long position = 0, found = -1; int matched = 0, read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                for (var i = 0; i < read; i++, position++)
                {
                    while (matched > 0 && buffer[i] != marker[matched]) matched = prefix[matched - 1];
                    if (buffer[i] == marker[matched]) matched++;
                    if (matched == marker.Length)
                    { Require(found < 0, "Ambiguous bundle marker"); found = position - marker.Length + 1; matched = prefix[matched - 1]; }
                }
            return found;
        }

        private static string HashRange(Stream stream, long offset, long length)
        {
            stream.Position = offset;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                while (length > 0)
                {
                    var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                    Require(read > 0, "Truncated payload"); hash.AppendData(buffer, 0, read); length -= read;
                }
                return Convert.ToHexString(hash.GetHashAndReset());
            }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
