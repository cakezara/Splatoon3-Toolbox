using ByamlExt.Byaml;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Toolbox.Library;

namespace FirstPlugin
{
    internal class Splatoon3LayoutPortResult
    {
        public int SourceObjectCount;
        public int ActorCount;
        public int FieldObjectCount;
        public int RailCount;
        public readonly List<string> UsedMappings = new List<string>();
        public readonly List<string> MissingMappings = new List<string>();
    }

    internal class Splatoon3LayoutActorMapping
    {
        public string Splatoon2Name;
        public string Splatoon3Name;

        public Splatoon3LayoutActorMapping(string splatoon2Name, string splatoon3Name)
        {
            Splatoon2Name = splatoon2Name;
            Splatoon3Name = splatoon3Name;
        }
    }

    internal static class Splatoon3LayoutPorter
    {
        private static readonly Random Random = new Random();

        internal static readonly List<Splatoon3LayoutActorMapping> ActorMappings = new List<Splatoon3LayoutActorMapping>
        {
            new Splatoon3LayoutActorMapping("Brd_Pigeon00", "DObj_Pigeon00"),
            new Splatoon3LayoutActorMapping("GachihokoHikikomoriArea", "LocatorGachihokoHikikomoriArea"),
            new Splatoon3LayoutActorMapping("GachihokoHikikomoriArea2", "LocatorGachihokoHikikomoriArea2"),
            new Splatoon3LayoutActorMapping("GachihokoRouteArea", "LocatorGachihokoRouteArea"),
            new Splatoon3LayoutActorMapping("GachihokoTargetPoint", "LocatorGachihokoRouteTargetPoint"),
            new Splatoon3LayoutActorMapping("MapPaintableChanger", "ChangePaintableArea"),
            new Splatoon3LayoutActorMapping("Obj_Bunker01", "DObj_Bunker01"),
            new Splatoon3LayoutActorMapping("Obj_BunkerWall02", "DObj_BunkerWall02"),
            new Splatoon3LayoutActorMapping("Obj_InkRailVersus", "InkRailOnline"),
            new Splatoon3LayoutActorMapping("Obj_Jerry00", "NpcVsJerry_General_00"),
            new Splatoon3LayoutActorMapping("Obj_JerryClerk", "NpcVsJerry_General_00"),
            new Splatoon3LayoutActorMapping("Obj_SpongeVersus", "SpongeSmall_3p0_VS"),
            new Splatoon3LayoutActorMapping("Obj_Tree02", "Obj_Tree02"),
            new Splatoon3LayoutActorMapping("Obj_Tree03", "Obj_Tree03"),
            new Splatoon3LayoutActorMapping("Obj_Tree04", "Obj_Tree04"),
            new Splatoon3LayoutActorMapping("Obj_VictoryClamBankEmitArea", "LocatorGachiasariClamInitSpawnArea"),
            new Splatoon3LayoutActorMapping("Obj_VictoryClamBasket", "GachiasariGoal"),
            new Splatoon3LayoutActorMapping("Obj_VictoryClamSpawnPoint", "LocatorGachiasariClamSpawnPoint"),
            new Splatoon3LayoutActorMapping("Obj_VictoryLift", "Gachiyagura_2M"),
            new Splatoon3LayoutActorMapping("Obj_VictoryPoint", "GachihokoGoal"),
            new Splatoon3LayoutActorMapping("PaintTargetArea", "PaintTargetArea_Cube"),
            new Splatoon3LayoutActorMapping("RespawnPos", "LocatorVersusStart"),
        };

        private static readonly Dictionary<string, string> RailMappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Rail", "GachiyaguraRail" },
            { "Rail_VLift", "LiftRail" },
        };

        public static Splatoon3LayoutPortResult Port(string sourcePath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new InvalidOperationException("Select a valid Splatoon 2 layout BYAML.");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new InvalidOperationException("Select a valid output path.");

            byte[] data = DecompressIfNeeded(File.ReadAllBytes(sourcePath), sourcePath);
            BymlFileData sourceByml = ByamlFile.LoadN(new MemoryStream(data));
            IDictionary<string, dynamic> sourceRoot = sourceByml.RootNode as IDictionary<string, dynamic>;
            if (sourceRoot == null)
                throw new InvalidOperationException("The selected Splatoon 2 layout root is not a dictionary.");
            if (!sourceRoot.TryGetValue("Objs", out dynamic sourceObjsValue) || !(sourceObjsValue is IList<dynamic> sourceObjs))
                throw new InvalidOperationException("The selected Splatoon 2 layout does not contain an Objs list.");

            Splatoon3LayoutPortResult result = new Splatoon3LayoutPortResult();
            result.SourceObjectCount = sourceObjs.Count;

            Dictionary<string, Splatoon3LayoutActorMapping> mappings = ActorMappings
                .GroupBy(mapping => mapping.Splatoon2Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            List<dynamic> actors = new List<dynamic>();
            HashSet<string> usedMappings = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> missingMappings = new HashSet<string>(StringComparer.Ordinal);

            foreach (dynamic sourceObj in sourceObjs)
            {
                IDictionary<string, dynamic> source = sourceObj as IDictionary<string, dynamic>;
                if (source == null)
                    continue;

                string unitConfigName = GetString(source, "UnitConfigName");
                if (string.IsNullOrWhiteSpace(unitConfigName))
                    continue;
                if (IsPatchArea(source))
                    continue;
                if (IsFieldObject(unitConfigName))
                {
                    result.FieldObjectCount++;
                    actors.Add(ConvertActor(source, unitConfigName));
                    continue;
                }

                if (!mappings.TryGetValue(unitConfigName, out Splatoon3LayoutActorMapping mapping))
                    mapping = ResolveFallbackMapping(unitConfigName, missingMappings);

                usedMappings.Add(mapping.Splatoon2Name + " -> " + mapping.Splatoon3Name);
                actors.Add(ConvertActor(source, mapping.Splatoon3Name));
            }

            List<dynamic> rails = new List<dynamic>();
            if (sourceRoot.TryGetValue("Rails", out dynamic sourceRailsValue) && sourceRailsValue is IList<dynamic> sourceRails)
            {
                foreach (dynamic sourceRailObj in sourceRails)
                {
                    IDictionary<string, dynamic> sourceRail = sourceRailObj as IDictionary<string, dynamic>;
                    if (sourceRail != null)
                        rails.Add(ConvertRail(sourceRail));
                }
            }

            actors.RemoveAll(actor => IsPatchArea(actor as IDictionary<string, dynamic>));

            Dictionary<string, dynamic> root = new Dictionary<string, dynamic>
            {
                { "Actors", actors },
                { "FilePath", "Work/Banc/Scene/" + Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(outputPath)) + ".bcett.json" },
                { "Rails", rails },
            };

            sourceByml.RootNode = root;
            byte[] savedByml;
            using (MemoryStream output = new MemoryStream())
            {
                ByamlFile.SaveN(output, sourceByml);
                savedByml = output.ToArray();
            }

            File.WriteAllBytes(outputPath, outputPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase) ? Zstb.SCompress(savedByml, 19) : savedByml);
            Validate(outputPath, actors.Count, rails.Count);

            result.ActorCount = actors.Count;
            result.RailCount = rails.Count;
            result.UsedMappings.AddRange(usedMappings.OrderBy(item => item, StringComparer.Ordinal));
            result.MissingMappings.AddRange(missingMappings.OrderBy(item => item, StringComparer.Ordinal));
            return result;
        }

        private static Splatoon3LayoutActorMapping ResolveFallbackMapping(string unitConfigName, HashSet<string> missingMappings)
        {
            if (unitConfigName.StartsWith("Obj_Jerry", StringComparison.Ordinal))
                return new Splatoon3LayoutActorMapping(unitConfigName, "NpcVsJerry_General_00");

            missingMappings.Add(unitConfigName);
            return new Splatoon3LayoutActorMapping(unitConfigName, unitConfigName);
        }

        private static Dictionary<string, dynamic> ConvertActor(IDictionary<string, dynamic> source, string gyaml)
        {
            ulong hash = NextUInt64();
            Dictionary<string, dynamic> actor = new Dictionary<string, dynamic>
            {
                { "Gyaml", gyaml },
                { "Hash", hash },
                { "InstanceID", Guid.NewGuid().ToString() },
                { "Layer", GetString(source, "LayerConfigName") ?? "Cmn" },
                { "Name", gyaml },
                { "Phive", CreatePhive(hash) },
                { "SRTHash", NextUInt32() },
                { "TeamCmp", CreateTeam(source) },
            };

            if (source.TryGetValue("Translate", out dynamic translate))
                actor["Translate"] = ConvertVector(translate, 0.1f);
            if (source.TryGetValue("Rotate", out dynamic rotate))
                actor["Rotate"] = ConvertRotation(rotate);
            if (source.TryGetValue("Scale", out dynamic scale) && ScaleNeeded(scale))
                actor["Scale"] = ConvertVector(scale, 1.0f);

            return actor;
        }

        private static Dictionary<string, dynamic> ConvertRail(IDictionary<string, dynamic> source)
        {
            string unitConfigName = GetString(source, "UnitConfigName") ?? "Rail";
            string gyaml = RailMappings.TryGetValue(unitConfigName, out string mappedGyaml) ? mappedGyaml : unitConfigName;
            ulong hash = NextUInt64();
            Dictionary<string, dynamic> rail = new Dictionary<string, dynamic>
            {
                { "Gyaml", gyaml },
                { "Hash", hash },
                { "InstanceID", Guid.NewGuid().ToString() },
                { "IsClosed", GetBool(source, "IsClosed") },
                { "Layer", GetString(source, "LayerConfigName") ?? "Cmn" },
                { "Name", gyaml },
                { "Points", ConvertRailPoints(source) },
                { "Rotation", source.TryGetValue("Rotate", out dynamic rotate) ? ConvertRotation(rotate) : CreateVectorList(0, 0, 0) },
            };
            return rail;
        }

        private static List<dynamic> ConvertRailPoints(IDictionary<string, dynamic> source)
        {
            List<dynamic> points = new List<dynamic>();
            if (!source.TryGetValue("RailPoints", out dynamic railPointsValue) || !(railPointsValue is IList<dynamic> railPoints))
                return points;

            foreach (dynamic pointObj in railPoints)
            {
                IDictionary<string, dynamic> sourcePoint = pointObj as IDictionary<string, dynamic>;
                if (sourcePoint == null)
                    continue;

                Dictionary<string, dynamic> point = new Dictionary<string, dynamic>
                {
                    { "Hash", NextUInt64() },
                };
                if (sourcePoint.TryGetValue("Translate", out dynamic translate))
                    point["Translate"] = ConvertVector(translate, 0.1f);
                points.Add(point);
            }
            return points;
        }

        private static Dictionary<string, dynamic> CreatePhive(ulong hash)
        {
            return new Dictionary<string, dynamic>
            {
                { "Placement", new Dictionary<string, dynamic> { { "ID", hash } } },
            };
        }

        private static Dictionary<string, dynamic> CreateTeam(IDictionary<string, dynamic> source)
        {
            int team = GetInt(source, "Team");
            string teamName = team == 1 ? "Alpha" : team == 2 ? "Bravo" : "Neutral";
            return new Dictionary<string, dynamic>
            {
                { "Team", teamName },
            };
        }

        private static List<dynamic> ConvertVector(dynamic vector, float multiplier)
        {
            IDictionary<string, dynamic> dict = vector as IDictionary<string, dynamic>;
            if (dict == null)
                return CreateVectorList(0, 0, 0);

            return CreateVectorList(
                GetFloat(dict, "X") * multiplier,
                GetFloat(dict, "Y") * multiplier,
                GetFloat(dict, "Z") * multiplier);
        }

        private static List<dynamic> ConvertRotation(dynamic vector)
        {
            IDictionary<string, dynamic> dict = vector as IDictionary<string, dynamic>;
            if (dict == null)
                return CreateVectorList(0, 0, 0);

            return CreateVectorList(
                DegreesToRadians(GetFloat(dict, "X")),
                DegreesToRadians(GetFloat(dict, "Y")),
                DegreesToRadians(GetFloat(dict, "Z")));
        }

        private static List<dynamic> CreateVectorList(float x, float y, float z)
        {
            return new List<dynamic>
            {
                x,
                y,
                z,
            };
        }

        private static bool ScaleNeeded(dynamic scale)
        {
            IDictionary<string, dynamic> dict = scale as IDictionary<string, dynamic>;
            if (dict == null)
                return false;

            return Math.Abs(GetFloat(dict, "X") - 1.0f) > 0.0001f ||
                   Math.Abs(GetFloat(dict, "Y") - 1.0f) > 0.0001f ||
                   Math.Abs(GetFloat(dict, "Z") - 1.0f) > 0.0001f;
        }

        private static float DegreesToRadians(float value)
        {
            return (float)(value * Math.PI / 180.0);
        }

        private static bool IsFieldObject(string unitConfigName)
        {
            return unitConfigName.StartsWith("Fld_", StringComparison.OrdinalIgnoreCase) ||
                   unitConfigName.StartsWith("FldObj_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPatchArea(IDictionary<string, dynamic> actor)
        {
            if (actor == null)
                return false;

            return IsPatchAreaName(GetString(actor, "UnitConfigName")) ||
                   IsPatchAreaName(GetString(actor, "Gyaml")) ||
                   IsPatchAreaName(GetString(actor, "Name"));
        }

        private static bool IsPatchAreaName(string name)
        {
            return string.Equals((name ?? "").Trim(), "PatchArea", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetString(IDictionary<string, dynamic> dict, string key)
        {
            return dict.TryGetValue(key, out dynamic value) && value != null ? Convert.ToString(value) : null;
        }

        private static int GetInt(IDictionary<string, dynamic> dict, string key)
        {
            if (!dict.TryGetValue(key, out dynamic value) || value == null)
                return 0;
            return Convert.ToInt32(value);
        }

        private static bool GetBool(IDictionary<string, dynamic> dict, string key)
        {
            if (!dict.TryGetValue(key, out dynamic value) || value == null)
                return false;
            return Convert.ToBoolean(value);
        }

        private static float GetFloat(IDictionary<string, dynamic> dict, string key)
        {
            if (!dict.TryGetValue(key, out dynamic value) || value == null)
                return 0;
            return Convert.ToSingle(value);
        }

        private static ulong NextUInt64()
        {
            byte[] bytes = new byte[8];
            lock (Random)
                Random.NextBytes(bytes);
            return BitConverter.ToUInt64(bytes, 0);
        }

        private static uint NextUInt32()
        {
            byte[] bytes = new byte[4];
            lock (Random)
                Random.NextBytes(bytes);
            return BitConverter.ToUInt32(bytes, 0);
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

        private static void Validate(string outputPath, int expectedActors, int expectedRails)
        {
            byte[] data = DecompressIfNeeded(File.ReadAllBytes(outputPath), outputPath);
            BymlFileData byml = ByamlFile.LoadN(new MemoryStream(data));
            IDictionary<string, dynamic> root = byml.RootNode as IDictionary<string, dynamic>;
            if (root == null)
                throw new InvalidOperationException("The saved layout root is not a dictionary.");
            if (!root.TryGetValue("Actors", out dynamic actorsValue) || !(actorsValue is IList<dynamic> actors))
                throw new InvalidOperationException("The saved layout does not contain an Actors list.");
            if (actors.Count != expectedActors)
                throw new InvalidOperationException($"The saved layout has {actors.Count} actors, expected {expectedActors}.");
            if (!root.TryGetValue("Rails", out dynamic railsValue) || !(railsValue is IList<dynamic> rails))
                throw new InvalidOperationException("The saved layout does not contain a Rails list.");
            if (rails.Count != expectedRails)
                throw new InvalidOperationException($"The saved layout has {rails.Count} rails, expected {expectedRails}.");
        }
    }
}
