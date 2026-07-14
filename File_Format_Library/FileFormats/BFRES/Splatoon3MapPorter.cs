using Bfres.Structs;
using OpenTK;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Toolbox.Library;

namespace FirstPlugin
{
    internal class Splatoon3MapPortAnalysis
    {
        public int ModelCount;
        public int ShapeCount;
        public int BoneCount;
        public int MaterialCount;
        public int Splatoon2MaterialCount;
        public int Splatoon3MaterialCount;
        public readonly List<string> UnmatchedMaterials = new List<string>();
        internal readonly List<Splatoon3MaterialReplacement> Replacements = new List<Splatoon3MaterialReplacement>();
        internal readonly List<Splatoon3MaterialReplacement> MaterialProposals = new List<Splatoon3MaterialReplacement>();
    }

    internal class Splatoon3MaterialReplacement
    {
        public FMDL Model;
        public FMAT Material;
        public FMAT SourceMaterial;
        public string PresetPath;
        public string Signature;
        public string Paintability;
        public string Status;
        public bool UsePresetTextures;
        public string OpaTextureName;
        public readonly Dictionary<int, string> BakeTextures = new Dictionary<int, string>();
    }

    internal class Splatoon3TextureTransfer
    {
        public TextureData SourceTexture;
        public BNTX SourceBntx;
        public string BftexPath;
        public TEX_FORMAT? DefaultFormat;
        public bool ReplacesExisting;
        public bool IsBake;
        public string TextureName => SourceTexture?.Text ?? Path.GetFileNameWithoutExtension(BftexPath);
    }

    internal class Splatoon3TextureTransferResult
    {
        public int Added;
        public int Replaced;
        public int Removed;
        public readonly List<string> MissingTextures = new List<string>();
    }

    internal sealed class Splatoon3MapPortCompression : ICompressionFormat
    {
        private readonly Zstb compression = new Zstb();

        public string[] Description { get; set; } = new string[] { "ZSTD" };
        public string[] Extension { get; set; } = new string[] { "*.zstd", "*.zst" };
        public bool CanCompress => true;

        public Splatoon3MapPortCompression(string fileName)
        {
            compression.Init(fileName ?? "");
        }

        public bool Identify(Stream stream, string fileName)
        {
            return compression.Identify(stream, fileName);
        }

        public Stream Decompress(Stream stream)
        {
            return compression.Decompress(stream);
        }

        public Stream Compress(Stream stream)
        {
            if (stream is MemoryStream memoryStream)
                return new MemoryStream(Zstb.SCompress(memoryStream.ToArray(), 9));

            using (MemoryStream input = new MemoryStream())
            {
                if (stream.CanSeek)
                    stream.Position = 0;
                stream.CopyTo(input);
                return new MemoryStream(Zstb.SCompress(input.ToArray(), 9));
            }
        }

        public override string ToString()
        {
            return "ZSTD";
        }
    }

    internal static class Splatoon3MapPorter
    {
        public static Splatoon3MapPortAnalysis Analyze(List<FMDL> models, string materialsFolder, BFRES sourceBfres = null)
        {
            return Analyze(models, materialsFolder, model => sourceBfres);
        }

        public static Splatoon3MapPortAnalysis Analyze(List<FMDL> models, string materialsFolder, Dictionary<FMDL, BFRES> sourceBfresByModel)
        {
            return Analyze(models, materialsFolder, model =>
            {
                if (sourceBfresByModel != null && sourceBfresByModel.TryGetValue(model, out BFRES sourceBfres))
                    return sourceBfres;

                return null;
            });
        }

        private static Splatoon3MapPortAnalysis Analyze(List<FMDL> models, string materialsFolder, Func<FMDL, BFRES> sourceResolver)
        {
            Splatoon3MapPortAnalysis analysis = new Splatoon3MapPortAnalysis();
            Dictionary<string, bool> transparentTextureCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            analysis.ModelCount = models.Count;

            foreach (FMDL model in models)
            {
                BNTX sourceBntx = sourceResolver?.Invoke(model)?.GetBNTX;
                analysis.ShapeCount += model.shapes.Count;
                analysis.BoneCount += model.Skeleton?.bones.Count ?? 0;
                analysis.MaterialCount += model.materials.Count;

                foreach (FMAT material in model.materials.Values)
                {
                    string shaderArchive = material.shaderassign?.ShaderArchive ?? "";
                    string opaTextureName = SupportsGeneratedOpa(material)
                        ? GetRequiredOpaTextureName(material, sourceBntx, transparentTextureCache)
                        : null;
                    Splatoon3MaterialReplacement proposal = new Splatoon3MaterialReplacement
                    {
                        Model = model,
                        Material = material,
                        SourceMaterial = material,
                        Signature = GetSignature(material, opaTextureName),
                        Paintability = "Unknown",
                        OpaTextureName = opaTextureName,
                    };
                    foreach (MatTexture map in material.TextureMaps.OfType<MatTexture>())
                    {
                        int bakeSlot = GetBakeSlot(map);
                        if (bakeSlot >= 0 && !string.IsNullOrWhiteSpace(map.Name))
                            proposal.BakeTextures[bakeSlot] = map.Name;
                    }
                    analysis.MaterialProposals.Add(proposal);

                    if (shaderArchive.IndexOf("Blitz_UBER", StringComparison.OrdinalIgnoreCase) >= 0)
                        analysis.Splatoon2MaterialCount++;
                    if (shaderArchive.IndexOf("Hoian_UBER", StringComparison.OrdinalIgnoreCase) >= 0)
                        analysis.Splatoon3MaterialCount++;

                    if (IsGlassTileDistantMaterialName(material.Text))
                    {
                        string glassTilePresetPath = Path.Combine(materialsFolder, "GlassTileDistant.bfmat");
                        proposal.Signature = "GlassTileDistant";
                        proposal.Paintability = "Glass";
                        proposal.UsePresetTextures = false;

                        if (File.Exists(glassTilePresetPath))
                        {
                            proposal.PresetPath = glassTilePresetPath;
                            proposal.Status = "GlassTileDistant";
                            analysis.Replacements.Add(proposal);
                        }
                        else
                        {
                            proposal.Status = "Missing GlassTileDistant.bfmat";
                            analysis.UnmatchedMaterials.Add($"{model.Text}/{material.Text} (missing GlassTileDistant.bfmat)");
                        }
                        continue;
                    }

                    if (IsGlassMaterialName(material.Text))
                    {
                        string glassPresetName = GetGlassPresetName(material);
                        string glassPresetPath = Path.Combine(materialsFolder, glassPresetName);
                        proposal.Signature = "Glass";
                        proposal.Paintability = "Glass";
                        proposal.UsePresetTextures = string.Equals(glassPresetName, "Glass.bfmat", StringComparison.OrdinalIgnoreCase);

                        if (File.Exists(glassPresetPath))
                        {
                            proposal.PresetPath = glassPresetPath;
                            proposal.Status = Path.GetFileNameWithoutExtension(glassPresetName);
                            analysis.Replacements.Add(proposal);
                        }
                        else
                        {
                            proposal.Status = $"Missing {glassPresetName}";
                            analysis.UnmatchedMaterials.Add($"{model.Text}/{material.Text} (missing {glassPresetName})");
                        }
                        continue;
                    }

                    if (shaderArchive.IndexOf("Blitz_UBER", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        if (!TryAssignFallbackPreset(analysis, proposal, materialsFolder, $"shader {shaderArchive}"))
                            analysis.UnmatchedMaterials.Add($"{model.Text}/{material.Text} (shader {shaderArchive})");
                        continue;
                    }

                    if (!TryGetPaintableValue(material, out bool paintable))
                    {
                        if (!TryAssignFallbackPreset(analysis, proposal, materialsFolder, "unknown paintability"))
                            analysis.UnmatchedMaterials.Add($"{model.Text}/{material.Text} (unknown paintability)");
                        continue;
                    }

                    proposal.Paintability = paintable ? "Paintable" : "Unpaintable";
                    string signature = proposal.Signature;
                    if (string.IsNullOrEmpty(signature))
                    {
                        if (!TryAssignFallbackPreset(analysis, proposal, materialsFolder, "unsupported texture maps"))
                            analysis.UnmatchedMaterials.Add($"{model.Text}/{material.Text} (unsupported texture maps)");
                        continue;
                    }

                    string presetName = (paintable ? "Paintable_" : "Unpaintable_") + signature + ".bfmat";
                    string presetPath = Path.Combine(materialsFolder, presetName);
                    if (!File.Exists(presetPath))
                    {
                        if (signature.IndexOf("Emm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            signature.IndexOf("Col", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            proposal.Status = $"Missing {presetName}";
                            analysis.UnmatchedMaterials.Add($"{model.Text}/{material.Text} (missing {presetName})");
                            continue;
                        }

                        if (!TryAssignFallbackPreset(analysis, proposal, materialsFolder, $"missing {presetName}"))
                            analysis.UnmatchedMaterials.Add($"{model.Text}/{material.Text} (missing {presetName})");
                        continue;
                    }

                    proposal.PresetPath = presetPath;
                    proposal.Status = "Matched";
                    analysis.Replacements.Add(proposal);
                }
            }

            return analysis;
        }

        private static bool TryAssignFallbackPreset(Splatoon3MapPortAnalysis analysis, Splatoon3MaterialReplacement proposal, string materialsFolder, string reason)
        {
            string signature = string.IsNullOrEmpty(proposal.Signature) ? "Alb" : proposal.Signature;
            List<string> presetNames = new List<string>();
            if (signature.IndexOf("Opa", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                presetNames.Add(proposal.Paintability + "_AlbNrmRghOpa.bfmat");
                presetNames.Add("Unpaintable_AlbNrmRghOpa.bfmat");
            }
            presetNames.Add("Unpaintable_" + signature + ".bfmat");
            presetNames.Add("Unpaintable_Alb.bfmat");

            foreach (string presetName in presetNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string presetPath = Path.Combine(materialsFolder, presetName);
                if (!File.Exists(presetPath))
                    continue;

                proposal.PresetPath = presetPath;
                proposal.Paintability = "Unpaintable";
                proposal.Status = $"Fallback: {presetName} ({reason})";
                analysis.Replacements.Add(proposal);
                return true;
            }

            proposal.Status = $"No fallback BFMAT ({reason})";
            return false;
        }

        public static void Apply(List<FMDL> models, Splatoon3MapPortAnalysis analysis)
        {
            ApplyMaterialReplacements(analysis.Replacements);

            foreach (FMDL model in models)
                ScaleModel(model, 0.1f);

            foreach (FMDL model in models)
            {
                foreach (FMAT material in model.materials.Values)
                {
                    string shaderArchive = material.Material?.ShaderAssign?.ShaderArchiveName ?? material.shaderassign?.ShaderArchive ?? "";
                    if (shaderArchive.IndexOf("Blitz_UBER", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new InvalidOperationException($"{model.Text}/{material.Text} still contains a Splatoon 2 material.");
                }
            }
        }

        public static void ReplaceTargetModels(List<FMDL> sourceModels, List<FMDL> targetModels)
        {
            string temporaryDirectory = Path.Combine(Path.GetTempPath(), "Splatoon3MapPort_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var targetMetadata = targetModels
                .Select(model => model.GetResFile())
                .Where(resFile => resFile != null)
                .Distinct()
                .Select(resFile => new
                {
                    ResFile = resFile,
                    resFile.VersionMajor,
                    resFile.VersionMajor2,
                    resFile.VersionMinor,
                    resFile.VersionMinor2,
                })
                .ToList();

            try
            {
                for (int i = 0; i < sourceModels.Count; i++)
                {
                    PumpMessages();
                    FMDL sourceModel = sourceModels[i];
                    FMDL targetModel = targetModels[i];

                    if (sourceModel.Model == null || targetModel.Model == null || targetModel.GetResFile() == null)
                        throw new InvalidOperationException($"{sourceModel.Text} cannot be transferred into the selected Splatoon 3 model.");

                    FSKL targetSkeleton = targetModel.Skeleton;
                    string targetPath = targetModel.Model.Path;
                    var targetUserData = targetModel.Model.UserData;
                    var targetUserDataDict = targetModel.Model.UserDataDict;
                    string modelPath = Path.Combine(temporaryDirectory, i + ".bfmdl");
                    sourceModel.Export(modelPath);
                    targetModel.Replace(modelPath, targetModel.GetResFile(), null);
                    if (ShouldPreserveImportedSkeleton(sourceModel, targetModel))
                    {
                        targetModel.Skeleton.reset();
                        targetModel.Skeleton.CalculateIndices();
                    }
                    else
                    {
                        FlattenImportedModelSkinning(targetModel);
                        targetModel.Nodes.Remove(targetModel.Skeleton.node);
                        targetModel.Skeleton = targetSkeleton;
                        targetModel.Nodes.Insert(Math.Min(2, targetModel.Nodes.Count), targetSkeleton.node);
                        targetModel.Model.Skeleton = targetSkeleton.node.Skeleton;
                        RenameRootBoneToModelName(targetModel);
                    }

                    targetModel.Model.Path = targetPath;
                    targetModel.Model.UserData = targetUserData;
                    targetModel.Model.UserDataDict = targetUserDataDict;
                    PumpMessages();
                }
            }
            finally
            {
                foreach (var metadata in targetMetadata)
                {
                    metadata.ResFile.VersionMajor = metadata.VersionMajor;
                    metadata.ResFile.VersionMajor2 = metadata.VersionMajor2;
                    metadata.ResFile.VersionMinor = metadata.VersionMinor;
                    metadata.ResFile.VersionMinor2 = metadata.VersionMinor2;
                }

                foreach (string file in Directory.GetFiles(temporaryDirectory))
                    File.Delete(file);
                Directory.Delete(temporaryDirectory);
            }
        }

        private static bool ShouldPreserveImportedSkeleton(FMDL sourceModel, FMDL targetModel)
        {
            if (!IsAnimatedModelName(sourceModel?.Text) && !IsAnimatedModelName(targetModel?.Text))
                return false;

            return HasImportedSkeletonData(sourceModel);
        }

        private static bool IsAnimatedModelName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return name.IndexOf("Flag", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasImportedSkeletonData(FMDL model)
        {
            if (model?.Skeleton?.bones == null)
                return false;

            if (model.Skeleton.bones.Count > 1)
                return true;

            foreach (FSHP shape in model.shapes)
            {
                if (shape?.Shape?.SkinBoneIndices != null && shape.Shape.SkinBoneIndices.Count > 0)
                    return true;

                if (shape?.vertexAttributes != null &&
                    shape.vertexAttributes.Any(attribute =>
                        attribute.Name.StartsWith("_i", StringComparison.OrdinalIgnoreCase) ||
                        attribute.Name.StartsWith("_w", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }

        private static void FlattenImportedModelSkinning(FMDL model)
        {
            foreach (FSHP shape in model.shapes)
                FlattenImportedShapeSkinning(shape);
        }

        private static void RenameRootBoneToModelName(FMDL model)
        {
            if (model?.Skeleton?.bones == null || model.Skeleton.bones.Count == 0)
                return;

            if (model.Skeleton.bones[0] is BfresBone rootBone)
                rootBone.RenameBone(model.Text);
            else
                model.Skeleton.bones[0].Text = model.Text;
        }

        private static void FlattenImportedShapeSkinning(FSHP shape)
        {
            shape.VertexSkinCount = 0;
            shape.BoneIndex = 0;
            shape.BoneIndices.Clear();

            if (shape.Shape != null)
            {
                shape.Shape.VertexSkinCount = 0;
                shape.Shape.BoneIndex = 0;
                if (shape.Shape.SkinBoneIndices == null)
                    shape.Shape.SkinBoneIndices = new List<ushort>();
                else
                    shape.Shape.SkinBoneIndices.Clear();
            }

            if (shape.VertexBuffer != null)
                shape.VertexBuffer.VertexSkinCount = 0;

            if (shape.vertices != null)
            {
                foreach (Toolbox.Library.Rendering.Vertex vertex in shape.vertices)
                {
                    vertex.boneIds.Clear();
                    vertex.boneWeights.Clear();
                }
            }

            shape.vertexAttributes.RemoveAll(attribute =>
                attribute.Name.StartsWith("_i", StringComparison.OrdinalIgnoreCase) ||
                attribute.Name.StartsWith("_w", StringComparison.OrdinalIgnoreCase));
        }

        private static void PumpMessages()
        {
            if (Application.MessageLoop)
                Application.DoEvents();
        }

        public static List<Splatoon3TextureTransfer> AnalyzeTextures(BFRES sourceBfres, BFRES targetBfres, List<FMDL> sourceModels, string materialsFolder)
        {
            List<Splatoon3TextureTransfer> transfers = new List<Splatoon3TextureTransfer>();
            BNTX sourceBntx = sourceBfres.GetBNTX;
            BNTX targetBntx = targetBfres.GetBNTX;

            if (targetBntx == null)
                return transfers;

            HashSet<string> bakeTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (FMDL model in sourceModels)
            {
                foreach (FMAT material in model.materials.Values)
                {
                    foreach (MatTexture map in material.TextureMaps.OfType<MatTexture>())
                    {
                        if (IsBakeMap(map))
                            bakeTextures.Add(map.Name);
                    }
                }
            }

            if (sourceBntx != null)
            {
                foreach (TextureData texture in sourceBntx.Textures.Values)
                {
                    texture.ParentBNTX = sourceBntx;
                    transfers.Add(new Splatoon3TextureTransfer
                    {
                        SourceTexture = texture,
                        SourceBntx = sourceBntx,
                        ReplacesExisting = targetBntx.Textures.ContainsKey(texture.Text),
                        IsBake = bakeTextures.Contains(texture.Text) || texture.Text.IndexOf("Bake", StringComparison.OrdinalIgnoreCase) >= 0,
                    });
                }

                Dictionary<string, TextureData> generatedOpaTextures = new Dictionary<string, TextureData>(StringComparer.OrdinalIgnoreCase);
                foreach (FMDL model in sourceModels)
                {
                    foreach (FMAT material in model.materials.Values)
                    {
                        if (!SupportsGeneratedOpa(material))
                            continue;

                        string albedoTextureName = GetAlbedoTextureName(material);
                        if (string.IsNullOrWhiteSpace(albedoTextureName) ||
                            !sourceBntx.Textures.TryGetValue(albedoTextureName, out TextureData albedoTexture))
                            continue;

                        string opaTextureName = GetOpaTextureName(albedoTextureName);
                        if (string.IsNullOrWhiteSpace(opaTextureName) ||
                            sourceBntx.Textures.ContainsKey(opaTextureName) ||
                            generatedOpaTextures.ContainsKey(opaTextureName))
                            continue;

                        TextureData opaTexture = CreateOpaTexture(albedoTexture, sourceBntx, opaTextureName);
                        if (opaTexture == null)
                            continue;

                        generatedOpaTextures.Add(opaTextureName, opaTexture);
                        transfers.Add(new Splatoon3TextureTransfer
                        {
                            SourceTexture = opaTexture,
                            SourceBntx = sourceBntx,
                            ReplacesExisting = targetBntx.Textures.ContainsKey(opaTextureName),
                            IsBake = false,
                        });
                    }
                }
            }

            bool hasGlassMaterials = sourceModels
                .SelectMany(model => model.materials.Values)
                .Any(material => IsGlassMaterialName(material.Text) && !IsGlassTileDistantMaterialName(material.Text));

            if (hasGlassMaterials)
            {
                AddPresetTextureTransfer(transfers, targetBntx, Path.Combine(materialsFolder, "Glass_Nrm.bftex"));
                AddPresetTextureTransfer(transfers, targetBntx, Path.Combine(materialsFolder, "Glass_Rgh.bftex"));
            }
            AddBasicTextureTransfers(transfers, targetBntx, materialsFolder);

            return transfers.OrderBy(transfer => transfer.TextureName).ToList();
        }

        public static Splatoon3TextureTransferResult TransferTextures(BFRES targetBfres, List<Splatoon3TextureTransfer> transfers)
        {
            BNTX targetBntx = targetBfres.GetBNTX;
            if (targetBntx == null)
                throw new InvalidOperationException("The active Splatoon 3 BFRES does not contain a BNTX texture container.");

            Splatoon3TextureTransferResult result = new Splatoon3TextureTransferResult();
            string temporaryDirectory = Path.Combine(Path.GetTempPath(), "Splatoon3TexturePort_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);

            try
            {
                for (int index = 0; index < transfers.Count; index++)
                {
                    PumpMessages();
                    Splatoon3TextureTransfer transfer = transfers[index];
                    string texturePath = Path.Combine(temporaryDirectory, index + ".bftex");

                    if (transfer.SourceTexture != null)
                    {
                        TextureData sourceTexture = ResolveSourceTexture(transfer);
                        BNTX sourceBntx = transfer.SourceBntx ?? sourceTexture.ParentBNTX;
                        sourceTexture.ParentBNTX = sourceBntx;
                        if (sourceTexture.ParentBNTX?.BinaryTexFile == null)
                            throw new InvalidOperationException($"Cannot export texture {transfer.TextureName} because its source BNTX is unavailable.");
                        sourceTexture.SaveBinaryTexture(texturePath);
                    }
                    else
                    {
                        if (Path.GetExtension(transfer.BftexPath).Equals(".bftex", StringComparison.OrdinalIgnoreCase))
                            File.Copy(transfer.BftexPath, texturePath, true);
                        else
                            ConvertPresetTextureToBftex(transfer, targetBntx, texturePath);
                    }

                    if (targetBntx.Textures.TryGetValue(transfer.TextureName, out TextureData targetTexture))
                    {
                        targetTexture.ReplaceBftex(texturePath, false);
                        result.Replaced++;
                    }
                    else
                    {
                        TextureData addedTexture = targetBntx.AddTexture(texturePath);
                        if (addedTexture == null)
                            throw new InvalidOperationException($"Failed to import {transfer.TextureName} through BFTEX.");
                        result.Added++;
                    }
                    PumpMessages();
                }
            }
            finally
            {
                foreach (string file in Directory.GetFiles(temporaryDirectory))
                    File.Delete(file);
                Directory.Delete(temporaryDirectory);
            }

            return result;
        }

        private static void ConvertPresetTextureToBftex(Splatoon3TextureTransfer transfer, BNTX targetBntx, string outputPath)
        {
            TextureImporterSettings settings = new TextureImporterSettings();
            if (transfer.DefaultFormat.HasValue)
                settings.DefaultFormat = transfer.DefaultFormat.Value;
            settings.LoadBitMap(transfer.BftexPath);
            if (transfer.DefaultFormat.HasValue)
                settings.Format = TextureData.GenericToBntxSurfaceFormat(transfer.DefaultFormat.Value);
            settings.DataBlockOutput.Add(settings.GenerateMips(STCompressionMode.Normal, false));
            Syroot.NintenTools.NSW.Bntx.Texture texture = settings.FromBitMap(settings.DataBlockOutput, settings);
            texture.Name = transfer.TextureName;
            TextureData textureData = new TextureData(texture, targetBntx.BinaryTexFile)
            {
                Text = transfer.TextureName,
                ParentBNTX = targetBntx,
            };
            textureData.SaveBinaryTexture(outputPath);
        }

        private static TextureData ResolveSourceTexture(Splatoon3TextureTransfer transfer)
        {
            TextureData sourceTexture = transfer.SourceTexture;
            if (transfer.SourceBntx != null &&
                !string.IsNullOrWhiteSpace(transfer.TextureName) &&
                transfer.SourceBntx.Textures.TryGetValue(transfer.TextureName, out TextureData liveTexture) &&
                liveTexture?.Texture != null)
            {
                sourceTexture = liveTexture;
            }

            if (sourceTexture == null)
                throw new InvalidOperationException($"Cannot export texture {transfer.TextureName} because its source texture is unavailable.");

            if (sourceTexture.Texture == null)
                throw new InvalidOperationException($"Cannot export texture {transfer.TextureName} because its source texture data is unavailable.");

            return sourceTexture;
        }

        public static void RemoveUnreferencedTextures(BFRES targetBfres, List<FMDL> targetModels, List<Splatoon3TextureTransfer> retainedTransfers, Splatoon3TextureTransferResult result)
        {
            BNTX targetBntx = targetBfres.GetBNTX;
            if (targetBntx == null)
                throw new InvalidOperationException("The active Splatoon 3 BFRES does not contain a BNTX texture container.");

            HashSet<string> referencedTextures = GetReferencedTextureNames(targetBfres, targetModels);
            HashSet<string> retainedTextureNames = new HashSet<string>(
                retainedTransfers.Select(transfer => transfer.TextureName),
                StringComparer.OrdinalIgnoreCase);
            foreach (TextureData texture in targetBntx.Textures.Values.ToList())
            {
                PumpMessages();
                if (IsReferencedTexture(texture.Text, referencedTextures) || retainedTextureNames.Contains(texture.Text))
                    continue;

                targetBntx.RemoveTexture(texture);
                result.Removed++;
            }
        }

        public static void ValidateTextureReferences(BFRES targetBfres, List<FMDL> targetModels, Splatoon3TextureTransferResult result)
        {
            BNTX targetBntx = targetBfres.GetBNTX;
            if (targetBntx == null)
                throw new InvalidOperationException("The active Splatoon 3 BFRES does not contain a BNTX texture container.");

            HashSet<string> referencedTextures = GetReferencedTextureNames(targetBfres, targetModels);

            foreach (string textureName in referencedTextures.OrderBy(name => name))
            {
                PumpMessages();
                if (targetBntx.Textures.ContainsKey(textureName) ||
                    targetBntx.Textures.Keys.Any(name => IsReferencedTexture(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { textureName })))
                    continue;

                result.MissingTextures.Add(textureName);
            }

        }

        private static HashSet<string> GetReferencedTextureNames(BFRES targetBfres, List<FMDL> targetModels)
        {
            HashSet<string> referencedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FMDL model in targetModels)
            {
                foreach (FMAT material in model.materials.Values)
                {
                    foreach (string textureName in GetTextureReferenceNames(material))
                    {
                        if (!string.IsNullOrWhiteSpace(textureName))
                            referencedTextures.Add(textureName);
                    }
                }
            }

            if (targetBfres.resFile?.MaterialAnims != null)
            {
                foreach (var materialAnim in targetBfres.resFile.MaterialAnims)
                {
                    if (materialAnim.TextureNames == null)
                        continue;

                    foreach (string textureName in materialAnim.TextureNames)
                    {
                        if (!string.IsNullOrWhiteSpace(textureName))
                            referencedTextures.Add(textureName);
                    }
                }
            }

            return referencedTextures;
        }

        public static void ValidateSave(BFRES targetBfres, List<Splatoon3MaterialReplacement> replacements, List<Splatoon3TextureTransfer> retainedTransfers)
        {
            string targetPath = targetBfres.FilePath ?? targetBfres.FileName ?? "";
            if (targetBfres.IFileInfo != null &&
                (targetBfres.IFileInfo.FileIsCompressed || targetPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)))
            {
                targetBfres.IFileInfo.FileIsCompressed = true;
                targetBfres.IFileInfo.FileCompression = new Splatoon3MapPortCompression(targetPath);
            }

            using (MemoryStream bfresStream = new MemoryStream())
            {
                PumpMessages();
                targetBfres.Save(bfresStream);
                PumpMessages();
                byte[] bfresData = bfresStream.ToArray();

                using (MemoryStream reloadStream = new MemoryStream(bfresData))
                {
                    var reloadedResFile = new Syroot.NintenTools.NSW.Bfres.ResFile(reloadStream);
                    if (targetBfres.resFile != null &&
                        (reloadedResFile.VersionMajor != targetBfres.resFile.VersionMajor ||
                         reloadedResFile.VersionMajor2 != targetBfres.resFile.VersionMajor2 ||
                         reloadedResFile.VersionMinor != targetBfres.resFile.VersionMinor ||
                         reloadedResFile.VersionMinor2 != targetBfres.resFile.VersionMinor2))
                        throw new InvalidOperationException($"The saved BFRES version changed from {targetBfres.resFile.VersionFull} to {reloadedResFile.VersionFull}.");

                    Dictionary<string, Syroot.NintenTools.NSW.Bfres.Material> expectedMaterials = new Dictionary<string, Syroot.NintenTools.NSW.Bfres.Material>(StringComparer.OrdinalIgnoreCase);
                    foreach (Splatoon3MaterialReplacement replacement in replacements)
                    {
                        var model = reloadedResFile.Models.FirstOrDefault(item => item.Name == replacement.Model.Text);
                        var material = model?.Materials.FirstOrDefault(item => item.Name == replacement.Material.Text);
                        if (material == null)
                            throw new InvalidOperationException($"The saved material {replacement.Model.Text}/{replacement.Material.Text} could not be validated.");

                        Syroot.NintenTools.NSW.Bfres.Material expected;
                        if (replacement.SourceMaterial?.Material != null)
                        {
                            expected = new Syroot.NintenTools.NSW.Bfres.Material();
                            expected.Import(replacement.PresetPath);
                            CopyCompatibleShaderParameterValues(expected, replacement.SourceMaterial.Material);
                        }
                        else if (!expectedMaterials.TryGetValue(replacement.PresetPath, out expected))
                        {
                            expected = new Syroot.NintenTools.NSW.Bfres.Material();
                            expected.Import(replacement.PresetPath);
                            expectedMaterials.Add(replacement.PresetPath, expected);
                        }
                        ValidateMaterialPayload(material, expected, $"{replacement.Model.Text}/{replacement.Material.Text}");
                    }

                    var externalBntx = reloadedResFile.ExternalFiles.FirstOrDefault(file =>
                        file.Data != null && file.Data.Length >= 4 &&
                        file.Data[0] == (byte)'B' && file.Data[1] == (byte)'N' && file.Data[2] == (byte)'T' && file.Data[3] == (byte)'X');
                    if (externalBntx == null && retainedTransfers.Count > 0)
                        throw new InvalidOperationException("The saved BFRES does not contain the transferred BNTX.");

                    if (externalBntx != null)
                    {
                        using (MemoryStream bntxStream = new MemoryStream(externalBntx.Data))
                        {
                            var savedBntx = new Syroot.NintenTools.NSW.Bntx.BntxFile(bntxStream, true);
                            HashSet<string> savedTextureNames = new HashSet<string>(savedBntx.Textures.Select(texture => texture.Name), StringComparer.OrdinalIgnoreCase);
                            List<string> missingTransfers = retainedTransfers
                                .Select(transfer => transfer.TextureName)
                                .Where(name => !savedTextureNames.Contains(name))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            if (missingTransfers.Count > 0)
                                throw new InvalidOperationException("The saved BNTX is missing transferred Splatoon 2 textures: " + string.Join(", ", missingTransfers));
                        }
                    }
                }

                if (targetBfres.IFileInfo?.FileIsCompressed == true && targetBfres.IFileInfo.FileCompression == null)
                    throw new InvalidOperationException("The target compression was not configured.");
            }
        }

        private static void ApplyMaterialReplacements(List<Splatoon3MaterialReplacement> replacements)
        {
            foreach (Splatoon3MaterialReplacement replacement in replacements)
            {
                replacement.Material.Replace(replacement.PresetPath, false);
                var expected = new Syroot.NintenTools.NSW.Bfres.Material();
                expected.Import(replacement.PresetPath);
                ValidateMaterialPayload(replacement.Material.Material, expected, $"{replacement.Model.Text}/{replacement.Material.Text}");
                RestoreBakeTextures(replacement);
                RestoreOpaTexture(replacement);
                ApplySourceShaderParameterValues(replacement);
            }
        }

        private static void ApplySourceShaderParameterValues(Splatoon3MaterialReplacement replacement)
        {
            if (replacement.Material?.Material == null || replacement.SourceMaterial?.Material == null)
                return;

            if (CopyCompatibleShaderParameterValues(replacement.Material.Material, replacement.SourceMaterial.Material))
                replacement.Material.ReadShaderParams(replacement.Material.Material);
        }

        private static bool CopyCompatibleShaderParameterValues(Syroot.NintenTools.NSW.Bfres.Material target, Syroot.NintenTools.NSW.Bfres.Material source)
        {
            if (target?.ShaderParams == null || source?.ShaderParams == null || target.ShaderParamData == null || source.ShaderParamData == null)
                return false;

            Dictionary<string, Syroot.NintenTools.NSW.Bfres.ShaderParam> sourceParams = source.ShaderParams
                .Where(parameter => IsPortableSourceShaderParameterName(parameter.Name))
                .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            bool changed = false;
            foreach (Syroot.NintenTools.NSW.Bfres.ShaderParam targetParam in target.ShaderParams)
            {
                if (!IsPortableSourceShaderParameterName(targetParam.Name) ||
                    !sourceParams.TryGetValue(targetParam.Name, out Syroot.NintenTools.NSW.Bfres.ShaderParam sourceParam) ||
                    sourceParam.Type != targetParam.Type ||
                    sourceParam.DataSize != targetParam.DataSize ||
                    !IsShaderParamRangeValid(source.ShaderParamData, sourceParam) ||
                    !IsShaderParamRangeValid(target.ShaderParamData, targetParam))
                    continue;

                Array.Copy(source.ShaderParamData, (int)sourceParam.DataOffset, target.ShaderParamData, (int)targetParam.DataOffset, (int)targetParam.DataSize);
                changed = true;
            }

            return changed;
        }

        private static bool IsPortableSourceShaderParameterName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return !name.StartsWith("blitz_", StringComparison.OrdinalIgnoreCase) &&
                   !name.StartsWith("enable_calc_", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsShaderParamRangeValid(byte[] data, Syroot.NintenTools.NSW.Bfres.ShaderParam parameter)
        {
            long offset = parameter.DataOffset;
            long size = parameter.DataSize;
            return offset >= 0 && size >= 0 && offset + size <= data.Length && offset <= int.MaxValue && size <= int.MaxValue;
        }

        private static void RestoreOpaTexture(Splatoon3MaterialReplacement replacement)
        {
            if (string.IsNullOrWhiteSpace(replacement.OpaTextureName))
                return;

            bool changed = false;
            for (int index = 0; index < replacement.Material.TextureMaps.Count; index++)
            {
                if (!(replacement.Material.TextureMaps[index] is MatTexture map) || !IsOpaMap(map))
                    continue;

                map.Name = replacement.OpaTextureName;
                map.textureState = STGenericMatTexture.TextureState.Replaced;
                if (replacement.Material.Material?.TextureRefs != null && index < replacement.Material.Material.TextureRefs.Count)
                    replacement.Material.Material.TextureRefs[index] = replacement.OpaTextureName;
                if (replacement.Material.MaterialU?.TextureRefs != null && index < replacement.Material.MaterialU.TextureRefs.Count)
                    replacement.Material.MaterialU.TextureRefs[index].Name = replacement.OpaTextureName;
                changed = true;
            }

            if (!changed)
                throw new InvalidOperationException($"{replacement.Model.Text}/{replacement.Material.Text} requires an Opa material slot, but the selected BFMAT does not contain one.");

            replacement.Material.UpdateTextureMaps();
        }

        private static void RestoreBakeTextures(Splatoon3MaterialReplacement replacement)
        {
            bool changed = false;
            bool forceBakeDummy = IsGlassMaterialName(replacement.Material.Text) || IsGlassMaterialName(replacement.Signature);
            for (int index = 0; index < replacement.Material.TextureMaps.Count; index++)
            {
                if (!(replacement.Material.TextureMaps[index] is MatTexture map))
                    continue;

                int bakeSlot = GetBakeSlot(map);
                if (bakeSlot < 0)
                    continue;

                string textureName;
                if (forceBakeDummy)
                    textureName = GetBakeDummyTextureName(bakeSlot);
                else if (!replacement.BakeTextures.TryGetValue(bakeSlot, out textureName))
                    continue;

                map.Name = textureName;
                map.textureState = STGenericMatTexture.TextureState.Replaced;
                if (replacement.Material.Material?.TextureRefs != null && index < replacement.Material.Material.TextureRefs.Count)
                    replacement.Material.Material.TextureRefs[index] = textureName;
                if (replacement.Material.MaterialU?.TextureRefs != null && index < replacement.Material.MaterialU.TextureRefs.Count)
                    replacement.Material.MaterialU.TextureRefs[index].Name = textureName;
                changed = true;
            }

            if (changed)
                replacement.Material.UpdateTextureMaps();
        }

        private static string GetBakeDummyTextureName(int bakeSlot)
        {
            return bakeSlot == 1 ? "LightBakeDummy00" : "BakeDummy00";
        }

        private static void ValidateMaterialPayload(Syroot.NintenTools.NSW.Bfres.Material actual, Syroot.NintenTools.NSW.Bfres.Material expected, string materialName)
        {
            if (actual == null || expected == null)
                throw new InvalidOperationException($"{materialName} could not be compared to the selected Splatoon 3 BFMAT payload.");
            if (actual.Flags != expected.Flags)
                throw new InvalidOperationException($"{materialName} has different material flags than the selected Splatoon 3 BFMAT payload.");
            if (actual.ShaderAssign?.ShaderArchiveName != expected.ShaderAssign?.ShaderArchiveName)
                throw new InvalidOperationException($"{materialName} has a different shader archive than the selected Splatoon 3 BFMAT payload.");
            if (actual.ShaderAssign?.ShadingModelName != expected.ShaderAssign?.ShadingModelName)
                throw new InvalidOperationException($"{materialName} has a different shading model than the selected Splatoon 3 BFMAT payload.");
            if (!GetShaderAssignValues(actual.ShaderAssign?.ShaderOptionDict, actual.ShaderAssign?.ShaderOptions).SequenceEqual(GetShaderAssignValues(expected.ShaderAssign?.ShaderOptionDict, expected.ShaderAssign?.ShaderOptions)))
                throw new InvalidOperationException($"{materialName} has different shader options than the selected Splatoon 3 BFMAT payload.");
            if (GetShaderAssignCount(actual.ShaderAssign?.SamplerAssigns) != GetShaderAssignCount(expected.ShaderAssign?.SamplerAssigns))
                throw new InvalidOperationException($"{materialName} has a different sampler slot count than the selected Splatoon 3 BFMAT payload.");
            if (!GetShaderParamValues(actual).SequenceEqual(GetShaderParamValues(expected)))
                throw new InvalidOperationException($"{materialName} has different shader parameter definitions than the selected Splatoon 3 BFMAT payload.");
            if (!GetShaderParamDataValues(actual).SequenceEqual(GetShaderParamDataValues(expected)))
                throw new InvalidOperationException($"{materialName} has different shader parameter values than the selected Splatoon 3 BFMAT payload.");
            if (!GetRenderInfoValues(actual).SequenceEqual(GetRenderInfoValues(expected)))
                throw new InvalidOperationException($"{materialName} has different render infos than the selected Splatoon 3 BFMAT payload.");
        }

        private static IEnumerable<string> GetShaderAssignValues(Syroot.NintenTools.NSW.Bfres.ResDict dictionary, IList<string> values)
        {
            if (dictionary == null || values == null)
                yield break;

            for (int index = 0; index < values.Count; index++)
                yield return dictionary.GetKey(index) + "\0" + values[index];
        }

        private static int GetShaderAssignCount(IList<string> values)
        {
            return values?.Count ?? 0;
        }

        private static IEnumerable<string> GetShaderParamValues(Syroot.NintenTools.NSW.Bfres.Material material)
        {
            if (material.ShaderParams == null)
                yield break;

            foreach (var parameter in material.ShaderParams)
                yield return parameter.Name + "\0" + parameter.Type + "\0" + parameter.DataOffset + "\0" + parameter.DataSize;
        }

        private static IEnumerable<string> GetShaderParamDataValues(Syroot.NintenTools.NSW.Bfres.Material material)
        {
            if (material.ShaderParams == null)
                yield break;

            byte[] data = material.ShaderParamData ?? new byte[0];
            foreach (var parameter in material.ShaderParams)
            {
                long offset = parameter.DataOffset;
                long size = parameter.DataSize;
                if (offset < 0 || size < 0 || offset + size > data.Length)
                {
                    yield return parameter.Name + "\0" + parameter.Type + "\0missing";
                    continue;
                }

                byte[] value = new byte[(int)size];
                Array.Copy(data, (int)offset, value, 0, (int)size);
                yield return parameter.Name + "\0" + parameter.Type + "\0" + Convert.ToBase64String(value);
            }
        }

        private static IEnumerable<string> GetRenderInfoValues(Syroot.NintenTools.NSW.Bfres.Material material)
        {
            if (material.RenderInfos == null)
                yield break;

            foreach (var renderInfo in material.RenderInfos)
            {
                string value;
                switch (renderInfo.Type)
                {
                    case Syroot.NintenTools.NSW.Bfres.RenderInfoType.Int32:
                        value = string.Join(",", renderInfo.GetValueInt32s());
                        break;
                    case Syroot.NintenTools.NSW.Bfres.RenderInfoType.Single:
                        value = string.Join(",", renderInfo.GetValueSingles().Select(item => item.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    default:
                        value = string.Join("\0", renderInfo.GetValueStrings());
                        break;
                }
                yield return renderInfo.Name + "\0" + renderInfo.Type + "\0" + value;
            }
        }

        private static void ScaleModel(FMDL model, float scale)
        {
            Vector3 scaleVector = new Vector3(scale);

            foreach (FSHP shape in model.shapes)
                shape.TransformPosition(Vector3.Zero, Vector3.Zero, scaleVector);

            ScaleSkeleton(model, scale);
            model.Skeleton.reset();
            model.Skeleton.CalculateIndices();

            foreach (FSHP shape in model.shapes)
            {
                shape.SaveVertexBuffer(shape.IsWiiU);
                shape.RebuildSingleMesh(model);
            }

            model.IsEdited = true;
            model.UpdateVertexData();
        }

        private static void ScaleSkeleton(FMDL model, float scale)
        {
            if (model?.Skeleton?.bones == null)
                return;

            foreach (STBone bone in model.Skeleton.bones)
            {
                bone.Position *= scale;
                if (bone is BfresBone bfresBone)
                    bfresBone.GenericToBfresBone();
            }
        }

        private static string GetSignature(FMAT material, string opaTextureName)
        {
            List<string> textures = GetTextureReferenceNames(material);
            List<string> tokens = new List<string>();

            if (textures.Any(texture => ContainsIgnoreCase(texture, "_Alb")))
                tokens.Add("Alb");
            if (textures.Any(texture => ContainsIgnoreCase(texture, "_Nrm")))
                tokens.Add("Nrm");
            if (textures.Any(texture => ContainsIgnoreCase(texture, "_Rgh")))
                tokens.Add("Rgh");
            else if (textures.Any(texture => ContainsIgnoreCase(texture, "_Spm")))
                tokens.Add("Spm");
            if (!string.IsNullOrWhiteSpace(opaTextureName) || textures.Any(texture => ContainsIgnoreCase(texture, "_Opa")))
                tokens.Add("Opa");
            else if (textures.Any(texture => ContainsIgnoreCase(texture, "_Thc") || ContainsIgnoreCase(texture, "_Trm")))
                tokens.Add("Opa");
            if (textures.Any(texture => ContainsIgnoreCase(texture, "_Mtl")))
                tokens.Add("Mtl");
            if (textures.Any(texture => ContainsIgnoreCase(texture, "_ao")))
                tokens.Add("AO");
            if (HasEmissionSampler(material))
                tokens.Add("Emm");
            if (textures.Any(texture => ContainsIgnoreCase(texture, "_Col")))
                tokens.Add("Col");

            return string.Concat(tokens);
        }

        private static string GetRequiredOpaTextureName(FMAT material, BNTX sourceBntx, Dictionary<string, bool> transparentTextureCache)
        {
            string albedoTextureName = GetAlbedoTextureName(material);
            if (string.IsNullOrWhiteSpace(albedoTextureName) || sourceBntx == null ||
                !sourceBntx.Textures.TryGetValue(albedoTextureName, out TextureData texture))
                return null;

            string cacheKey = $"{sourceBntx.GetHashCode()}:{albedoTextureName}";
            if (!transparentTextureCache.TryGetValue(cacheKey, out bool containsTransparentPixels))
            {
                containsTransparentPixels = ContainsFullyTransparentPixels(texture);
                transparentTextureCache[cacheKey] = containsTransparentPixels;
            }

            return containsTransparentPixels ? GetOpaTextureName(albedoTextureName) : null;
        }

        private static string GetAlbedoTextureName(FMAT material)
        {
            MatTexture albedoMap = material.TextureMaps.OfType<MatTexture>().FirstOrDefault(map =>
                string.Equals(map.SamplerName, "_a0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(map.FragShaderSampler, "_a0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(map.SamplerName, "albedo0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(map.FragShaderSampler, "albedo0", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(albedoMap?.Name))
                return albedoMap.Name;

            return GetTextureReferenceNames(material).FirstOrDefault(texture => ContainsIgnoreCase(texture, "_Alb"));
        }

        private static string GetOpaTextureName(string albedoTextureName)
        {
            if (string.IsNullOrWhiteSpace(albedoTextureName))
                return null;

            int index = albedoTextureName.LastIndexOf("_Alb", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return albedoTextureName + "_Opa";
            return albedoTextureName.Substring(0, index) + "_Opa" + albedoTextureName.Substring(index + 4);
        }

        private static bool ContainsFullyTransparentPixels(TextureData texture)
        {
            using (Bitmap bitmap = texture.GetBitmap())
            using (Bitmap rgba = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb))
            {
                BitmapData data = rgba.LockBits(new Rectangle(0, 0, rgba.Width, rgba.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] pixels = new byte[Math.Abs(data.Stride) * data.Height];
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    for (int y = 0; y < data.Height; y++)
                    {
                        int row = y * Math.Abs(data.Stride);
                        for (int x = 0; x < data.Width; x++)
                        {
                            if (pixels[row + (x * 4) + 3] == 0)
                                return true;
                        }
                    }
                }
                finally
                {
                    rgba.UnlockBits(data);
                }
            }
            return false;
        }

        private static TextureData CreateOpaTexture(TextureData albedoTexture, BNTX sourceBntx, string opaTextureName)
        {
            using (Bitmap bitmap = albedoTexture.GetBitmap())
            using (Bitmap rgba = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb))
            using (Bitmap mask = new Bitmap(rgba.Width, rgba.Height, PixelFormat.Format32bppArgb))
            {
                BitmapData sourceData = rgba.LockBits(new Rectangle(0, 0, rgba.Width, rgba.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                BitmapData targetData = mask.LockBits(new Rectangle(0, 0, mask.Width, mask.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                bool containsTransparentPixels = false;
                try
                {
                    byte[] sourcePixels = new byte[Math.Abs(sourceData.Stride) * sourceData.Height];
                    byte[] targetPixels = new byte[Math.Abs(targetData.Stride) * targetData.Height];
                    Marshal.Copy(sourceData.Scan0, sourcePixels, 0, sourcePixels.Length);
                    for (int y = 0; y < sourceData.Height; y++)
                    {
                        int sourceRow = y * Math.Abs(sourceData.Stride);
                        int targetRow = y * Math.Abs(targetData.Stride);
                        for (int x = 0; x < sourceData.Width; x++)
                        {
                            int sourceOffset = sourceRow + (x * 4);
                            int targetOffset = targetRow + (x * 4);
                            byte value = sourcePixels[sourceOffset + 3] == 0 ? (byte)0 : (byte)255;
                            containsTransparentPixels |= value == 0;
                            targetPixels[targetOffset] = value;
                            targetPixels[targetOffset + 1] = value;
                            targetPixels[targetOffset + 2] = value;
                            targetPixels[targetOffset + 3] = 255;
                        }
                    }
                    Marshal.Copy(targetPixels, 0, targetData.Scan0, targetPixels.Length);
                }
                finally
                {
                    rgba.UnlockBits(sourceData);
                    mask.UnlockBits(targetData);
                }

                if (!containsTransparentPixels)
                    return null;

                TextureImporterSettings settings = new TextureImporterSettings
                {
                    DefaultFormat = albedoTexture.Format,
                    Alignment = (int)albedoTexture.Texture.Alignment,
                };
                settings.LoadBitMap(mask, opaTextureName + ".png");
                settings.MipCount = Math.Max(1, albedoTexture.MipCount);
                settings.DataBlockOutput.Add(settings.GenerateMips(STCompressionMode.Normal, false));
                Syroot.NintenTools.NSW.Bntx.Texture generated = settings.FromBitMap(settings.DataBlockOutput, settings);
                generated.Name = opaTextureName;
                TextureData texture = new TextureData(generated, sourceBntx.BinaryTexFile)
                {
                    Text = opaTextureName,
                    ParentBNTX = sourceBntx,
                };
                return texture;
            }
        }

        private static bool IsOpaMap(MatTexture map)
        {
            return IsOpaSampler(map?.SamplerName) ||
                   IsOpaSampler(map?.FragShaderSampler) ||
                   string.Equals(map?.SamplerName, "transparency0", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(map?.FragShaderSampler, "transparency0", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOpaSampler(string sampler)
        {
            return string.Equals(sampler, "_op0", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sampler, "_o0", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(sampler, "_t0", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SupportsGeneratedOpa(FMAT material)
        {
            if (material == null || IsGlassMaterialName(material.Text) || IsGlassTileDistantMaterialName(material.Text))
                return false;

            string shaderArchive = material.Material?.ShaderAssign?.ShaderArchiveName ?? material.shaderassign?.ShaderArchive ?? "";
            return shaderArchive.IndexOf("Blitz_UBER", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasEmissionSampler(FMAT material)
        {
            return material.TextureMaps.OfType<MatTexture>().Any(map =>
                string.Equals(map.SamplerName, "_e0", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(map.Name));
        }

        private static List<string> GetTextureReferenceNames(FMAT material)
        {
            List<string> names = new List<string>();

            if (material.Material?.TextureRefs != null)
                names.AddRange(material.Material.TextureRefs);

            if (material.MaterialU?.TextureRefs != null)
                names.AddRange(material.MaterialU.TextureRefs.Select(texture => texture.Name));

            return names;
        }

        private static bool IsBakeMap(MatTexture map)
        {
            return GetBakeSlot(map) >= 0;
        }

        private static int GetBakeSlot(MatTexture map)
        {
            string sampler = map?.SamplerName ?? "";
            string fragmentSampler = map?.FragShaderSampler ?? "";
            if (sampler.Equals("bake0", StringComparison.OrdinalIgnoreCase) ||
                sampler.Equals("_b0", StringComparison.OrdinalIgnoreCase) ||
                fragmentSampler.Equals("bake0", StringComparison.OrdinalIgnoreCase) ||
                fragmentSampler.Equals("_b0", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (sampler.Equals("bake1", StringComparison.OrdinalIgnoreCase) ||
                sampler.Equals("_b1", StringComparison.OrdinalIgnoreCase) ||
                fragmentSampler.Equals("bake1", StringComparison.OrdinalIgnoreCase) ||
                fragmentSampler.Equals("_b1", StringComparison.OrdinalIgnoreCase))
                return 1;
            return -1;
        }

        private static bool IsGlassMaterialName(string materialName)
        {
            return materialName?.StartsWith("Mirror", StringComparison.OrdinalIgnoreCase) == true ||
                   materialName?.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetGlassPresetName(FMAT material)
        {
            string signature = GetSignature(material, null);
            bool needsAlbedo = signature.IndexOf("Alb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                !string.IsNullOrWhiteSpace(GetAlbedoTextureName(material));
            if (!needsAlbedo)
                return "Glass.bfmat";

            bool needsEmission = signature.IndexOf("Emm", StringComparison.OrdinalIgnoreCase) >= 0;
            bool needsOpacity = signature.IndexOf("Opa", StringComparison.OrdinalIgnoreCase) >= 0 ||
                material.TextureMaps.OfType<MatTexture>().Any(map => IsOpaMap(map) && !string.IsNullOrWhiteSpace(map.Name));

            if (needsEmission || needsOpacity)
                return "Glass_AlbNrmRghOpaMtlEmm.bfmat";

            return "Glass_AlbNrmRgh.bfmat";
        }

        private static bool IsGlassTileDistantMaterialName(string materialName)
        {
            return materialName?.IndexOf("GlassTileDistant", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddPresetTextureTransfer(List<Splatoon3TextureTransfer> transfers, BNTX targetBntx, string texturePath)
        {
            if (!File.Exists(texturePath))
                return;

            string textureName = Path.GetFileNameWithoutExtension(texturePath);
            if (transfers.Any(transfer => transfer.TextureName.Equals(textureName, StringComparison.OrdinalIgnoreCase)))
                return;

            transfers.Add(new Splatoon3TextureTransfer
            {
                BftexPath = texturePath,
                ReplacesExisting = targetBntx.Textures.ContainsKey(textureName),
                IsBake = false,
            });
        }

        private static void AddBasicTextureTransfers(List<Splatoon3TextureTransfer> transfers, BNTX targetBntx, string materialsFolder)
        {
            if (!Directory.Exists(materialsFolder))
                return;

            foreach (string texturePath in Directory.GetFiles(materialsFolder, "Basic_*.png"))
            {
                string textureName = Path.GetFileNameWithoutExtension(texturePath);
                if (transfers.Any(transfer => transfer.TextureName.Equals(textureName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                transfers.Add(new Splatoon3TextureTransfer
                {
                    BftexPath = texturePath,
                    DefaultFormat = textureName.Equals("Basic_Emm", StringComparison.OrdinalIgnoreCase) ? TEX_FORMAT.BC5_UNORM : (TEX_FORMAT?)null,
                    ReplacesExisting = targetBntx.Textures.ContainsKey(textureName),
                    IsBake = false,
                });
            }
        }

        private static bool IsReferencedTexture(string textureName, HashSet<string> referencedTextures)
        {
            if (referencedTextures.Contains(textureName))
                return true;

            foreach (string referencedTexture in referencedTextures)
            {
                if (!referencedTexture.EndsWith(".0", StringComparison.OrdinalIgnoreCase))
                    continue;

                string prefix = referencedTexture.Substring(0, referencedTexture.Length - 1);
                if (textureName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(textureName.Substring(prefix.Length), out int frame) && frame >= 0)
                    return true;
            }

            return false;
        }

        private static bool TryGetPaintableValue(FMAT material, out bool paintable)
        {
            paintable = false;

            if (material.Material?.UserDatas != null)
            {
                foreach (var data in material.Material.UserDatas)
                {
                    if (data.Name.Equals("Paintable", StringComparison.OrdinalIgnoreCase) && TryDecodeUserDataFlag(data, out paintable))
                        return true;
                }
            }

            if (material.MaterialU?.UserData != null)
            {
                foreach (var data in material.MaterialU.UserData)
                {
                    if (data.Key.Equals("Paintable", StringComparison.OrdinalIgnoreCase) && TryDecodeUserDataFlag(data.Value, out paintable))
                        return true;
                }
            }

            bool found = false;
            bool anyPaintable = false;
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GroundPaint", "WallPaint", "ObjPaint" };

            if (material.Material?.UserDatas != null)
            {
                foreach (var data in material.Material.UserDatas)
                {
                    if (!keys.Contains(data.Name) || !TryDecodeUserDataFlag(data, out bool value))
                        continue;

                    found = true;
                    anyPaintable |= value;
                }
            }

            if (material.MaterialU?.UserData != null)
            {
                foreach (var data in material.MaterialU.UserData)
                {
                    if (!keys.Contains(data.Key) || !TryDecodeUserDataFlag(data.Value, out bool value))
                        continue;

                    found = true;
                    anyPaintable |= value;
                }
            }

            paintable = anyPaintable;
            return found;
        }

        private static bool TryDecodeUserDataFlag(object userData, out bool value)
        {
            value = false;
            if (userData == null)
                return false;

            FieldInfo field = userData.GetType()
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault();

            if (field == null)
                return false;

            object raw = field.GetValue(userData);

            if (raw is int[] ints && ints.Length > 0)
            {
                value = ints[0] == 1;
                return true;
            }

            if (raw is uint[] uints && uints.Length > 0)
            {
                value = uints[0] == 1;
                return true;
            }

            if (raw is byte[] bytes && bytes.Length > 0)
            {
                value = bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) == 1 : bytes[0] == 1;
                return true;
            }

            return false;
        }

        private static bool ContainsIgnoreCase(string value, string token)
        {
            return value?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
