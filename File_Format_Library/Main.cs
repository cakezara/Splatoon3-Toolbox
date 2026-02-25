using FirstPlugin.Forms;
using FirstPlugin.LuigisMansion.DarkMoon;
using FirstPlugin.LuigisMansion3;
using FirstPlugin;
using Bfres.Structs;
using PluginContracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Toolbox.Library;
using Toolbox.Library.Forms;
using Toolbox.Library.IO;

namespace FirstPlugin
{
    public class FirstPlugin : IPlugin
    {
        public string Name => "First Plugin";
        public string Description => "Custom Toolbox Extensions + Splatoon 3 Paint Fix";
        public string Author => "KXG";
        public string Version => "1.0";

        public Type[] Types
        {
            get
            {
                List<Type> types = new List<Type>();

                types.AddRange(LoadFileFormats());
                types.AddRange(LoadCompressionFormats());
                types.AddRange(LoadMenus());

                return types.ToArray();
            }
        }

        public void Load()
        {
            Config.StartupFromFile(Runtime.ExecutableDir + "/Lib/Plugins/config.xml");
        }

        public void Unload()
        {
            PluginRuntime.bntxContainers.Clear();
        }


        class MenuExt : IMenuExtension
        {
            public STToolStripItem[] FileMenuExtensions => null;
            public STToolStripItem[] ToolsMenuExtensions => toolsExt;
            public STToolStripItem[] TitleBarExtensions => null;

            readonly STToolStripItem[] toolsExt = new STToolStripItem[1];

            public MenuExt()
            {
                toolsExt[0] = new STToolStripItem("Splatoon 3");
                toolsExt[0].DropDownItems.Add(new STToolStripItem("Texture Replacement", OpenTextureReplacementWindow));
                toolsExt[0].DropDownItems.Add(new STToolStripItem("Apply Paint Fix", ApplyPaintFix));
            }

            private void ApplyPaintFix(object sender, EventArgs e)
            {
                if (!(LibraryGUI.GetActiveForm() is ObjectEditor editor))
                {
                    MessageBox.Show("Open a BFRES model first.");
                    return;
                }

                var file = editor.GetActiveFile();

                if (file is BFRES bfres)
                {
                    int fixedCount = PaintabilityFix.ApplyFix(bfres);

                    if (fixedCount > 0)
                    {
                        LibraryGUI.UpdateViewport();
                        MessageBox.Show($"Paint Fix Applied!");
                    }
                    else
                    {
                        MessageBox.Show("No paint issues detected.");
                    }
                }
                else
                {
                    MessageBox.Show("Active file is not a BFRES model.");
                }
            }

            private void RunAlbToOpa(object sender, EventArgs e)
            {
                RunTextureReplacementTool("AlbToOpa.py", "_Alb", "_Opa", false);
            }

            private void RunSpmToRgh(object sender, EventArgs e)
            {
                RunTextureReplacementTool("SpmToRgh.py", "_Spm", "_Rgh", true);
            }

            private void OpenTextureReplacementWindow(object sender, EventArgs e)
            {
                if (!TryGetBfres(out BFRES bfres))
                    return;

                WriteLog($"[{bfres.Text}] Opened Texture Replacements");

                new TextureReplacementToolForm(
                    bfres,
                    AutoMatchMaterialNamesWithTexture,
                    () => RunTextureReplacementTool("AlbToOpa.py", "_Alb", "_Opa", false),
                    () => RunTextureReplacementTool("SpmToRgh.py", "_Spm", "_Rgh", true))
                    .ShowDialog();
            }

            private void AutoMatchMaterialNamesWithTexture()
            {
                if (!TryGetBfres(out BFRES bfres))
                    return;

                WriteLog($"[{bfres.Text}] Starting texture scan");

                List<TextureAdapter> allTextures = GetAllBfresTextures(bfres);
                if (allTextures.Count == 0)
                {
                    MessageBox.Show("The active BFRES has no textures.");
                    WriteLog($"[{bfres.Text}] Aborted: no textures available");
                    return;
                }

                List<FMDL> models = GetBfresModels(bfres);
                if (models.Count == 0)
                {
                    MessageBox.Show("The active BFRES has no models/materials.");
                    WriteLog($"[{bfres.Text}] Aborted: no models/materials available");
                    return;
                }

                FMDL selectedModel = PromptModelSelection(models);
                if (selectedModel == null)
                {
                    WriteLog("[AutoTextures] No model selected");
                    return;
                }

                WriteLog($"[AutoTextures] Selected model: {selectedModel.Text} (Materials: {selectedModel.materials.Count})");

                Dictionary<string, string> textureLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tex in allTextures)
                {
                    if (!textureLookup.ContainsKey(tex.Name))
                        textureLookup.Add(tex.Name, tex.Name);
                }

                int mapsProcessed = 0;
                int mapsMissing = 0;
                int mapsUnknownToken = 0;
                List<AutoMatchProposal> proposals = new List<AutoMatchProposal>();

                foreach (var model in new[] { selectedModel })
                {
                    foreach (var material in model.materials.Values)
                    {
                        foreach (var map in material.TextureMaps.OfType<MatTexture>())
                        {
                            mapsProcessed++;

                            if (!TryResolveTextureToken(map, out string token))
                            {
                                mapsUnknownToken++;
                                proposals.Add(new AutoMatchProposal()
                                {
                                    Material = material,
                                    Map = map,
                                    MaterialName = material.Text,
                                    SamplerDisplay = GetSamplerDisplay(map),
                                    Token = "(unknown)",
                                    CurrentTextureName = map.Name,
                                    TargetTextureName = "",
                                    Status = "Unknown token",
                                    IsApplicable = false,
                                });
                                continue;
                            }

                            if (!TryFindTextureForMaterial(textureLookup, material.Text, token, map, out string matchedTextureName, out string matchMode))
                            {
                                mapsMissing++;
                                proposals.Add(new AutoMatchProposal()
                                {
                                    Material = material,
                                    Map = map,
                                    MaterialName = material.Text,
                                    SamplerDisplay = GetSamplerDisplay(map),
                                    Token = token,
                                    CurrentTextureName = map.Name,
                                    TargetTextureName = "",
                                    Status = "Missing target",
                                    IsApplicable = false,
                                });
                                continue;
                            }

                            if (string.Equals(map.Name, matchedTextureName, StringComparison.OrdinalIgnoreCase))
                            {
                                proposals.Add(new AutoMatchProposal()
                                {
                                    Material = material,
                                    Map = map,
                                    MaterialName = material.Text,
                                    SamplerDisplay = GetSamplerDisplay(map),
                                    Token = token,
                                    CurrentTextureName = map.Name,
                                    TargetTextureName = matchedTextureName,
                                    Status = "Already matched",
                                    IsApplicable = false,
                                });
                            }
                            else
                            {
                                string status = "Will replace";
                                if (!string.Equals(matchMode, "Direct", StringComparison.OrdinalIgnoreCase))
                                    status = $"Will replace ({matchMode})";

                                proposals.Add(new AutoMatchProposal()
                                {
                                    Material = material,
                                    Map = map,
                                    MaterialName = material.Text,
                                    SamplerDisplay = GetSamplerDisplay(map),
                                    Token = token,
                                    CurrentTextureName = map.Name,
                                    TargetTextureName = matchedTextureName,
                                    Status = status,
                                    IsApplicable = true,
                                });
                            }
                        }
                    }
                }

                if (proposals.Count == 0)
                {
                    WriteLog($"[AutoTextures] Scan complete for model {selectedModel.Text}. No candidates found. Processed={mapsProcessed}, Missing={mapsMissing}, Unknown={mapsUnknownToken}");
                    MessageBox.Show(
                        $"Auto match scan complete for model \"{selectedModel.Text}\".\n\nNo replacement candidates were found.\nTexture maps processed: {mapsProcessed}\nMissing textures: {mapsMissing}\nUnknown sampler tokens: {mapsUnknownToken}");
                    return;
                }

                WriteLog($"[AutoTextures] Scan complete for model {selectedModel.Text}. Candidates={proposals.Count(x => x.IsApplicable)}, Rows={proposals.Count}, Processed={mapsProcessed}, Missing={mapsMissing}, Unknown={mapsUnknownToken}");
                List<AutoMatchProposal> selected = PromptAutoMatchSelection(proposals, mapsProcessed, mapsMissing, mapsUnknownToken);
                if (selected == null)
                {
                    WriteLog("[AutoTextures] Cancelled at preview stage.");
                    return;
                }

                if (selected.Count == 0)
                {
                    MessageBox.Show("No matches selected.");
                    WriteLog("[AutoTextures] No matches selected to apply.");
                    return;
                }

                WriteLog($"[AutoTextures] Applying {selected.Count} selected matches for model {selectedModel.Text}.");
                int mapsMatched = 0;
                HashSet<FMAT> changedMaterials = new HashSet<FMAT>();
                foreach (var proposal in selected)
                {
                    proposal.Map.Name = proposal.TargetTextureName;
                    proposal.Map.textureState = STGenericMatTexture.TextureState.Replaced;
                    changedMaterials.Add(proposal.Material);
                    mapsMatched++;
                }

                foreach (var material in changedMaterials)
                {
                    if (material.Material != null)
                        BfresSwitch.SetMaterial(material, material.Material);
                    if (material.MaterialU != null)
                        BfresWiiU.SetMaterial(material, material.MaterialU, material.GetResFileU());

                    material.UpdateTextureMaps();
                }

                LibraryGUI.UpdateViewport();
                WriteLog($"[AutoTextures] Complete for model {selectedModel.Text}. Applied={mapsMatched}, MaterialsChanged={changedMaterials.Count}");
                MessageBox.Show(
                    $"Auto match complete for model \"{selectedModel.Text}\"!\n\nCandidates found: {proposals.Count(x => x.IsApplicable)}\nApplied: {mapsMatched}\nMaterials changed: {changedMaterials.Count}\nTexture maps processed: {mapsProcessed}\nMissing textures: {mapsMissing}\nUnknown sampler tokens: {mapsUnknownToken}");
            }

            private void WriteLog(string message)
            {
                try { Console.WriteLine(message); } catch { }
                try { STConsole.WriteLine(message); } catch { }
            }

            private string GetSamplerDisplay(MatTexture map)
            {
                string sampler = map?.SamplerName ?? "";
                string frag = map?.FragShaderSampler ?? "";

                if (!string.IsNullOrWhiteSpace(sampler) &&
                    !string.IsNullOrWhiteSpace(frag) &&
                    !sampler.Equals(frag, StringComparison.OrdinalIgnoreCase))
                    return $"{sampler} / {frag}";

                if (!string.IsNullOrWhiteSpace(frag))
                    return frag;

                if (!string.IsNullOrWhiteSpace(sampler))
                    return sampler;

                return "(none)";
            }

            private List<FMDL> GetBfresModels(BFRES bfres)
            {
                List<FMDL> models = new List<FMDL>();

                foreach (TreeNode node in bfres.Nodes)
                {
                    if (!(node is BFRESGroupNode group) || group.Type != BRESGroupType.Models)
                        continue;

                    foreach (var model in group.Nodes.OfType<FMDL>())
                    {
                        if (!models.Contains(model))
                            models.Add(model);
                    }

                    if (group.Nodes.Count == 0)
                    {
                        foreach (var child in group.ResourceNodes.Values.OfType<FMDL>())
                        {
                            if (!models.Contains(child))
                                models.Add(child);
                        }
                    }
                }

                return models;
            }

            private bool TryResolveTextureToken(MatTexture map, out string token)
            {
                token = null;
                if (map == null)
                    return false;

                if (TryResolveTextureTokenFromSampler(map.FragShaderSampler, out token))
                    return true;

                if (TryResolveTextureTokenFromSampler(map.SamplerName, out token))
                    return true;

                if (TryResolveTextureTokenFromName(map.Name, out token))
                    return true;

                if (TryResolveTextureTokenFromType(map.Type, out token))
                    return true;

                return false;
            }

            private bool TryResolveTextureTokenFromSampler(string samplerName, out string token)
            {
                token = null;
                if (string.IsNullOrWhiteSpace(samplerName))
                    return false;

                string s = samplerName.Trim().ToLowerInvariant();

                switch (s)
                {
                    case "_a0":
                    case "_a1":
                    case "albedo0":
                    case "albedo1":
                        token = "Alb";
                        return true;
                    case "_n0":
                    case "_n1":
                    case "normal0":
                    case "normal1":
                        token = "Nrm";
                        return true;
                    case "_r0":
                    case "_r1":
                        token = "Rgh";
                        return true;
                    case "_m0":
                    case "_m1":
                    case "_mt0":
                    case "metalness0":
                        token = "Mtl";
                        return true;
                    case "_s0":
                    case "_s1":
                        token = "Spm";
                        return true;
                    case "_t0":
                        token = "Opa";
                        return true;
                    case "_e0":
                    case "_e1":
                    case "emissive0":
                    case "emissive1":
                        token = "Emm";
                        return true;
                    case "_ao0":
                    case "_ao1":
                        token = "AO";
                        return true;
                    case "_su0":
                    case "_cp0":
                        token = "Col";
                        return true;
                    case "bake0":
                    case "_b0":
                        token = "BakeDummy00";
                        return true;
                    case "bake1":
                    case "_b1":
                        token = "LightBakeDummy00";
                        return true;
                }

                if (ContainsToken(s, "albedo") || ContainsToken(s, "diffuse"))
                {
                    token = "Alb";
                    return true;
                }
                if (ContainsToken(s, "normal"))
                {
                    token = "Nrm";
                    return true;
                }
                if (ContainsToken(s, "rough"))
                {
                    token = "Rgh";
                    return true;
                }
                if (ContainsToken(s, "metal"))
                {
                    token = "Mtl";
                    return true;
                }
                if (ContainsToken(s, "spec"))
                {
                    token = "Spm";
                    return true;
                }
                if (ContainsToken(s, "alpha") || ContainsToken(s, "opa"))
                {
                    token = "Opa";
                    return true;
                }
                if (ContainsToken(s, "occlusion") || ContainsToken(s, "_ao"))
                {
                    token = "AO";
                    return true;
                }
                if (ContainsToken(s, "emiss"))
                {
                    token = "Emm";
                    return true;
                }
                if (ContainsToken(s, "bake0"))
                {
                    token = "BakeDummy00";
                    return true;
                }
                if (ContainsToken(s, "bake1"))
                {
                    token = "LightBakeDummy00";
                    return true;
                }

                return false;
            }

            private bool TryResolveTextureTokenFromType(STGenericMatTexture.TextureType type, out string token)
            {
                token = null;
                switch (type)
                {
                    case STGenericMatTexture.TextureType.Diffuse:
                    case STGenericMatTexture.TextureType.DiffuseLayer2:
                        token = "Alb";
                        return true;
                    case STGenericMatTexture.TextureType.Normal:
                        token = "Nrm";
                        return true;
                    case STGenericMatTexture.TextureType.Roughness:
                        token = "Rgh";
                        return true;
                    case STGenericMatTexture.TextureType.Metalness:
                        token = "Mtl";
                        return true;
                    case STGenericMatTexture.TextureType.Specular:
                        token = "Spm";
                        return true;
                    case STGenericMatTexture.TextureType.AO:
                        token = "AO";
                        return true;
                    case STGenericMatTexture.TextureType.Transparency:
                        token = "Opa";
                        return true;
                    case STGenericMatTexture.TextureType.Emission:
                        token = "Emm";
                        return true;
                    case STGenericMatTexture.TextureType.TeamColor:
                        token = "Col";
                        return true;
                    case STGenericMatTexture.TextureType.Shadow:
                        token = "BakeDummy00";
                        return true;
                    case STGenericMatTexture.TextureType.Light:
                        token = "LightBakeDummy00";
                        return true;
                }
                return false;
            }

            private bool TryResolveTextureTokenFromName(string value, out string token)
            {
                token = null;
                if (string.IsNullOrWhiteSpace(value))
                    return false;

                if (ContainsToken(value, "_Alb") || ContainsToken(value, "_a0") || ContainsToken(value, "albedo") || ContainsToken(value, "diffuse"))
                {
                    token = "Alb";
                    return true;
                }

                if (ContainsToken(value, "_Nrm") || ContainsToken(value, "_n0") || ContainsToken(value, "normal"))
                {
                    token = "Nrm";
                    return true;
                }

                if (ContainsToken(value, "_Rgh") || ContainsToken(value, "_r0") || ContainsToken(value, "rough"))
                {
                    token = "Rgh";
                    return true;
                }

                if (ContainsToken(value, "_Mtl") || ContainsToken(value, "_m0") || ContainsToken(value, "metal"))
                {
                    token = "Mtl";
                    return true;
                }

                if (ContainsToken(value, "_Spm") || ContainsToken(value, "_s0") || ContainsToken(value, "spec"))
                {
                    token = "Spm";
                    return true;
                }

                if (ContainsToken(value, "_Opa") || ContainsToken(value, "_t0") || ContainsToken(value, "opa") || ContainsToken(value, "alpha"))
                {
                    token = "Opa";
                    return true;
                }

                if (ContainsToken(value, "_ao") || ContainsToken(value, "_AO") || ContainsToken(value, "occlusion"))
                {
                    token = "AO";
                    return true;
                }

                if (ContainsToken(value, "_Emm") || ContainsToken(value, "_Emi") || ContainsToken(value, "_e0") || ContainsToken(value, "emiss"))
                {
                    token = "Emm";
                    return true;
                }

                if (ContainsToken(value, "_Col") || ContainsToken(value, "_cp0") || ContainsToken(value, "_su0"))
                {
                    token = "Col";
                    return true;
                }

                if (ContainsToken(value, "BakeDummy00") || ContainsToken(value, "bake0") || ContainsToken(value, "_b0"))
                {
                    token = "BakeDummy00";
                    return true;
                }

                if (ContainsToken(value, "LightBakeDummy00") || ContainsToken(value, "bake1") || ContainsToken(value, "_b1"))
                {
                    token = "LightBakeDummy00";
                    return true;
                }

                return false;
            }

            private bool TryFindTextureForMaterial(
                Dictionary<string, string> textureLookup,
                string materialName,
                string token,
                MatTexture map,
                out string matchedTextureName,
                out string matchMode)
            {
                matchedTextureName = null;
                matchMode = "Direct";
                if (textureLookup == null || textureLookup.Count == 0 || string.IsNullOrWhiteSpace(token))
                    return false;

                List<string> candidates = new List<string>()
                {
                };

                if (token.Equals("BakeDummy00", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("LightBakeDummy00", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(token);
                }
                else
                {
                    foreach (var materialCandidate in BuildMaterialNameCandidates(materialName))
                    {
                        candidates.Add($"{materialCandidate}_{token}");

                        if (token.Equals("AO", StringComparison.OrdinalIgnoreCase))
                            candidates.Add($"{materialCandidate}_ao");
                    }
                }

                foreach (var candidate in candidates)
                {
                    if (!TryLookupTextureByCandidate(textureLookup, candidate, out string exact))
                        continue;

                    matchedTextureName = exact;
                    matchMode = "Direct";
                    return true;
                }

                if (token.Equals("Rgh", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsRoughnessSamplerR0(map))
                    {
                        foreach (var materialCandidate in BuildMaterialNameCandidates(materialName))
                        {
                            string spmCandidate = $"{materialCandidate}_Spm";
                            if (TryLookupTextureByCandidate(textureLookup, spmCandidate, out string spmTexture))
                            {
                                matchedTextureName = spmTexture;
                                matchMode = "Rgh->Spm fallback";
                                return true;
                            }
                        }
                    }

                    if (TryLookupTextureByCandidate(textureLookup, "Basic_Rgh", out string basicRgh))
                    {
                        matchedTextureName = basicRgh;
                        matchMode = "Basic_Rgh fallback";
                        return true;
                    }
                }

                if (token.Equals("Mtl", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryLookupTextureByCandidate(textureLookup, "Basic_Mtl", out string basicMtl))
                    {
                        matchedTextureName = basicMtl;
                        matchMode = "Basic_Mtl fallback";
                        return true;
                    }
                }

                return false;
            }

            private bool IsRoughnessSamplerR0(MatTexture map)
            {
                if (map == null)
                    return false;

                string sampler = map.SamplerName ?? "";
                string frag = map.FragShaderSampler ?? "";
                return sampler.Equals("_r0", StringComparison.OrdinalIgnoreCase) ||
                       frag.Equals("_r0", StringComparison.OrdinalIgnoreCase) ||
                       sampler.Equals("r0", StringComparison.OrdinalIgnoreCase) ||
                       frag.Equals("r0", StringComparison.OrdinalIgnoreCase);
            }

            private IEnumerable<string> BuildMaterialNameCandidates(string materialName)
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(materialName))
                    return names;

                AddMaterialCandidateVariants(names, materialName);

                string stripped = materialName;
                bool changed;
                do
                {
                    changed = false;
                    foreach (var suffix in new[] { "_np", "_wp", "_sl" })
                    {
                        if (stripped.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        {
                            stripped = stripped.Substring(0, stripped.Length - suffix.Length);
                            AddMaterialCandidateVariants(names, stripped);
                            changed = true;
                        }
                    }
                }
                while (changed);

                return names;
            }

            private void AddMaterialCandidateVariants(HashSet<string> names, string value)
            {
                if (names == null || string.IsNullOrWhiteSpace(value))
                    return;

                names.Add(value);

                string withoutDotZero = RemoveDotZero(value);
                if (!string.IsNullOrWhiteSpace(withoutDotZero))
                    names.Add(withoutDotZero);
            }

            private bool TryLookupTextureByCandidate(
                Dictionary<string, string> textureLookup,
                string candidate,
                out string exactTextureName)
            {
                exactTextureName = null;
                if (textureLookup == null || string.IsNullOrWhiteSpace(candidate))
                    return false;

                if (textureLookup.TryGetValue(candidate, out string direct))
                {
                    exactTextureName = direct;
                    return true;
                }

                string normalizedCandidate = RemoveDotZero(candidate);
                if (string.IsNullOrWhiteSpace(normalizedCandidate))
                    return false;

                foreach (var pair in textureLookup)
                {
                    if (RemoveDotZero(pair.Key).Equals(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                    {
                        exactTextureName = pair.Value;
                        return true;
                    }
                }

                return false;
            }

            private string RemoveDotZero(string value)
            {
                if (string.IsNullOrEmpty(value))
                    return value;

                return value.Replace(".0", "");
            }

            private bool ContainsToken(string value, string token)
            {
                return value?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private void RunTextureReplacementTool(string scriptName, string sourceSuffix, string targetSuffix, bool scriptWritesToNewTexturesFolder)
            {
                if (!TryGetBfres(out BFRES bfres))
                    return;

                if (!TryResolveScriptPath(scriptName, out string scriptPath))
                {
                    MessageBox.Show($"Could not find {scriptName}. Place it in the project root or executable directory.");
                    return;
                }

                List<TextureAdapter> allTextures = GetAllBfresTextures(bfres);
                if (allTextures.Count == 0)
                {
                    MessageBox.Show("The active BFRES has no textures to process.");
                    return;
                }

                List<TextureAdapter> sourceTextures = GetSourceTextures(allTextures, sourceSuffix);
                if (sourceTextures.Count == 0)
                {
                    MessageBox.Show($"No textures ending in \"{sourceSuffix}\" were found in the open BFRES.");
                    return;
                }

                List<string> selectedSourceNames = PromptTextureSelection(sourceTextures, sourceSuffix, targetSuffix);
                if (selectedSourceNames == null)
                    return;

                if (selectedSourceNames.Count == 0)
                {
                    MessageBox.Show("No textures selected.");
                    return;
                }

                HashSet<string> selectedSet = new HashSet<string>(selectedSourceNames, StringComparer.OrdinalIgnoreCase);
                sourceTextures = sourceTextures.Where(x => selectedSet.Contains(x.Name)).ToList();

                List<TextureProcessJob> jobs = BuildProcessJobs(sourceTextures, sourceSuffix, targetSuffix, scriptWritesToNewTexturesFolder);
                if (jobs.Count == 0)
                {
                    MessageBox.Show($"No valid texture names found containing \"{sourceSuffix}\".");
                    return;
                }

                Dictionary<string, TextureAdapter> textureLookup = BuildTextureLookup(allTextures);

                int preCreatedTargets = 0;
                foreach (var job in jobs)
                {
                    if (textureLookup.ContainsKey(job.TargetName))
                        continue;

                    if (TryCreateMissingTarget(job.SourceTexture, job.TargetName, out TextureAdapter createdTexture))
                    {
                        textureLookup[job.TargetName] = createdTexture;
                        preCreatedTargets++;
                    }
                }

                if (preCreatedTargets > 0)
                    LibraryGUI.UpdateViewport();

                string tempRoot = Path.Combine(Path.GetTempPath(), "SwitchToolbox_Splatoon3Tools", Guid.NewGuid().ToString("N"));
                string inputFolder = Path.Combine(tempRoot, "Textures");
                Directory.CreateDirectory(inputFolder);

                int exported = 0;
                foreach (var job in jobs)
                {
                    string outputPath = Path.Combine(inputFolder, job.TempInputFileName);
                    using (Bitmap bitmap = job.SourceTexture.GetBitmap())
                    {
                        bitmap.Save(outputPath, ImageFormat.Png);
                    }
                    exported++;
                }

                Cursor previousCursor = Cursor.Current;
                Cursor.Current = Cursors.WaitCursor;

                try
                {
                    if (!RunPythonScript(scriptPath, tempRoot, out string stdErr))
                    {
                        MessageBox.Show(
                            $"Failed to run {scriptName}.\n\n{stdErr}\n\nEnsure Python is installed and Pillow is available (pip install pillow).");
                        return;
                    }

                    string outputFolder = scriptWritesToNewTexturesFolder
                        ? Path.Combine(tempRoot, "New_Textures")
                        : inputFolder;

                    if (!Directory.Exists(outputFolder))
                    {
                        MessageBox.Show($"Expected output folder not found:\n{outputFolder}");
                        return;
                    }

                    int generated = 0;
                    int replaced = 0;
                    int missingTargets = 0;
                    int createdTargets = 0;

                    foreach (var job in jobs)
                    {
                        string outputFile = Path.Combine(outputFolder, job.TempOutputFileName);
                        if (!File.Exists(outputFile))
                            continue;

                        generated++;

                        if (!textureLookup.TryGetValue(job.TargetName, out TextureAdapter targetTexture))
                        {
                            if (!TryCreateMissingTarget(job.SourceTexture, job.TargetName, out targetTexture))
                            {
                                missingTargets++;
                                continue;
                            }

                            textureLookup[job.TargetName] = targetTexture;
                            createdTargets++;
                        }

                        using (Bitmap sourceBitmap = new Bitmap(outputFile))
                        {
                            targetTexture.SetBitmap(new Bitmap(sourceBitmap));
                        }

                        replaced++;
                    }

                    LibraryGUI.UpdateViewport();
                    MessageBox.Show(
                        $"{Path.GetFileNameWithoutExtension(scriptName)} complete!\n\nSelected source textures: {jobs.Count}\nExported: {exported}\nGenerated: {generated}\nAdded to Textures: {preCreatedTargets + createdTargets}\nReplaced/Applied: {replaced}\nMissing targets: {missingTargets}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Texture replacement failed.\n\n{ex.Message}");
                }
                finally
                {
                    Cursor.Current = previousCursor;
                    TryDeleteDirectory(tempRoot);
                }
            }

            private class TextureAdapter
            {
                public string Name;
                public Func<Bitmap> GetBitmap;
                public Action<Bitmap> SetBitmap;
                public Func<string, TextureAdapter> CreateSibling;
            }

            private class TextureProcessJob
            {
                public TextureAdapter SourceTexture;
                public string TargetName;
                public string TempInputFileName;
                public string TempOutputFileName;
            }

            private class AutoMatchProposal
            {
                public FMAT Material;
                public MatTexture Map;
                public string MaterialName;
                public string SamplerDisplay;
                public string Token;
                public string CurrentTextureName;
                public string TargetTextureName;
                public string Status;
                public bool IsApplicable;
            }

            private bool TryGetBfres(out BFRES bfres)
            {
                bfres = null;

                if (!(LibraryGUI.GetActiveForm() is ObjectEditor editor))
                {
                    MessageBox.Show("Open a BFRES file first.");
                    return false;
                }

                bfres = editor.GetActiveFile() as BFRES;
                if (bfres == null)
                {
                    MessageBox.Show("Active file is not a BFRES.");
                    return false;
                }

                return true;
            }

            private List<TextureAdapter> GetAllBfresTextures(BFRES bfres)
            {
                Dictionary<string, TextureAdapter> textures = new Dictionary<string, TextureAdapter>(StringComparer.OrdinalIgnoreCase);

                foreach (TreeNode node in bfres.Nodes)
                {
                    if (node is BFRESGroupNode group && group.Type == BRESGroupType.Textures)
                    {
                        foreach (TreeNode child in group.Nodes)
                        {
                            if (child is FTEX ftex)
                            {
                                var tex = ftex;
                                AddTextureAdapter(textures, tex.Text,
                                    () => tex.GetBitmap(),
                                    (bitmap) => tex.SetImageData(bitmap, 0),
                                    (targetName) =>
                                    {
                                        FTEX created = group.Nodes.OfType<FTEX>().FirstOrDefault(x => x.Text.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                                        if (created == null && group.ResourceNodes.ContainsKey(targetName))
                                            created = group.ResourceNodes[targetName] as FTEX;
                                        if (created == null)
                                        {
                                            string tempBftex = CreateTempBftexPath(tex.Text);
                                            try
                                            {
                                                tex.SaveBinaryTexture(tempBftex);

                                                created = new FTEX();
                                                created.texture = new Syroot.NintenTools.Bfres.Texture();
                                                created.texture.Import(tempBftex, group.GetResFileU());
                                                created.texture.Name = targetName;
                                                created.Read(created.texture);
                                                created.Text = targetName;

                                                group.AddNode(created);
                                                created.LoadOpenGLTexture();
                                            }
                                            catch
                                            {
                                                return null;
                                            }
                                            finally
                                            {
                                                TryDeleteFile(tempBftex);
                                            }
                                        }

                                        var createdTex = created;
                                        return new TextureAdapter()
                                        {
                                            Name = targetName,
                                            GetBitmap = () => createdTex.GetBitmap(),
                                            SetBitmap = (bitmap) => createdTex.SetImageData(bitmap, 0),
                                            CreateSibling = null,
                                        };
                                    });
                            }
                        }

                        if (group.Nodes.Count == 0)
                        {
                            foreach (var child in group.ResourceNodes.Values)
                            {
                                if (child is FTEX ftex)
                                {
                                    var tex = ftex;
                                    AddTextureAdapter(textures, tex.Text,
                                        () => tex.GetBitmap(),
                                        (bitmap) => tex.SetImageData(bitmap, 0),
                                        (targetName) =>
                                        {
                                            FTEX created = group.Nodes.OfType<FTEX>().FirstOrDefault(x => x.Text.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                                            if (created == null && group.ResourceNodes.ContainsKey(targetName))
                                                created = group.ResourceNodes[targetName] as FTEX;
                                            if (created == null)
                                            {
                                                string tempBftex = CreateTempBftexPath(tex.Text);
                                                try
                                                {
                                                    tex.SaveBinaryTexture(tempBftex);

                                                    created = new FTEX();
                                                    created.texture = new Syroot.NintenTools.Bfres.Texture();
                                                    created.texture.Import(tempBftex, group.GetResFileU());
                                                    created.texture.Name = targetName;
                                                    created.Read(created.texture);
                                                    created.Text = targetName;

                                                    group.AddNode(created);
                                                    created.LoadOpenGLTexture();
                                                }
                                                catch
                                                {
                                                    return null;
                                                }
                                                finally
                                                {
                                                    TryDeleteFile(tempBftex);
                                                }
                                            }

                                            var createdTex = created;
                                            return new TextureAdapter()
                                            {
                                                Name = targetName,
                                                GetBitmap = () => createdTex.GetBitmap(),
                                                SetBitmap = (bitmap) => createdTex.SetImageData(bitmap, 0),
                                                CreateSibling = null,
                                            };
                                        });
                                }
                            }
                        }
                    }
                    else if (node is BNTX bntx && bntx.Textures != null)
                    {
                        foreach (var texture in bntx.Textures.Values)
                        {
                            var tex = texture;
                            AddTextureAdapter(textures, tex.Text,
                                () => tex.GetBitmap(),
                                (bitmap) => tex.SetImageData(bitmap, 0),
                                (targetName) =>
                                {
                                    TextureData created;
                                    if (!bntx.Textures.ContainsKey(targetName))
                                    {
                                        string tempBftex = CreateTempBftexPath(tex.Text);
                                        try
                                        {
                                            tex.SaveBinaryTexture(tempBftex);
                                            var imported = new Syroot.NintenTools.NSW.Bntx.Texture();
                                            imported.Import(tempBftex);
                                            imported.Name = targetName;

                                            created = new TextureData(imported, bntx.BinaryTexFile);
                                            created.Text = targetName;
                                            created.ParentBNTX = bntx;

                                            bntx.Nodes.Add(created);
                                            bntx.Textures.Add(targetName, created);
                                            created.LoadOpenGLTexture();
                                        }
                                        catch
                                        {
                                            return null;
                                        }
                                        finally
                                        {
                                            TryDeleteFile(tempBftex);
                                        }
                                    }

                                    if (!bntx.Textures.ContainsKey(targetName))
                                        return null;

                                    created = bntx.Textures[targetName];
                                    var createdTex = created;
                                    return new TextureAdapter()
                                    {
                                        Name = targetName,
                                        GetBitmap = () => createdTex.GetBitmap(),
                                        SetBitmap = (bitmap) => createdTex.SetImageData(bitmap, 0),
                                        CreateSibling = null,
                                    };
                                });
                        }
                    }
                }

                return textures.Values.ToList();
            }

            private void AddTextureAdapter(
                Dictionary<string, TextureAdapter> textures,
                string name,
                Func<Bitmap> getBitmap,
                Action<Bitmap> setBitmap,
                Func<string, TextureAdapter> createSibling)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return;

                if (textures.ContainsKey(name))
                    return;

                textures[name] = new TextureAdapter()
                {
                    Name = name,
                    GetBitmap = getBitmap,
                    SetBitmap = setBitmap,
                    CreateSibling = createSibling,
                };
            }

            private List<TextureAdapter> GetSourceTextures(List<TextureAdapter> textures, string suffix)
            {
                List<TextureAdapter> output = new List<TextureAdapter>();
                foreach (var texture in textures)
                {
                    if (texture.Name.IndexOf(suffix, StringComparison.OrdinalIgnoreCase) >= 0)
                        output.Add(texture);
                }
                return output;
            }

            private List<TextureProcessJob> BuildProcessJobs(
                List<TextureAdapter> sourceTextures,
                string sourceSuffix,
                string targetSuffix,
                bool scriptWritesToNewTexturesFolder)
            {
                List<TextureProcessJob> jobs = new List<TextureProcessJob>();
                int index = 0;

                foreach (var sourceTexture in sourceTextures)
                {
                    if (!TryReplaceToken(sourceTexture.Name, sourceSuffix, targetSuffix, out string targetName))
                        continue;

                    string tempBase = $"job_{index:D4}{sourceSuffix}";
                    string tempInput = tempBase + ".png";
                    string tempOutput = scriptWritesToNewTexturesFolder
                        ? tempInput
                        : (ReplaceSuffix(tempBase, sourceSuffix, targetSuffix) + ".png");

                    jobs.Add(new TextureProcessJob()
                    {
                        SourceTexture = sourceTexture,
                        TargetName = targetName,
                        TempInputFileName = tempInput,
                        TempOutputFileName = tempOutput,
                    });

                    index++;
                }

                return jobs;
            }

            private bool TryReplaceToken(string input, string oldToken, string newToken, out string output)
            {
                output = input;
                if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldToken))
                    return false;

                int index = input.LastIndexOf(oldToken, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    return false;

                output = input.Substring(0, index) + newToken + input.Substring(index + oldToken.Length);
                return true;
            }

            private Dictionary<string, TextureAdapter> BuildTextureLookup(List<TextureAdapter> textures)
            {
                return textures.ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase);
            }

            private bool TryCreateMissingTarget(TextureAdapter sourceTexture, string targetName, out TextureAdapter targetTexture)
            {
                targetTexture = null;
                if (sourceTexture?.CreateSibling == null)
                    return false;

                targetTexture = sourceTexture.CreateSibling(targetName);
                return targetTexture != null;
            }

            private List<string> PromptTextureSelection(List<TextureAdapter> sourceTextures, string sourceSuffix, string targetSuffix)
            {
                List<string> names = sourceTextures.Select(x => x.Name).OrderBy(x => x).ToList();
                using (TextureSelectionForm form = new TextureSelectionForm(names, sourceSuffix, targetSuffix))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                        return form.GetSelectedNames();
                }
                return null;
            }

            private List<AutoMatchProposal> PromptAutoMatchSelection(
                List<AutoMatchProposal> proposals,
                int mapsProcessed,
                int mapsMissing,
                int mapsUnknownToken)
            {
                using (AutoMatchPreviewForm form = new AutoMatchPreviewForm(proposals, mapsProcessed, mapsMissing, mapsUnknownToken))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                        return form.GetSelectedProposals();
                }

                return null;
            }

            private FMDL PromptModelSelection(List<FMDL> models)
            {
                using (ModelSelectionForm form = new ModelSelectionForm(models))
                {
                    if (form.ShowDialog() == DialogResult.OK)
                        return form.GetSelectedModel();
                }

                return null;
            }

            private class TextureReplacementToolForm : GenericEditorForm
            {
                private readonly Action autoMatchMaterialNamesWithTexture;
                private readonly Action runAlbToOpa;
                private readonly Action runSpmToRgh;
                private readonly UserControl contentControl;

                public TextureReplacementToolForm(
                    BFRES activeBfres,
                    Action autoMatchMaterialNamesWithTexture,
                    Action runAlbToOpa,
                    Action runSpmToRgh)
                    : base(false, CreateContentControl(out UserControl control))
                {
                    this.autoMatchMaterialNamesWithTexture = autoMatchMaterialNamesWithTexture;
                    this.runAlbToOpa = runAlbToOpa;
                    this.runSpmToRgh = runSpmToRgh;
                    contentControl = control;

                    Text = $"Splatoon 3 - Texture Replacement ({activeBfres.Text})";
                    InitializeUI();
                }

                private static UserControl CreateContentControl(out UserControl control)
                {
                    control = new UserControl();
                    control.Dock = DockStyle.Fill;
                    return control;
                }

                private void InitializeUI()
                {
                    contentControl.Padding = new Padding(10);

                    TableLayoutPanel centerHost = new TableLayoutPanel();
                    centerHost.Dock = DockStyle.Fill;
                    centerHost.ColumnCount = 3;
                    centerHost.RowCount = 3;
                    centerHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
                    centerHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    centerHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
                    centerHost.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
                    centerHost.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    centerHost.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

                    FlowLayoutPanel contentPanel = new FlowLayoutPanel();
                    contentPanel.AutoSize = true;
                    contentPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                    contentPanel.FlowDirection = FlowDirection.TopDown;
                    contentPanel.WrapContents = false;
                    contentPanel.Margin = new Padding(0);

                    Label description = new Label();
                    description.AutoSize = false;
                    description.Width = 460;
                    description.Height = 42;
                    description.ForeColor = Color.White;
                    description.TextAlign = ContentAlignment.MiddleCenter;
                    description.Margin = new Padding(0, 0, 0, 12);
                    description.Text = "Choose a generator, select textures, then press Apply.";

                    Button btnGenerateOpa = CreateButton("Alb to Opa Textures");
                    btnGenerateOpa.Click += (s, e) => runAlbToOpa?.Invoke();

                    Button btnGenerateRgh = CreateButton("Spm to Rgh Textures");
                    btnGenerateRgh.Click += (s, e) => runSpmToRgh?.Invoke();

                    Button btnAutoMatch = CreateButton("Auto Match Textures to Materials");
                    btnAutoMatch.Click += (s, e) => autoMatchMaterialNamesWithTexture?.Invoke();

                    Label note = new Label();
                    note.AutoSize = false;
                    note.Width = 460;
                    note.Height = 24;
                    note.ForeColor = Color.White;
                    note.TextAlign = ContentAlignment.MiddleCenter;
                    note.Margin = new Padding(0, 8, 0, 0);

                    contentPanel.Controls.Add(description);
                    contentPanel.Controls.Add(btnGenerateOpa);
                    contentPanel.Controls.Add(btnGenerateRgh);
                    contentPanel.Controls.Add(btnAutoMatch);
                    contentPanel.Controls.Add(note);

                    centerHost.Controls.Add(contentPanel, 1, 1);
                    contentControl.Controls.Add(centerHost);
                }

                private Button CreateButton(string text)
                {
                    Button button = new Button();
                    button.Text = text;
                    button.Width = 300;
                    button.Height = 34;
                    button.Margin = new Padding(80, 4, 80, 4);
                    button.BackColor = Color.FromArgb(60, 60, 60);
                    button.ForeColor = Color.White;
                    button.FlatStyle = FlatStyle.Flat;
                    return button;
                }
            }

            private class TextureSelectionForm : GenericEditorForm
            {
                private readonly UserControl contentControl;
                private readonly CheckedListBox checkedList;
                private readonly Button btnRun;
                private readonly Button btnCancel;

                public TextureSelectionForm(List<string> textureNames, string sourceSuffix, string targetSuffix)
                    : base(false, CreateContentControl(out UserControl control))
                {
                    contentControl = control;
                    checkedList = new CheckedListBox();
                    btnRun = new Button();
                    btnCancel = new Button();

                    Text = $"Select {sourceSuffix} Sources ({sourceSuffix} -> {targetSuffix})";
                    InitializeUI(textureNames);
                }

                private static UserControl CreateContentControl(out UserControl control)
                {
                    control = new UserControl();
                    control.Dock = DockStyle.Fill;
                    return control;
                }

                private void InitializeUI(List<string> textureNames)
                {
                    contentControl.Padding = new Padding(8);
                    TableLayoutPanel root = new TableLayoutPanel();
                    root.Dock = DockStyle.Fill;
                    root.ColumnCount = 1;
                    root.RowCount = 4;
                    root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                    Label description = new Label();
                    description.Dock = DockStyle.Fill;
                    description.Height = 30;
                    description.ForeColor = Color.White;
                    description.TextAlign = ContentAlignment.MiddleLeft;
                    description.Text = "Choose the textures you'd like to include.";
                    description.Margin = new Padding(0, 0, 0, 4);

                    FlowLayoutPanel topButtons = new FlowLayoutPanel();
                    topButtons.Dock = DockStyle.Fill;
                    topButtons.Height = 34;
                    topButtons.WrapContents = false;
                    topButtons.FlowDirection = FlowDirection.LeftToRight;
                    topButtons.Margin = new Padding(0, 0, 0, 6);

                    Button btnAll = CreateButton("Select All", 100, 24, new Padding(0, 4, 8, 4));
                    btnAll.Click += (s, e) =>
                    {
                        for (int i = 0; i < checkedList.Items.Count; i++)
                            checkedList.SetItemChecked(i, true);
                    };

                    Button btnNone = CreateButton("Select None", 100, 24, new Padding(0, 4, 8, 4));
                    btnNone.Click += (s, e) =>
                    {
                        for (int i = 0; i < checkedList.Items.Count; i++)
                            checkedList.SetItemChecked(i, false);
                    };

                    topButtons.Controls.Add(btnAll);
                    topButtons.Controls.Add(btnNone);

                    checkedList.Dock = DockStyle.Fill;
                    checkedList.CheckOnClick = true;
                    checkedList.IntegralHeight = false;
                    checkedList.BackColor = Color.FromArgb(45, 45, 45);
                    checkedList.ForeColor = Color.White;
                    checkedList.BorderStyle = BorderStyle.FixedSingle;
                    checkedList.Margin = new Padding(0, 0, 0, 8);

                    foreach (var name in textureNames)
                    {
                        int index = checkedList.Items.Add(name);
                        checkedList.SetItemChecked(index, true);
                    }

                    FlowLayoutPanel bottomButtons = new FlowLayoutPanel();
                    bottomButtons.Dock = DockStyle.Fill;
                    bottomButtons.Height = 36;
                    bottomButtons.FlowDirection = FlowDirection.RightToLeft;
                    bottomButtons.WrapContents = false;
                    bottomButtons.Margin = new Padding(0);

                    btnRun.Text = "Apply";
                    btnRun.DialogResult = DialogResult.OK;
                    btnRun.Width = 90;
                    btnRun.Height = 28;
                    btnRun.BackColor = Color.FromArgb(60, 60, 60);
                    btnRun.ForeColor = Color.White;
                    btnRun.FlatStyle = FlatStyle.Flat;
                    btnRun.Margin = new Padding(0, 4, 0, 4);

                    btnCancel.Text = "Cancel";
                    btnCancel.DialogResult = DialogResult.Cancel;
                    btnCancel.Width = 90;
                    btnCancel.Height = 28;
                    btnCancel.BackColor = Color.FromArgb(60, 60, 60);
                    btnCancel.ForeColor = Color.White;
                    btnCancel.FlatStyle = FlatStyle.Flat;
                    btnCancel.Margin = new Padding(8, 4, 0, 4);

                    bottomButtons.Controls.Add(btnCancel);
                    bottomButtons.Controls.Add(btnRun);

                    root.Controls.Add(description, 0, 0);
                    root.Controls.Add(topButtons, 0, 1);
                    root.Controls.Add(checkedList, 0, 2);
                    root.Controls.Add(bottomButtons, 0, 3);

                    contentControl.Controls.Add(root);

                    Width = 460;
                    Height = 560;
                    AcceptButton = btnRun;
                    CancelButton = btnCancel;
                }

                private Button CreateButton(string text, int width, int height, Padding margin)
                {
                    Button button = new Button();
                    button.Text = text;
                    button.Width = width;
                    button.Height = height;
                    button.Margin = margin;
                    button.BackColor = Color.FromArgb(60, 60, 60);
                    button.ForeColor = Color.White;
                    button.FlatStyle = FlatStyle.Flat;
                    return button;
                }

                public List<string> GetSelectedNames()
                {
                    return checkedList.CheckedItems.Cast<object>()
                        .Select(x => x.ToString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                }
            }

            private class ModelSelectionForm : GenericEditorForm
            {
                private readonly UserControl contentControl;
                private readonly ListBox modelList;
                private readonly Button btnApply;
                private readonly Button btnCancel;
                private readonly List<FMDL> models;

                public ModelSelectionForm(List<FMDL> models)
                    : base(false, CreateContentControl(out UserControl control))
                {
                    contentControl = control;
                    modelList = new ListBox();
                    btnApply = new Button();
                    btnCancel = new Button();
                    this.models = models ?? new List<FMDL>();

                    Text = "Select Model";
                    InitializeUI();
                    LoadModels();
                }

                private static UserControl CreateContentControl(out UserControl control)
                {
                    control = new UserControl();
                    control.Dock = DockStyle.Fill;
                    return control;
                }

                private void InitializeUI()
                {
                    contentControl.Padding = new Padding(8);

                    TableLayoutPanel root = new TableLayoutPanel();
                    root.Dock = DockStyle.Fill;
                    root.ColumnCount = 1;
                    root.RowCount = 3;
                    root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                    Label description = new Label();
                    description.Dock = DockStyle.Fill;
                    description.Height = 30;
                    description.ForeColor = Color.White;
                    description.TextAlign = ContentAlignment.MiddleLeft;
                    description.Text = "Choose the model to auto-match materials for.";
                    description.Margin = new Padding(0, 0, 0, 6);

                    modelList.Dock = DockStyle.Fill;
                    modelList.BackColor = Color.FromArgb(45, 45, 45);
                    modelList.ForeColor = Color.White;
                    modelList.BorderStyle = BorderStyle.FixedSingle;
                    modelList.Margin = new Padding(0, 0, 0, 8);
                    modelList.IntegralHeight = false;
                    modelList.DoubleClick += (s, e) =>
                    {
                        if (modelList.SelectedIndex >= 0)
                        {
                            DialogResult = DialogResult.OK;
                            Close();
                        }
                    };

                    FlowLayoutPanel bottomButtons = new FlowLayoutPanel();
                    bottomButtons.Dock = DockStyle.Fill;
                    bottomButtons.Height = 36;
                    bottomButtons.FlowDirection = FlowDirection.RightToLeft;
                    bottomButtons.WrapContents = false;
                    bottomButtons.Margin = new Padding(0);

                    btnApply.Text = "Continue";
                    btnApply.DialogResult = DialogResult.OK;
                    btnApply.Width = 90;
                    btnApply.Height = 28;
                    btnApply.BackColor = Color.FromArgb(60, 60, 60);
                    btnApply.ForeColor = Color.White;
                    btnApply.FlatStyle = FlatStyle.Flat;
                    btnApply.Margin = new Padding(0, 4, 0, 4);

                    btnCancel.Text = "Cancel";
                    btnCancel.DialogResult = DialogResult.Cancel;
                    btnCancel.Width = 90;
                    btnCancel.Height = 28;
                    btnCancel.BackColor = Color.FromArgb(60, 60, 60);
                    btnCancel.ForeColor = Color.White;
                    btnCancel.FlatStyle = FlatStyle.Flat;
                    btnCancel.Margin = new Padding(8, 4, 0, 4);

                    bottomButtons.Controls.Add(btnCancel);
                    bottomButtons.Controls.Add(btnApply);

                    root.Controls.Add(description, 0, 0);
                    root.Controls.Add(modelList, 0, 1);
                    root.Controls.Add(bottomButtons, 0, 2);
                    contentControl.Controls.Add(root);

                    Width = 460;
                    Height = 420;
                    AcceptButton = btnApply;
                    CancelButton = btnCancel;
                }

                private void LoadModels()
                {
                    for (int i = 0; i < models.Count; i++)
                    {
                        string name = string.IsNullOrWhiteSpace(models[i].Text) ? $"Model {i + 1}" : models[i].Text;
                        modelList.Items.Add($"{i + 1}. {name}");
                    }

                    if (modelList.Items.Count > 0)
                        modelList.SelectedIndex = 0;
                }

                public FMDL GetSelectedModel()
                {
                    int index = modelList.SelectedIndex;
                    if (index < 0 || index >= models.Count)
                        return null;

                    return models[index];
                }
            }

            private class AutoMatchPreviewForm : GenericEditorForm
            {
                private readonly UserControl contentControl;
                private readonly DataGridView reportGrid;
                private readonly Button btnApply;
                private readonly Button btnCancel;
                private readonly List<AutoMatchProposal> proposals;

                public AutoMatchPreviewForm(
                    List<AutoMatchProposal> proposals,
                    int mapsProcessed,
                    int mapsMissing,
                    int mapsUnknownToken)
                    : base(false, CreateContentControl(out UserControl control))
                {
                    contentControl = control;
                    reportGrid = new DataGridView();
                    btnApply = new Button();
                    btnCancel = new Button();
                    this.proposals = proposals ?? new List<AutoMatchProposal>();

                    Text = "Auto Match Preview";
                    InitializeUI(mapsProcessed, mapsMissing, mapsUnknownToken);
                    LoadRows();
                }

                private static UserControl CreateContentControl(out UserControl control)
                {
                    control = new UserControl();
                    control.Dock = DockStyle.Fill;
                    return control;
                }

                private void InitializeUI(int mapsProcessed, int mapsMissing, int mapsUnknownToken)
                {
                    contentControl.Padding = new Padding(8);

                    TableLayoutPanel root = new TableLayoutPanel();
                    root.Dock = DockStyle.Fill;
                    root.ColumnCount = 1;
                    root.RowCount = 4;
                    root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                    root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                    Label summary = new Label();
                    summary.Dock = DockStyle.Fill;
                    summary.Height = 40;
                    summary.ForeColor = Color.White;
                    summary.TextAlign = ContentAlignment.MiddleLeft;
                    summary.Margin = new Padding(0, 0, 0, 6);
                    summary.Text =
                        $"Review matches before applying. Candidates: {proposals.Count(x => x.IsApplicable)} | Total rows: {proposals.Count} | Processed: {mapsProcessed} | Missing: {mapsMissing} | Unknown: {mapsUnknownToken}";

                    FlowLayoutPanel topButtons = new FlowLayoutPanel();
                    topButtons.Dock = DockStyle.Fill;
                    topButtons.Height = 34;
                    topButtons.WrapContents = false;
                    topButtons.FlowDirection = FlowDirection.LeftToRight;
                    topButtons.Margin = new Padding(0, 0, 0, 6);

                    Button btnAll = CreateButton("Select All", 100, 24, new Padding(0, 4, 8, 4));
                    btnAll.Click += (s, e) =>
                    {
                        foreach (DataGridViewRow row in reportGrid.Rows)
                        {
                            if (row.Tag is AutoMatchProposal proposal && proposal.IsApplicable)
                                row.Cells[0].Value = true;
                        }
                    };

                    Button btnNone = CreateButton("Select None", 100, 24, new Padding(0, 4, 8, 4));
                    btnNone.Click += (s, e) =>
                    {
                        foreach (DataGridViewRow row in reportGrid.Rows)
                            row.Cells[0].Value = false;
                    };

                    topButtons.Controls.Add(btnAll);
                    topButtons.Controls.Add(btnNone);

                    reportGrid.Dock = DockStyle.Fill;
                    reportGrid.AutoGenerateColumns = false;
                    reportGrid.AllowUserToAddRows = false;
                    reportGrid.RowHeadersVisible = false;
                    reportGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                    reportGrid.EnableHeadersVisualStyles = false;
                    reportGrid.AllowUserToResizeRows = false;
                    reportGrid.BackgroundColor = contentControl.BackColor;
                    reportGrid.GridColor = contentControl.BackColor;
                    reportGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                    reportGrid.MultiSelect = false;

                    reportGrid.DefaultCellStyle.BackColor = contentControl.BackColor;
                    reportGrid.DefaultCellStyle.ForeColor = Color.White;
                    reportGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(60, 60, 60);
                    reportGrid.DefaultCellStyle.SelectionForeColor = Color.White;

                    reportGrid.ColumnHeadersVisible = true;
                    reportGrid.ColumnHeadersHeight = 30;
                    reportGrid.ColumnHeadersDefaultCellStyle.BackColor = contentControl.BackColor;
                    reportGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
                    reportGrid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

                    reportGrid.Columns.Add(new DataGridViewCheckBoxColumn()
                    {
                        HeaderText = "",
                        FillWeight = 8,
                    });
                    reportGrid.Columns.Add(new DataGridViewTextBoxColumn()
                    {
                        HeaderText = "Material",
                        ReadOnly = true,
                        FillWeight = 18,
                    });
                    reportGrid.Columns.Add(new DataGridViewTextBoxColumn()
                    {
                        HeaderText = "Sampler",
                        ReadOnly = true,
                        FillWeight = 16,
                    });
                    reportGrid.Columns.Add(new DataGridViewTextBoxColumn()
                    {
                        HeaderText = "Current Texture",
                        ReadOnly = true,
                        FillWeight = 24,
                    });
                    reportGrid.Columns.Add(new DataGridViewTextBoxColumn()
                    {
                        HeaderText = "Matched Texture",
                        ReadOnly = true,
                        FillWeight = 24,
                    });
                    reportGrid.Columns.Add(new DataGridViewTextBoxColumn()
                    {
                        HeaderText = "Status",
                        ReadOnly = true,
                        FillWeight = 10,
                    });

                    reportGrid.CurrentCellDirtyStateChanged += (s, e) =>
                    {
                        if (reportGrid.IsCurrentCellDirty)
                            reportGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    };

                    FlowLayoutPanel bottomButtons = new FlowLayoutPanel();
                    bottomButtons.Dock = DockStyle.Fill;
                    bottomButtons.Height = 36;
                    bottomButtons.FlowDirection = FlowDirection.RightToLeft;
                    bottomButtons.WrapContents = false;
                    bottomButtons.Margin = new Padding(0);

                    btnApply.Text = "Apply";
                    btnApply.DialogResult = DialogResult.OK;
                    btnApply.Width = 90;
                    btnApply.Height = 28;
                    btnApply.BackColor = Color.FromArgb(60, 60, 60);
                    btnApply.ForeColor = Color.White;
                    btnApply.FlatStyle = FlatStyle.Flat;
                    btnApply.Margin = new Padding(0, 4, 0, 4);

                    btnCancel.Text = "Cancel";
                    btnCancel.DialogResult = DialogResult.Cancel;
                    btnCancel.Width = 90;
                    btnCancel.Height = 28;
                    btnCancel.BackColor = Color.FromArgb(60, 60, 60);
                    btnCancel.ForeColor = Color.White;
                    btnCancel.FlatStyle = FlatStyle.Flat;
                    btnCancel.Margin = new Padding(8, 4, 0, 4);

                    bottomButtons.Controls.Add(btnCancel);
                    bottomButtons.Controls.Add(btnApply);

                    root.Controls.Add(summary, 0, 0);
                    root.Controls.Add(topButtons, 0, 1);
                    root.Controls.Add(reportGrid, 0, 2);
                    root.Controls.Add(bottomButtons, 0, 3);
                    contentControl.Controls.Add(root);

                    Width = 960;
                    Height = 620;
                    AcceptButton = btnApply;
                    CancelButton = btnCancel;
                }

                private Button CreateButton(string text, int width, int height, Padding margin)
                {
                    Button button = new Button();
                    button.Text = text;
                    button.Width = width;
                    button.Height = height;
                    button.Margin = margin;
                    button.BackColor = Color.FromArgb(60, 60, 60);
                    button.ForeColor = Color.White;
                    button.FlatStyle = FlatStyle.Flat;
                    return button;
                }

                private void LoadRows()
                {
                    foreach (var proposal in proposals)
                    {
                        int row = reportGrid.Rows.Add(
                            proposal.IsApplicable,
                            proposal.MaterialName ?? "",
                            proposal.SamplerDisplay ?? "",
                            proposal.CurrentTextureName ?? "",
                            proposal.TargetTextureName ?? "",
                            proposal.Status ?? "");

                        reportGrid.Rows[row].Tag = proposal;
                        reportGrid.Rows[row].Cells[0].ReadOnly = !proposal.IsApplicable;
                    }
                }

                public List<AutoMatchProposal> GetSelectedProposals()
                {
                    List<AutoMatchProposal> selected = new List<AutoMatchProposal>();

                    foreach (DataGridViewRow row in reportGrid.Rows)
                    {
                        if (!(row.Tag is AutoMatchProposal proposal))
                            continue;

                        bool isChecked = row.Cells[0].Value is bool b && b;
                        if (isChecked && proposal.IsApplicable)
                            selected.Add(proposal);
                    }

                    return selected;
                }
            }

            private bool TryResolveScriptPath(string scriptName, out string scriptPath)
            {
                scriptPath = null;

                List<string> candidates = new List<string>();
                string executableDir = AppDomain.CurrentDomain.BaseDirectory;
                string currentDir = Directory.GetCurrentDirectory();

                candidates.Add(Path.Combine(executableDir, scriptName));
                candidates.Add(Path.Combine(currentDir, scriptName));

                string walkDir = executableDir;
                for (int i = 0; i < 8; i++)
                {
                    candidates.Add(Path.Combine(walkDir, scriptName));
                    DirectoryInfo parent = Directory.GetParent(walkDir);
                    if (parent == null)
                        break;
                    walkDir = parent.FullName;
                }

                foreach (var candidate in candidates)
                {
                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(candidate);
                    }
                    catch
                    {
                        continue;
                    }

                    if (File.Exists(fullPath))
                    {
                        scriptPath = fullPath;
                        return true;
                    }
                }

                return false;
            }

            private bool RunPythonScript(string scriptPath, string workingDirectory, out string stdErr)
            {
                stdErr = "";
                List<string> errors = new List<string>();

                foreach (var executable in new[] { "python", "py" })
                {
                    ProcessStartInfo start = new ProcessStartInfo();
                    start.FileName = executable;
                    start.WorkingDirectory = workingDirectory;
                    start.Arguments = executable == "py"
                        ? $"-3 \"{scriptPath}\""
                        : $"\"{scriptPath}\"";
                    start.UseShellExecute = false;
                    start.RedirectStandardOutput = true;
                    start.RedirectStandardError = true;
                    start.CreateNoWindow = true;
                    start.WindowStyle = ProcessWindowStyle.Hidden;

                    try
                    {
                        using (Process process = Process.Start(start))
                        {
                            if (process == null)
                                continue;

                            string stdout = process.StandardOutput.ReadToEnd();
                            string stderr = process.StandardError.ReadToEnd();
                            process.WaitForExit();

                            if (process.ExitCode == 0)
                                return true;

                            string errorText = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                            errors.Add($"{executable} exited with code {process.ExitCode}: {errorText}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{executable} failed: {ex.Message}");
                    }
                }

                stdErr = string.Join(Environment.NewLine, errors);
                if (string.IsNullOrWhiteSpace(stdErr))
                    stdErr = "Python runtime was not found.";

                return false;
            }

            private string ReplaceSuffix(string input, string oldSuffix, string newSuffix)
            {
                if (!input.EndsWith(oldSuffix, StringComparison.OrdinalIgnoreCase))
                    return input;

                return input.Substring(0, input.Length - oldSuffix.Length) + newSuffix;
            }

            private string CreateTempBftexPath(string baseName)
            {
                string safeName = string.Concat((baseName ?? "Texture").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                string dir = Path.Combine(Path.GetTempPath(), "SwitchToolbox_Splatoon3Tools");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, $"{Guid.NewGuid():N}_{safeName}.bftex");
            }

            private void TryDeleteFile(string path)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }

            private void TryDeleteDirectory(string path)
            {
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, true);
                }
                catch
                {
                }
            }
        }

        private void DiableLoadCheck()
        {
            BfresEditor bfresEditor = (BfresEditor)LibraryGUI.GetActiveContent(typeof(BfresEditor));
            bfresEditor.IsLoaded = false;
            bfresEditor.DisplayAllDDrawables();
        }
        

        private Type[] LoadMenus()
        {
            List<Type> MenuItems = new List<Type>();
            MenuItems.Add(typeof(MenuExt));
            return MenuItems.ToArray();
        }

        private Type[] LoadCompressionFormats()
        {
            List<Type> Formats = new List<Type>();
            Formats.Add(typeof(MeshCodecFormat));
            return Formats.ToArray();
        }

        private Type[] LoadFileFormats()
        {
            List<Type> Formats = new List<Type>();
            Formats.Add(typeof(BFRES));
            Formats.Add(typeof(MT_TEX));
            Formats.Add(typeof(MT_Model));
            Formats.Add(typeof(DKCTF.CModel));
            Formats.Add(typeof(DKCTF.CTexture));
            Formats.Add(typeof(BCSV));
            Formats.Add(typeof(TVOL));
            Formats.Add(typeof(BTI));
            Formats.Add(typeof(TXE));
            Formats.Add(typeof(SARC));
            Formats.Add(typeof(TRPAK));
            Formats.Add(typeof(BNTX));
            Formats.Add(typeof(BEA));
            Formats.Add(typeof(TagProductRSTBL));
            Formats.Add(typeof(BYAML));
            Formats.Add(typeof(XTX));
            Formats.Add(typeof(BXFNT));
            Formats.Add(typeof(MSBT));
            Formats.Add(typeof(BARS));
            Formats.Add(typeof(GFPAK));
            Formats.Add(typeof(NUTEXB));
            Formats.Add(typeof(NUT));
            Formats.Add(typeof(KCL));
            Formats.Add(typeof(GTXFile));
            Formats.Add(typeof(AAMP));
            Formats.Add(typeof(PTCL));
            Formats.Add(typeof(EFF));
            Formats.Add(typeof(EFCF));
            Formats.Add(typeof(BNSH));
            Formats.Add(typeof(BFSHA));
            Formats.Add(typeof(BFSTM));
            Formats.Add(typeof(BCSTM));
            Formats.Add(typeof(BRSTM));
            Formats.Add(typeof(BFWAV));
            Formats.Add(typeof(BCWAV));
            Formats.Add(typeof(BRWAV));
            Formats.Add(typeof(WAV));
            Formats.Add(typeof(MP3));
            Formats.Add(typeof(OGG));
            Formats.Add(typeof(IDSP));
            Formats.Add(typeof(HPS));
            Formats.Add(typeof(SHARC));
            Formats.Add(typeof(SHARCFB));
            Formats.Add(typeof(NARC));
            Formats.Add(typeof(TMPK));
            Formats.Add(typeof(TEX3DS));
            Formats.Add(typeof(NXARC));
            Formats.Add(typeof(SP2));
            Formats.Add(typeof(SWU));
            Formats.Add(typeof(SPC));
            Formats.Add(typeof(GameDataToc));
            Formats.Add(typeof(NUSHDB));
            Formats.Add(typeof(MKGPDX_PAC));
            Formats.Add(typeof(LZARC));
            Formats.Add(typeof(IGA_PAK));
            Formats.Add(typeof(MKAGPDX_Model));
            Formats.Add(typeof(GFBMDL));
            Formats.Add(typeof(GFBANM));
            Formats.Add(typeof(GFBANMCFG));
            Formats.Add(typeof(Turbo.Course_MapCamera_bin));
            Formats.Add(typeof(SDF));
            Formats.Add(typeof(RARC));
            Formats.Add(typeof(ME01));
            Formats.Add(typeof(LM3_DICT));
            Formats.Add(typeof(LM2_DICT));
            Formats.Add(typeof(GMX));
            Formats.Add(typeof(BMD));
            Formats.Add(typeof(GCDisk));
            Formats.Add(typeof(TPL));
            Formats.Add(typeof(BFTTF));
            Formats.Add(typeof(HedgehogLibrary.PACx));
            Formats.Add(typeof(GAR));
            Formats.Add(typeof(CTXB));
            Formats.Add(typeof(CSAB));
            Formats.Add(typeof(CMB));
            Formats.Add(typeof(G1T));
            Formats.Add(typeof(HyruleWarriors.G1M.G1M));
            Formats.Add(typeof(LayoutBXLYT.Cafe.BFLYT));
            Formats.Add(typeof(LayoutBXLYT.BCLYT));
            Formats.Add(typeof(LayoutBXLYT.BRLYT));
            Formats.Add(typeof(LayoutBXLYT.BFLAN));
            Formats.Add(typeof(LayoutBXLYT.BRLAN));
            Formats.Add(typeof(LayoutBXLYT.BCLAN));
            Formats.Add(typeof(ZSI));
            Formats.Add(typeof(IGZ_TEX));
            Formats.Add(typeof(MOD));
            Formats.Add(typeof(U8));
            Formats.Add(typeof(CTPK));
            Formats.Add(typeof(LINKDATA));
            Formats.Add(typeof(NCCH));
            Formats.Add(typeof(NCSD));
            Formats.Add(typeof(CTR.NCCH.RomFS));
            Formats.Add(typeof(DKCTF.MSBT));
            Formats.Add(typeof(DKCTF.PAK));
            Formats.Add(typeof(WTB));
            Formats.Add(typeof(PKZ));
            Formats.Add(typeof(DARC));
            Formats.Add(typeof(BFLIM));
            Formats.Add(typeof(BCLIM));
            Formats.Add(typeof(DAT_Bayonetta));
            Formats.Add(typeof(VIBS));
            Formats.Add(typeof(NLG.StrikersRLT));
            Formats.Add(typeof(NLG.StrikersRLG));
            Formats.Add(typeof(PunchOutWii.PO_DICT));
            Formats.Add(typeof(LM2_ARCADE_Model));
            Formats.Add(typeof(NLG_NLOC));
            Formats.Add(typeof(PCK));
            Formats.Add(typeof(NLG.StrikersSAnim));
            Formats.Add(typeof(APAK));
            Formats.Add(typeof(CtrLibrary.BCH));
            Formats.Add(typeof(LZS));
            Formats.Add(typeof(WTA));
            Formats.Add(typeof(BinGzArchive));
            Formats.Add(typeof(BNR));
            Formats.Add(typeof(PKG));
            Formats.Add(typeof(MTXT));
            Formats.Add(typeof(NKN));
            Formats.Add(typeof(MetroidDreadLibrary.BSMAT));
            Formats.Add(typeof(TRANM));
            Formats.Add(typeof(GFA));
            Formats.Add(typeof(TXTG));





            if (Runtime.DEVELOPER_DEBUG_MODE)
            {
                Formats.Add(typeof(BFSAR));
            }


            return Formats.ToArray();
        }
    }
}


