using SARCExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Toolbox.Library;

namespace FirstPlugin
{
    internal class Splatoon3CollisionReplaceEntry
    {
        public string PackPath;
        public string CollisionPath;
    }

    internal class Splatoon3CollisionReplaceItemResult
    {
        public string PackPath;
        public int ReplacedCount;
        public readonly List<string> ReplacedFiles = new List<string>();
    }

    internal class Splatoon3CollisionReplaceResult
    {
        public readonly List<Splatoon3CollisionReplaceItemResult> Items = new List<Splatoon3CollisionReplaceItemResult>();
    }

    internal static class Splatoon3CollisionReplacer
    {
        public static Splatoon3CollisionReplaceResult Replace(List<Splatoon3CollisionReplaceEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                throw new InvalidOperationException("Add at least one actor pack.");

            Splatoon3CollisionReplaceResult result = new Splatoon3CollisionReplaceResult();
            foreach (Splatoon3CollisionReplaceEntry entry in entries)
                result.Items.Add(ReplaceSingle(entry));

            return result;
        }

        private static Splatoon3CollisionReplaceItemResult ReplaceSingle(Splatoon3CollisionReplaceEntry entry)
        {
            string packPath = entry?.PackPath?.Trim() ?? "";
            string collisionPath = entry?.CollisionPath?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(packPath) || !File.Exists(packPath))
                throw new InvalidOperationException("Select a valid actor pack.");
            if (string.IsNullOrWhiteSpace(collisionPath) || !File.Exists(collisionPath))
                throw new InvalidOperationException($"Select a valid .bphsh file for:\n{packPath}");
            if (!collisionPath.EndsWith(".bphsh", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The selected collision file is not a .bphsh:\n{collisionPath}");

            SarcData pack = ReadPack(packPath);
            if (pack.HashOnly)
                throw new InvalidOperationException($"Hash-only actor packs cannot be modified safely:\n{packPath}");

            List<string> targetFiles = pack.Files.Keys
                .Where(IsDccCollisionFile)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            if (targetFiles.Count == 0)
                throw new InvalidOperationException($"No .bphsh file was found under Phive/Shape/Dcc in:\n{packPath}");

            byte[] replacement = File.ReadAllBytes(collisionPath);
            foreach (string targetFile in targetFiles)
                pack.Files[targetFile] = replacement;

            Tuple<int, byte[]> packed = SARCExt.SARC.PackN(pack);
            byte[] compressed = packPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase) || !IsSarc(File.ReadAllBytes(packPath))
                ? Zstb.SCompress(packed.Item2, 19)
                : packed.Item2;
            File.WriteAllBytes(packPath, compressed);
            Validate(packPath, targetFiles.Count, replacement.Length);

            Splatoon3CollisionReplaceItemResult result = new Splatoon3CollisionReplaceItemResult
            {
                PackPath = packPath,
                ReplacedCount = targetFiles.Count,
            };
            result.ReplacedFiles.AddRange(targetFiles);
            return result;
        }

        private static bool IsDccCollisionFile(string fileName)
        {
            return fileName != null &&
                   fileName.StartsWith("Phive/Shape/Dcc/", StringComparison.OrdinalIgnoreCase) &&
                   fileName.EndsWith(".bphsh", StringComparison.OrdinalIgnoreCase);
        }

        private static SarcData ReadPack(string sourcePath)
        {
            byte[] data = File.ReadAllBytes(sourcePath);
            if (!IsSarc(data))
            {
                Zstb compression = new Zstb();
                compression.Init(sourcePath);
                using (MemoryStream input = new MemoryStream(data))
                using (Stream output = compression.Decompress(input))
                using (MemoryStream memory = new MemoryStream())
                {
                    output.CopyTo(memory);
                    data = memory.ToArray();
                }
            }

            return SARCExt.SARC.UnpackRamN(new MemoryStream(data));
        }

        private static bool IsSarc(byte[] data)
        {
            return data != null &&
                   data.Length >= 4 &&
                   Encoding.ASCII.GetString(data, 0, 4) == "SARC";
        }

        private static void Validate(string packPath, int expectedCount, int expectedLength)
        {
            SarcData pack = ReadPack(packPath);
            List<string> targetFiles = pack.Files.Keys.Where(IsDccCollisionFile).ToList();
            if (targetFiles.Count != expectedCount)
                throw new InvalidOperationException($"The saved actor pack has {targetFiles.Count} .bphsh files under Phive/Shape/Dcc, expected {expectedCount}.");
            foreach (string targetFile in targetFiles)
            {
                if (pack.Files[targetFile].Length != expectedLength)
                    throw new InvalidOperationException($"The saved collision size does not match for:\n{targetFile}");
            }
        }
    }
}
