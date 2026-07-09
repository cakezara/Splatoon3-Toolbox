using Bfres.Structs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Toolbox.Library;
using Toolbox.Library.Forms;
using System.Reflection;
using ResU = Syroot.NintenTools.Bfres;

namespace FirstPlugin
{
    public class BatchMaterialReplaceForm : GenericEditorForm
    {
        private FMDL Model;
        private Label lblCounter;
        private DataGridView materialGrid;
        private Button btnApply;
        private Button btnAutoDetect;
        private ComboBox cmbPaintable;
        private ComboBox cmbSignature;
        private Dictionary<string, string> ReplacementMap = new Dictionary<string, string>();
        private UserControl contentControl;
        private FlowLayoutPanel optionsPanel;
        private readonly string[] SignatureTokens = new string[] { "Alb", "Nrm", "Rgh", "Opa", "Mtl", "AO", "Emm", "Col" };

        public BatchMaterialReplaceForm(FMDL model)
            : base(false, CreateContentControl(out UserControl control))
        {
            Model = model;
            contentControl = control;
            Text = $"Batch Replace Materials - {model.Text}";
            InitializeUI();
            LoadMaterials();
        }

        private static UserControl CreateContentControl(out UserControl control)
        {
            control = new UserControl();
            control.Dock = DockStyle.Fill;
            return control;
        }

        private void InitializeUI()
        {
            materialGrid = new DataGridView();
            btnApply = new Button();
            btnAutoDetect = new Button();
            lblCounter = new Label();
            optionsPanel = new FlowLayoutPanel();

            contentControl.Padding = new Padding(8);

            optionsPanel.Dock = DockStyle.Top;
            optionsPanel.Height = 34;
            optionsPanel.AutoSize = false;
            optionsPanel.WrapContents = false;
            optionsPanel.BackColor = contentControl.BackColor;
            optionsPanel.FlowDirection = FlowDirection.LeftToRight;

            btnAutoDetect.Text = "Auto-Fill From Materials Folder";
            btnAutoDetect.Height = 26;
            btnAutoDetect.BackColor = System.Drawing.Color.FromArgb(60, 60, 60);
            btnAutoDetect.ForeColor = System.Drawing.Color.White;
            btnAutoDetect.FlatStyle = FlatStyle.Flat;
            btnAutoDetect.Margin = new Padding(0, 2, 10, 2);
            btnAutoDetect.Click += AutoFillFromMaterialsFolder;

            cmbPaintable = new ComboBox();
            cmbPaintable.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbPaintable.Width = 140;
            cmbPaintable.Items.Add("Paintable");
            cmbPaintable.Items.Add("Unpaintable");
            cmbPaintable.SelectedIndex = 0;
            cmbPaintable.Margin = new Padding(0, 4, 10, 2);
            cmbPaintable.SelectedIndexChanged += (s, e) => RefreshSignatureOptions(true);

            cmbSignature = new ComboBox();
            cmbSignature.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSignature.Width = 160;
            cmbSignature.Margin = new Padding(0, 4, 10, 2);

            optionsPanel.Controls.Add(btnAutoDetect);
            optionsPanel.Controls.Add(new Label()
            {
                Text = "Type:",
                ForeColor = System.Drawing.Color.White,
                AutoSize = true,
                Margin = new Padding(0, 8, 6, 0),
                BackColor = contentControl.BackColor
            });
            optionsPanel.Controls.Add(cmbPaintable);
            optionsPanel.Controls.Add(new Label()
            {
                Text = "Maps:",
                ForeColor = System.Drawing.Color.White,
                AutoSize = true,
                Margin = new Padding(0, 8, 6, 0),
                BackColor = contentControl.BackColor
            });
            optionsPanel.Controls.Add(cmbSignature);

            lblCounter.Text = "";
            lblCounter.Dock = DockStyle.Top;
            lblCounter.Height = 30;
            lblCounter.ForeColor = System.Drawing.Color.White;
            lblCounter.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

            materialGrid.Dock = DockStyle.Fill;
            materialGrid.AutoGenerateColumns = false;
            materialGrid.AllowUserToAddRows = false;
            materialGrid.RowHeadersVisible = false;
            materialGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            materialGrid.EnableHeadersVisualStyles = false;
            materialGrid.AllowUserToResizeColumns = false;
            materialGrid.AllowUserToResizeRows = false;
            materialGrid.BackgroundColor = contentControl.BackColor;
            materialGrid.GridColor = contentControl.BackColor;

            materialGrid.DefaultCellStyle.BackColor = contentControl.BackColor;
            materialGrid.DefaultCellStyle.ForeColor = System.Drawing.Color.White;
            materialGrid.DefaultCellStyle.SelectionBackColor = System.Drawing.Color.FromArgb(60, 60, 60);
            materialGrid.DefaultCellStyle.SelectionForeColor = System.Drawing.Color.White;

            materialGrid.ColumnHeadersVisible = true;
            materialGrid.ColumnHeadersHeight = 30;
            materialGrid.ColumnHeadersDefaultCellStyle.BackColor = contentControl.BackColor;
            materialGrid.ColumnHeadersDefaultCellStyle.ForeColor = System.Drawing.Color.White;
            materialGrid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            materialGrid.Columns.Add(new DataGridViewTextBoxColumn()
            {
                HeaderText = "Material Name",
                ReadOnly = true,
                FillWeight = 25
            });

            materialGrid.Columns.Add(new DataGridViewTextBoxColumn()
            {
                HeaderText = "Material Type",
                ReadOnly = true,
                FillWeight = 40
            });

            materialGrid.Columns.Add(new DataGridViewTextBoxColumn()
            {
                HeaderText = "Paintable",
                ReadOnly = true,
                FillWeight = 15,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter }
            });

            materialGrid.Columns.Add(new DataGridViewTextBoxColumn()
            {
                HeaderText = "Replacement File",
                FillWeight = 35
            });

            materialGrid.Columns.Add(new DataGridViewButtonColumn()
            {
                HeaderText = "",
                Text = "Browse",
                UseColumnTextForButtonValue = true,
                FillWeight = 15
            });

            materialGrid.CellClick += MaterialGrid_CellClick;

            btnApply.Text = "Apply Replacements";
            btnApply.Dock = DockStyle.Bottom;
            btnApply.Height = 30;
            btnApply.BackColor = System.Drawing.Color.FromArgb(60, 60, 60);
            btnApply.ForeColor = System.Drawing.Color.White;
            btnApply.FlatStyle = FlatStyle.Flat;
            btnApply.Click += ApplyReplacements;

            contentControl.Controls.Add(btnApply);
            contentControl.Controls.Add(materialGrid);
            contentControl.Controls.Add(lblCounter);
            contentControl.Controls.Add(optionsPanel);

            RefreshSignatureOptions(false);
        }

        private List<string> GetTextureReferenceNames(FMAT mat)
        {
            List<string> names = new List<string>();

            if (mat.Material != null && mat.Material.TextureRefs != null)
                foreach (var tex in mat.Material.TextureRefs)
                    names.Add(tex);

            if (mat.MaterialU != null && mat.MaterialU.TextureRefs != null)
                foreach (var tex in mat.MaterialU.TextureRefs)
                    names.Add(tex.Name);

            return names;
        }

        private string GetMaterialCategory(FMAT mat)
        {
            var textures = GetTextureReferenceNames(mat);
            List<string> types = new List<string>();

            if (mat.isTransparent)
                types.Add("Translucent");

            if (textures.Any(t => t.Contains("_Alb")))
                types.Add("Alb");

            if (textures.Any(t => t.Contains("_Nrm")))
                types.Add("Nrm");

            if (textures.Any(t => t.Contains("_Rgh")))
                types.Add("Rgh");
            if (textures.Any(t => t.Contains("_Spm")))
                types.Add("Rgh");

            if (textures.Any(t => t.Contains("_Opa")))
                types.Add("Opa");

            if (textures.Any(t => t.Contains("_Mtl")))
                types.Add("Mtl");

            if (textures.Any(t => t.Contains("_ao")))
                types.Add("AO");

            if (HasEmissionSampler(mat))
                types.Add("Emm");

            if (textures.Any(t => ContainsIgnoreCase(t, "_Col")))
                types.Add("Col");

            if (types.Count == 0)
                types.Add("No known Materials");

            return string.Join(" + ", types);
        }

        private string DecodeUserData(object userData)
        {
            if (!TryDecodeUserDataFlag(userData, out bool isPaintable))
                return "";

            return isPaintable ? "Paintable" : "Not Paintable";
        }

        private bool TryDecodeUserDataFlag(object userData, out bool isPaintable)
        {
            isPaintable = false;
            if (userData == null)
                return false;

            var field = userData.GetType()
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault();

            if (field == null)
                return false;

            object raw = field.GetValue(userData);

            if (raw is int[] i && i.Length > 0)
            {
                isPaintable = i[0] == 1;
                return true;
            }

            if (raw is uint[] u && u.Length > 0)
            {
                isPaintable = u[0] == 1;
                return true;
            }

            if (raw is byte[] b && b.Length > 0)
            {
                if (b.Length >= 4)
                {
                    isPaintable = BitConverter.ToInt32(b, 0) == 1;
                    return true;
                }

                isPaintable = b[0] == 1;
                return true;
            }

            return false;
        }

        private bool TryGetSplatoon2PaintableValue(FMAT mat, out bool isPaintable)
        {
            isPaintable = false;
            bool found = false;
            bool anyPaintable = false;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GroundPaint", "WallPaint", "ObjPaint" };

            if (mat.Material != null && mat.Material.UserDatas != null)
            {
                foreach (var data in mat.Material.UserDatas)
                {
                    if (!keys.Contains(data.Name))
                        continue;

                    if (!TryDecodeUserDataFlag(data, out bool value))
                        continue;

                    found = true;
                    if (value)
                        anyPaintable = true;
                }
            }

            if (mat.MaterialU != null && mat.MaterialU.UserData != null)
            {
                foreach (var pair in mat.MaterialU.UserData)
                {
                    if (!keys.Contains(pair.Key))
                        continue;

                    if (!TryDecodeUserDataFlag(pair.Value, out bool value))
                        continue;

                    found = true;
                    if (value)
                        anyPaintable = true;
                }
            }

            if (!found)
                return false;

            isPaintable = anyPaintable;
            return true;
        }

        private string GetPaintableValue(FMAT mat)
        {
            if (mat.Material != null && mat.Material.UserDatas != null)
                foreach (var data in mat.Material.UserDatas)
                    if (data.Name.Equals("Paintable", StringComparison.OrdinalIgnoreCase))
                        return DecodeUserData(data);

            if (mat.MaterialU != null && mat.MaterialU.UserData != null)
                foreach (var pair in mat.MaterialU.UserData)
                    if (pair.Key.Equals("Paintable", StringComparison.OrdinalIgnoreCase))
                        return DecodeUserData(pair.Value);

            if (TryGetSplatoon2PaintableValue(mat, out bool splatoon2Paintable))
                return splatoon2Paintable ? "Paintable" : "Not Paintable";

            return "";
        }

        private bool TryGetPaintableValue(FMAT mat, out bool isPaintable)
        {
            isPaintable = false;
            string value = GetPaintableValue(mat);

            if (value == "Paintable")
            {
                isPaintable = true;
                return true;
            }
            if (value == "Not Paintable")
            {
                isPaintable = false;
                return true;
            }
            return false;
        }

        private static bool ContainsIgnoreCase(string value, string token)
        {
            return value?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool HasEmissionSampler(FMAT mat)
        {
            return mat.shaderassign?.samplers?.Keys.Any(sampler =>
                       string.Equals(sampler, "_e0", StringComparison.OrdinalIgnoreCase)) == true ||
                   mat.TextureMaps.OfType<MatTexture>().Any(map =>
                string.Equals(map.SamplerName, "_e0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(map.FragShaderSampler, "_e0", StringComparison.OrdinalIgnoreCase));
        }

        private bool HasSignatureTokens(FMAT mat, List<string> tokens)
        {
            var textures = GetTextureReferenceNames(mat);
            foreach (var token in tokens)
            {
                if (token == "Emm")
                {
                    if (!HasEmissionSampler(mat))
                        return false;
                }
                else if (token == "AO")
                {
                    if (!textures.Any(t => ContainsIgnoreCase(t, "_ao")))
                        return false;
                }
                else
                {
                    if (!textures.Any(t => ContainsIgnoreCase(t, "_" + token)))
                        return false;
                }
            }
            return true;
        }

        private HashSet<string> GetPresentSignatureTokens(FMAT mat)
        {
            var textures = GetTextureReferenceNames(mat);
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var token in SignatureTokens)
            {
                if (token == "Emm")
                {
                    if (HasEmissionSampler(mat))
                        present.Add(token);
                }
                else if (token == "AO")
                {
                    if (textures.Any(t => ContainsIgnoreCase(t, "_ao")))
                        present.Add(token);
                }
                else
                {
                    if (textures.Any(t => ContainsIgnoreCase(t, "_" + token)))
                        present.Add(token);
                }
            }

            if (textures.Any(t => ContainsIgnoreCase(t, "_Spm")))
                present.Add("Rgh");

            return present;
        }

        private bool HasExactSignatureTokens(FMAT mat, List<string> tokens)
        {
            var present = GetPresentSignatureTokens(mat);
            if (present.Count != tokens.Count)
                return false;

            foreach (var token in tokens)
            {
                if (!present.Contains(token))
                    return false;
            }

            return true;
        }

        private void LoadMaterials()
        {
            materialGrid.Rows.Clear();

            foreach (var mat in Model.materials.Values)
            {
                string category = GetMaterialCategory(mat);
                string paintable = GetPaintableValue(mat);

                int rowIndex = materialGrid.Rows.Add(
                    mat.Text,
                    category,
                    paintable,
                    "",
                    "Browse"
                );

                materialGrid.Rows[rowIndex].Tag = mat;
            }
        }

        private void AutoFillFromMaterialsFolder(object sender, EventArgs e)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string folderPath = Path.Combine(baseDir, "Materials");

            if (!Directory.Exists(folderPath))
            {
                MessageBox.Show($"Materials folder not found:\n{folderPath}");
                return;
            }

            bool targetPaintable = cmbPaintable.SelectedIndex == 0;
            string signatureKey = cmbSignature.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(signatureKey))
            {
                MessageBox.Show("No signature selected.");
                return;
            }

            string fileName = (targetPaintable ? "Paintable_" : "Unpaintable_") + signatureKey + ".bfmat";
            string filePath = Path.Combine(folderPath, fileName);

            if (!File.Exists(filePath))
            {
                MessageBox.Show($"Material file not found:\n{filePath}");
                return;
            }

            List<string> signatureTokens = ParseSignatureTokens(signatureKey);
            if (signatureTokens.Count == 0)
            {
                MessageBox.Show($"Could not parse signature tokens from: {signatureKey}");
                return;
            }

            int matched = 0;
            int skipped = 0;
            int unknownPaintable = 0;

            foreach (DataGridViewRow row in materialGrid.Rows)
            {
                FMAT mat = row.Tag as FMAT;
                if (mat == null)
                    continue;

                if (!HasExactSignatureTokens(mat, signatureTokens))
                {
                    skipped++;
                    continue;
                }

                if (!TryGetPaintableValue(mat, out bool isPaintable))
                {
                    unknownPaintable++;
                    continue;
                }

                if (isPaintable != targetPaintable)
                {
                    skipped++;
                    continue;
                }

                row.Cells[3].Value = filePath;
                ReplacementMap[mat.Text] = filePath;
                matched++;
            }

            lblCounter.Text = $"Auto-Fill matched: {matched} / {materialGrid.Rows.Count} (skipped {skipped}, unknown paintable {unknownPaintable})";
        }

        private void RefreshSignatureOptions(bool preserveSelection)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string folderPath = Path.Combine(baseDir, "Materials");

            string previous = preserveSelection ? (cmbSignature.SelectedItem as string) : null;

            cmbSignature.Items.Clear();

            if (Directory.Exists(folderPath))
            {
                bool isPaintable = cmbPaintable.SelectedIndex == 0;
                string prefix = isPaintable ? "Paintable_" : "Unpaintable_";

                var signatures = Directory.GetFiles(folderPath, "*.bfmat")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Substring(prefix.Length))
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();

                foreach (var sig in signatures)
                    cmbSignature.Items.Add(sig);
            }

            if (cmbSignature.Items.Count == 0)
            {
                cmbSignature.Items.Add("AlbNrmRgh");
            }

            if (previous != null && cmbSignature.Items.Contains(previous))
                cmbSignature.SelectedItem = previous;
            else
                cmbSignature.SelectedIndex = 0;
        }

        private List<string> ParseSignatureTokens(string signature)
        {
            List<string> tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(signature))
                return tokens;

            foreach (var token in SignatureTokens)
            {
                if (signature.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    tokens.Add(token);
            }

            return tokens;
        }

        private void MaterialGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (materialGrid.Columns[e.ColumnIndex] is DataGridViewButtonColumn && e.RowIndex >= 0)
            {
                OpenFileDialog ofd = new OpenFileDialog();
                ofd.Filter = "BFMAT Files (*.bfmat)|*.bfmat";

                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string matName = materialGrid.Rows[e.RowIndex].Cells[0].Value.ToString();
                    materialGrid.Rows[e.RowIndex].Cells[3].Value = ofd.FileName;
                    ReplacementMap[matName] = ofd.FileName;
                }
            }
        }

        private void ApplyReplacements(object sender, EventArgs e)
        {
            int replacedCount = 0;
            foreach (var entry in ReplacementMap)
            {
                if (!Model.materials.ContainsKey(entry.Key))
                    continue;

                if (!File.Exists(entry.Value))
                    continue;

                FMAT material = Model.materials[entry.Key];
                material.Replace(entry.Value, false);
                replacedCount++;
            }

            LibraryGUI.UpdateViewport();
            lblCounter.Text = $"Total Materials Replaced: {replacedCount}";
            MessageBox.Show("Material replacement complete!");
        }

    }
}
