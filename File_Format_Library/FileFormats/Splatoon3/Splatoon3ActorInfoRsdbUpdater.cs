using ByamlExt.Byaml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Toolbox.Library;

namespace FirstPlugin
{
    internal class Splatoon3ActorInfoRsdbUpdateResult
    {
        public readonly List<string> RowIds = new List<string>();
        public int PreviousCount;
        public int NewCount;
    }

    internal class Splatoon3ActorInfoRsdbEntry
    {
        public string RowId;
        public string ModelFile;
        public string ModelName;
    }

    internal static class Splatoon3ActorInfoRsdbUpdater
    {
        public static Splatoon3ActorInfoRsdbUpdateResult AddActor(string filePath, string actorName)
        {
            return AddActors(filePath, new List<Splatoon3ActorInfoRsdbEntry>
            {
                CreateDefaultEntry(actorName),
            });
        }

        public static Splatoon3ActorInfoRsdbEntry CreateDefaultEntry(string actorName)
        {
            string name = actorName?.Trim() ?? "";
            return new Splatoon3ActorInfoRsdbEntry
            {
                RowId = name,
                ModelFile = name,
                ModelName = name,
            };
        }

        public static Splatoon3ActorInfoRsdbUpdateResult AddActors(string filePath, List<Splatoon3ActorInfoRsdbEntry> actors)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new InvalidOperationException("Select a valid ActorInfo RSDB file.");
            if (actors == null || actors.Count == 0)
                throw new InvalidOperationException("Add at least one actor entry.");

            List<Splatoon3ActorInfoRsdbEntry> normalizedActors = actors
                .Select(NormalizeEntry)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.RowId))
                .ToList();
            if (normalizedActors.Count == 0)
                throw new InvalidOperationException("Add at least one actor entry.");

            List<string> duplicateInput = normalizedActors
                .GroupBy(entry => entry.RowId, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicateInput.Count > 0)
                throw new InvalidOperationException($"Duplicate new actor entries:\n{string.Join("\n", duplicateInput)}");

            byte[] input = File.ReadAllBytes(filePath);
            byte[] bymlData = DecompressIfNeeded(input, filePath);
            BymlFileData byml = ByamlFile.LoadN(new MemoryStream(bymlData));
            if (!(byml.RootNode is IList<dynamic> entries))
                throw new InvalidOperationException("The selected RSDB root is not an actor entry list.");

            HashSet<string> existingRowIds = new HashSet<string>(
                entries.OfType<IDictionary<string, dynamic>>()
                    .Where(entry => entry.TryGetValue("__RowId", out dynamic value) && value is string)
                    .Select(entry => (string)entry["__RowId"]),
                StringComparer.Ordinal);
            List<string> duplicateExisting = normalizedActors
                .Where(entry => existingRowIds.Contains(entry.RowId))
                .Select(entry => entry.RowId)
                .ToList();
            if (duplicateExisting.Count > 0)
                throw new InvalidOperationException($"The selected RSDB already contains:\n{string.Join("\n", duplicateExisting)}");

            int previousCount = entries.Count;
            foreach (Splatoon3ActorInfoRsdbEntry actor in normalizedActors)
                entries.Add(CreateActorEntry(actor));
            byml.RootNode = entries;

            byte[] savedByml;
            using (MemoryStream output = new MemoryStream())
            {
                ByamlFile.SaveN(output, byml);
                savedByml = output.ToArray();
            }

            byte[] saved = IsCompressed(input) || filePath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)
                ? Zstb.SCompress(savedByml, 19)
                : savedByml;
            File.WriteAllBytes(filePath, saved);

            Validate(filePath, normalizedActors.Select(actor => actor.RowId).ToList(), previousCount + normalizedActors.Count);

            Splatoon3ActorInfoRsdbUpdateResult result = new Splatoon3ActorInfoRsdbUpdateResult
            {
                PreviousCount = previousCount,
                NewCount = previousCount + normalizedActors.Count,
            };
            result.RowIds.AddRange(normalizedActors.Select(actor => actor.RowId));
            return result;
        }

        private static Splatoon3ActorInfoRsdbEntry NormalizeEntry(Splatoon3ActorInfoRsdbEntry entry)
        {
            string rowId = entry?.RowId?.Trim() ?? "";
            string modelFile = entry?.ModelFile?.Trim() ?? "";
            string modelName = entry?.ModelName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(modelFile))
                modelFile = rowId;
            if (string.IsNullOrWhiteSpace(modelName))
                modelName = rowId;

            return new Splatoon3ActorInfoRsdbEntry
            {
                RowId = rowId,
                ModelFile = modelFile,
                ModelName = modelName,
            };
        }

        private static Dictionary<string, dynamic> CreateActorEntry(Splatoon3ActorInfoRsdbEntry actor)
        {
            return new Dictionary<string, dynamic>
            {
                { "CalcPriority", "Before" },
                { "Category", "Field" },
                { "ClassName", "SplActor" },
                { "Fmdb", $"Work/Model/Field/VSGame/{actor.ModelFile}/output/{actor.ModelName}.fmdb" },
                { "InstanceHeapSize", 2238128 },
                { "IsCalcNodePushBack", true },
                { "IsFarActor", false },
                { "ModelAabbMax", CreateVector(986.36170f, 178.75000f, 631.97090f) },
                { "ModelAabbMin", CreateVector(-908.47130f, -33.77605f, -939.84310f) },
                { "__RowId", actor.RowId },
            };
        }

        private static Dictionary<string, dynamic> CreateVector(float x, float y, float z)
        {
            return new Dictionary<string, dynamic>
            {
                { "X", x },
                { "Y", y },
                { "Z", z },
            };
        }

        private static byte[] DecompressIfNeeded(byte[] input, string filePath)
        {
            if (!IsCompressed(input))
                return input;

            Zstb compression = new Zstb();
            compression.Init(filePath);
            using (MemoryStream inputStream = new MemoryStream(input))
            using (Stream output = compression.Decompress(inputStream))
            using (MemoryStream memory = new MemoryStream())
            {
                output.CopyTo(memory);
                return memory.ToArray();
            }
        }

        private static bool IsCompressed(byte[] input)
        {
            return input != null &&
                   input.Length >= 4 &&
                   input[0] == 0x28 &&
                   input[1] == 0xB5 &&
                   input[2] == 0x2F &&
                   input[3] == 0xFD;
        }

        private static void Validate(string filePath, List<string> rowIds, int expectedCount)
        {
            byte[] input = File.ReadAllBytes(filePath);
            byte[] bymlData = DecompressIfNeeded(input, filePath);
            BymlFileData byml = ByamlFile.LoadN(new MemoryStream(bymlData));
            if (!(byml.RootNode is IList<dynamic> entries))
                throw new InvalidOperationException("The saved RSDB root is not an actor entry list.");
            if (entries.Count != expectedCount)
                throw new InvalidOperationException($"The saved RSDB has {entries.Count} entries, expected {expectedCount}.");

            HashSet<string> savedRowIds = new HashSet<string>(
                entries.OfType<IDictionary<string, dynamic>>()
                    .Where(entry => entry.TryGetValue("__RowId", out dynamic value) && value is string)
                    .Select(entry => (string)entry["__RowId"]),
                StringComparer.Ordinal);
            List<string> missing = rowIds.Where(rowId => !savedRowIds.Contains(rowId)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"The saved RSDB does not contain:\n{string.Join("\n", missing)}");
        }
    }
}
