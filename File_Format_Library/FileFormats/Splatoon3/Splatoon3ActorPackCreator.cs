using ByamlExt.Byaml;
using SARCExt;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Toolbox.Library;

namespace FirstPlugin
{
    internal class Splatoon3ActorPackTemplateInfo
    {
        public string SourcePath;
        public string SourceActorName;
        public int EntryCount;
        public readonly List<string> SubModelPaths = new List<string>();
    }

    internal class Splatoon3ActorPackCreateResult
    {
        public int EntryCount;
        public int RenamedEntryCount;
        public int UpdatedBymlCount;
        public int UpdatedStringCount;
        public int SubModelCount;
    }

    internal static class Splatoon3ActorPackCreator
    {
        public static Splatoon3ActorPackTemplateInfo ReadTemplateInfo(string sourcePath)
        {
            SarcData sarcData = ReadPack(sourcePath);
            Splatoon3ActorPackTemplateInfo info = new Splatoon3ActorPackTemplateInfo
            {
                SourcePath = sourcePath,
                SourceActorName = GetActorNameFromPackPath(sourcePath),
                EntryCount = sarcData.Files.Count,
            };

            foreach (var file in sarcData.Files)
            {
                if (!IsModelInfoFile(file.Key))
                    continue;

                BymlFileData data = LoadByml(file.Value);
                if (data.RootNode is IDictionary<string, dynamic> root &&
                    root.TryGetValue("SubModels", out dynamic subModels) &&
                    subModels is IEnumerable<dynamic>)
                {
                    foreach (dynamic subModel in subModels)
                    {
                        if (subModel is IDictionary<string, dynamic> subModelDictionary &&
                            subModelDictionary.TryGetValue("Fmdb", out dynamic fmdb) &&
                            fmdb is string fmdbPath &&
                            !string.IsNullOrWhiteSpace(fmdbPath))
                            info.SubModelPaths.Add(fmdbPath);
                    }
                }
            }

            return info;
        }

        public static Splatoon3ActorPackCreateResult Create(string sourcePath, string outputPath, string targetActorName, List<string> subModelPaths)
        {
            return Create(sourcePath, outputPath, targetActorName, targetActorName, subModelPaths);
        }

        public static Splatoon3ActorPackCreateResult Create(string sourcePath, string outputPath, string targetActorName, string targetModelFileName, List<string> subModelPaths)
        {
            if (string.IsNullOrWhiteSpace(targetActorName))
                throw new InvalidOperationException("Enter a target actor name.");
            if (string.IsNullOrWhiteSpace(targetModelFileName))
                targetModelFileName = targetActorName;

            SarcData sourcePack = ReadPack(sourcePath);
            if (sourcePack.HashOnly)
                throw new InvalidOperationException("Hash-only actor packs cannot be renamed safely.");

            string sourceActorName = GetActorNameFromPackPath(sourcePath);
            List<string> sourceTokens = GetActorTokens(sourceActorName);
            List<string> targetTokens = GetActorTokens(targetActorName);
            Dictionary<string, byte[]> outputFiles = new Dictionary<string, byte[]>();
            Splatoon3ActorPackCreateResult result = new Splatoon3ActorPackCreateResult
            {
                EntryCount = sourcePack.Files.Count,
                SubModelCount = subModelPaths?.Count ?? 0,
            };

            foreach (var file in sourcePack.Files)
            {
                string newFileName = ReplaceActorReferences(file.Key, sourceTokens, targetTokens, ref result.UpdatedStringCount);
                if (newFileName != file.Key)
                    result.RenamedEntryCount++;
                if (outputFiles.ContainsKey(newFileName))
                    throw new InvalidOperationException($"The renamed actor pack would contain a duplicate file:\n{newFileName}");

                byte[] fileData = file.Value;
                if (IsBymlFile(file.Key))
                {
                    BymlFileData byml = LoadByml(fileData);
                    int beforeCount = result.UpdatedStringCount;
                    byml.RootNode = ReplaceNodeActorReferences(byml.RootNode, sourceTokens, targetTokens, ref result.UpdatedStringCount);
                    if (IsModelInfoFile(file.Key))
                        ApplyModelInfo(byml, targetActorName, targetModelFileName, subModelPaths ?? new List<string>());
                    if (result.UpdatedStringCount != beforeCount || IsModelInfoFile(file.Key))
                    {
                        fileData = SaveByml(byml);
                        result.UpdatedBymlCount++;
                    }
                }

                outputFiles.Add(newFileName, fileData);
            }

            sourcePack.Files.Clear();
            foreach (var file in outputFiles)
                sourcePack.Files.Add(file.Key, file.Value);

            Tuple<int, byte[]> packed = SARCExt.SARC.PackN(sourcePack);
            byte[] compressed = Zstb.SCompress(packed.Item2, 19);
            File.WriteAllBytes(outputPath, compressed);
            ValidateOutput(outputPath, outputFiles.Count);
            return result;
        }

        public static List<string> ConvertSubModelPaths(List<string> paths, string sourceActorName, string targetActorName)
        {
            List<string> sourceTokens = GetActorTokens(sourceActorName);
            List<string> targetTokens = GetActorTokens(targetActorName);
            return paths.Select(path =>
            {
                int ignored = 0;
                return ReplaceActorReferences(path, sourceTokens, targetTokens, ref ignored);
            }).ToList();
        }

        public static string GetDefaultFmdbPath(string actorName, string modelName)
        {
            return $"Work/Model/Field/VSGame/{actorName}/output/{modelName}.fmdb";
        }

        private static SarcData ReadPack(string sourcePath)
        {
            byte[] data = File.ReadAllBytes(sourcePath);
            if (data.Length < 4 || Encoding.ASCII.GetString(data, 0, 4) != "SARC")
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

        private static void ValidateOutput(string outputPath, int expectedEntryCount)
        {
            SarcData outputPack = ReadPack(outputPath);
            if (outputPack.Files.Count != expectedEntryCount)
                throw new InvalidOperationException($"The saved actor pack has {outputPack.Files.Count} files, expected {expectedEntryCount}.");
        }

        private static string GetActorNameFromPackPath(string sourcePath)
        {
            string name = Path.GetFileName(sourcePath);
            if (name.EndsWith(".zs", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name);
            if (name.EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name);
            return name;
        }

        private static List<string> GetActorTokens(string actorName)
        {
            List<string> tokens = new List<string>();
            if (!string.IsNullOrWhiteSpace(actorName))
            {
                tokens.Add(actorName);
                int index = actorName.IndexOf('_');
                if (index >= 0 && index + 1 < actorName.Length)
                    tokens.Add(actorName.Substring(index + 1));
            }
            return tokens.Distinct(StringComparer.Ordinal).OrderByDescending(token => token.Length).ToList();
        }

        private static string ReplaceActorReferences(string value, List<string> sourceTokens, List<string> targetTokens, ref int replacementCount)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            string result = value;
            for (int i = 0; i < sourceTokens.Count && i < targetTokens.Count; i++)
            {
                string before = result;
                result = result.Replace(sourceTokens[i], targetTokens[i]);
                if (result != before)
                    replacementCount++;
            }
            return result;
        }

        private static dynamic ReplaceNodeActorReferences(dynamic node, List<string> sourceTokens, List<string> targetTokens, ref int replacementCount)
        {
            if (node is string stringValue)
                return ReplaceActorReferences(stringValue, sourceTokens, targetTokens, ref replacementCount);

            if (node is IDictionary<string, dynamic> dictionary)
            {
                Dictionary<string, dynamic> replaced = new Dictionary<string, dynamic>();
                foreach (var item in dictionary)
                {
                    string key = ReplaceActorReferences(item.Key, sourceTokens, targetTokens, ref replacementCount);
                    replaced[key] = ReplaceNodeActorReferences(item.Value, sourceTokens, targetTokens, ref replacementCount);
                }
                return replaced;
            }

            if (node is IList<dynamic> list)
            {
                List<dynamic> replaced = new List<dynamic>();
                foreach (dynamic item in list)
                    replaced.Add(ReplaceNodeActorReferences(item, sourceTokens, targetTokens, ref replacementCount));
                return replaced;
            }

            return node;
        }

        private static void ApplyModelInfo(BymlFileData byml, string targetActorName, string targetModelFileName, List<string> subModelPaths)
        {
            if (!(byml.RootNode is IDictionary<string, dynamic> root))
                root = new Dictionary<string, dynamic>();

            root["Fmdb"] = GetDefaultFmdbPath(targetModelFileName, targetActorName);
            List<dynamic> subModels = new List<dynamic>();
            foreach (string path in subModelPaths.Select(NormalizeSubModelPath).Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                Dictionary<string, dynamic> subModel = new Dictionary<string, dynamic>();
                subModel["Fmdb"] = path;
                subModels.Add(subModel);
            }
            root["SubModels"] = subModels;
            byml.RootNode = root;
        }

        private static string NormalizeSubModelPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string value = path.Trim().Replace('\\', '/');
            if (value.StartsWith("Work/", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".fmdb", StringComparison.OrdinalIgnoreCase))
                return value;

            string actorName = GetActorNameFromModelName(value);
            return GetDefaultFmdbPath(actorName, value);
        }

        private static string GetActorNameFromModelName(string modelName)
        {
            if (modelName.StartsWith("FldBG_", StringComparison.OrdinalIgnoreCase))
                return "Fld_" + modelName.Substring("FldBG_".Length);
            if (modelName.StartsWith("FldObj_", StringComparison.OrdinalIgnoreCase))
            {
                string rest = modelName.Substring("FldObj_".Length);
                int index = rest.IndexOf('_');
                if (index > 0)
                    return "Fld_" + rest.Substring(0, index);
                return "Fld_" + rest;
            }
            return modelName;
        }

        private static bool IsBymlFile(string fileName)
        {
            return fileName.EndsWith(".bgyml", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".byml", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".gyml", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsModelInfoFile(string fileName)
        {
            return fileName.IndexOf("/ModelInfo/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   fileName.EndsWith("engine__component__ModelInfo.bgyml", StringComparison.OrdinalIgnoreCase);
        }

        private static BymlFileData LoadByml(byte[] data)
        {
            return ByamlFile.LoadN(new MemoryStream(data));
        }

        private static byte[] SaveByml(BymlFileData data)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                ByamlFile.SaveN(stream, data);
                return stream.ToArray();
            }
        }
    }
}
