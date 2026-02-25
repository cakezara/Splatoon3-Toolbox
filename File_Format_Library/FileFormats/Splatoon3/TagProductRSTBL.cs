using SharpYaml;
using SharpYaml.Serialization;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Toolbox.Library;
using Toolbox.Library.Forms;

namespace FirstPlugin
{
    public class TagProductRSTBL : IEditor<UserControl>, IFileFormat
    {
        private static readonly Regex FilePattern =
            new Regex(@"^Tag\.Product\.[A-Za-z]*\d+\.rstbl\.byml(?:\.zs)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly object _sync = new object();

        private TagProductBymlDocument _document = new TagProductBymlDocument();
        private List<TagProductEntry> _entries = new List<TagProductEntry>();
        private List<string> _tags = new List<string>();

        private TagProductEditor _activeEditor;

        public FileType FileType { get; set; } = FileType.Parameter;
        public bool CanSave { get; set; }
        public string[] Description { get; set; } = new[] { "Splatoon 3 Tag Editor" };
        public string[] Extension { get; set; } = new[] { "*.zs" };
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public IFileInfo IFileInfo { get; set; }

        public Type[] Types => new Type[0];

        public bool Identify(Stream stream)
        {
            if (!IsTagProductFileName(FileName))
                return false;

            if (stream == null || stream.Length < 4)
                return false;

            long pos = stream.Position;
            try
            {
                stream.Position = 0;
                int b0 = stream.ReadByte();
                int b1 = stream.ReadByte();
                if (b0 == -1 || b1 == -1)
                    return false;

                return (b0 == 'Y' && b1 == 'B') || (b0 == 'B' && b1 == 'Y');
            }
            finally
            {
                stream.Position = pos;
            }
        }

        public UserControl OpenForm()
        {
            _activeEditor = new TagProductEditor(this);
            _activeEditor.Dock = DockStyle.Fill;
            _activeEditor.Text = FileName;
            return _activeEditor;
        }

        public void FillEditor(UserControl control)
        {
            if (control is TagProductEditor editor)
                editor.ReloadFromModel();
        }

        public void Load(Stream stream)
        {
            CanSave = true;

            byte[] fileData = ReadAllBytes(stream);
            _document = TagProductBymlSerializer.Read(fileData);

            lock (_sync)
            {
                _tags = new List<string>(_document.TagList);
                _entries = BuildEntries(_document.PathList, _document.TagList, _document.BitTable);
            }
        }

        public void Save(Stream stream)
        {
            if (_activeEditor != null && !_activeEditor.ApplyChanges(out string error))
                throw new InvalidDataException(error);

            TagProductBymlDocument outDoc;
            lock (_sync)
            {
                List<string> tags = NormalizeTagListPreserveOrder(_tags);
                List<TagProductEntry> editedEntries = NormalizeEntriesPreserveOrder(_entries);
                var tagSet = new HashSet<string>(tags, StringComparer.Ordinal);

                foreach (var edited in editedEntries)
                {
                    foreach (string tag in edited.Tags)
                    {
                        if (!tagSet.Contains(tag))
                            throw new InvalidDataException($"Entry \"{edited.ToTagKey()}\" references unknown tag \"{tag}\".");
                    }
                }

                List<TagProductEntry> originalEntries = BuildEntries(_document.PathList, _document.TagList, _document.BitTable);
                var editedByKey = new Dictionary<string, TagProductEntry>(StringComparer.Ordinal);
                foreach (var edited in editedEntries)
                {
                    string key = edited.ToTagKey();
                    if (editedByKey.ContainsKey(key))
                        throw new InvalidDataException($"Duplicate entry key \"{key}\".");
                    editedByKey.Add(key, edited);
                }

                var compiledEntries = new List<TagProductEntry>(editedEntries.Count);
                var seenCompiled = new HashSet<string>(StringComparer.Ordinal);

                foreach (var original in originalEntries)
                {
                    string key = original.ToTagKey();
                    if (!editedByKey.TryGetValue(key, out TagProductEntry edited))
                        continue;

                    compiledEntries.Add(new TagProductEntry()
                    {
                        Prefix = original.Prefix ?? string.Empty,
                        Name = original.Name ?? string.Empty,
                        Suffix = original.Suffix ?? string.Empty,
                        Tags = new List<string>(edited.Tags ?? new List<string>()),
                    });
                    seenCompiled.Add(key);
                }

                foreach (var edited in editedEntries)
                {
                    string key = edited.ToTagKey();
                    if (seenCompiled.Contains(key))
                        continue;

                    compiledEntries.Add(new TagProductEntry()
                    {
                        Prefix = edited.Prefix ?? string.Empty,
                        Name = edited.Name ?? string.Empty,
                        Suffix = edited.Suffix ?? string.Empty,
                        Tags = new List<string>(edited.Tags ?? new List<string>()),
                    });
                }

                byte[] bitTable = BuildBitTable(compiledEntries, tags);
                List<string> pathList = BuildPathList(compiledEntries);

                outDoc = new TagProductBymlDocument()
                {
                    IsBigEndian = _document.IsBigEndian,
                    Version = _document.Version,
                    PathList = pathList,
                    TagList = tags,
                    BitTable = bitTable,
                    RankTable = _document.RankTable != null ? (byte[])_document.RankTable.Clone() : Array.Empty<byte>(),
                };
            }

            byte[] saved = TagProductBymlSerializer.Write(outDoc);
            if (stream.CanSeek)
            {
                stream.Position = 0;
                stream.SetLength(0);
            }
            stream.Write(saved, 0, saved.Length);
        }

        public void Unload()
        {
            _activeEditor = null;
            _document = new TagProductBymlDocument();
            _entries.Clear();
            _tags.Clear();
        }

        internal List<TagProductEntry> GetEntriesSnapshot()
        {
            lock (_sync)
                return CloneEntries(_entries);
        }

        internal List<string> GetTagsSnapshot()
        {
            lock (_sync)
                return new List<string>(_tags);
        }

        internal int GetRankTableSize()
        {
            lock (_sync)
                return _document.RankTable?.Length ?? 0;
        }

        internal void SetEntriesAndTags(List<TagProductEntry> entries, List<string> tags)
        {
            lock (_sync)
            {
                _entries = CloneEntries(entries ?? new List<TagProductEntry>());
                _tags = new List<string>(tags ?? new List<string>());
            }
        }

        private static bool IsTagProductFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            string name = Path.GetFileName(fileName);
            return FilePattern.IsMatch(name);
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            if (stream is MemoryStream mem && mem.TryGetBuffer(out ArraySegment<byte> segment))
                return segment.ToArray();

            long pos = stream.Position;
            stream.Position = 0;
            try
            {
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
            finally
            {
                stream.Position = pos;
            }
        }

        private static List<TagProductEntry> BuildEntries(List<string> pathList, List<string> tagList, byte[] bitTable)
        {
            var result = new List<TagProductEntry>();
            if (pathList == null || pathList.Count == 0)
                return result;

            int entryCount = (pathList.Count + 2) / 3;
            for (int i = 0; i < entryCount; i++)
            {
                string prefix = GetPathValue(pathList, i * 3 + 0);
                string name = GetPathValue(pathList, i * 3 + 1);
                string suffix = GetPathValue(pathList, i * 3 + 2);

                result.Add(new TagProductEntry()
                {
                    Prefix = prefix,
                    Name = name,
                    Suffix = suffix,
                    Tags = DecodeTags(bitTable, i, tagList),
                });
            }
            return result;
        }

        private static string GetPathValue(List<string> pathList, int index)
        {
            if (pathList == null || index < 0 || index >= pathList.Count)
                return string.Empty;
            return pathList[index] ?? string.Empty;
        }

        private static List<string> DecodeTags(byte[] bitTable, int entryIndex, List<string> tagList)
        {
            var tags = new List<string>();
            if (bitTable == null || bitTable.Length == 0 || tagList == null || tagList.Count == 0)
                return tags;

            int tagCount = tagList.Count;
            for (int tagIndex = 0; tagIndex < tagCount; tagIndex++)
            {
                int bitIndex = entryIndex * tagCount + tagIndex;
                int byteIndex = bitIndex >> 3;
                int bitInByte = bitIndex & 7;

                if (byteIndex >= bitTable.Length)
                    break;

                if ((bitTable[byteIndex] & (1 << bitInByte)) != 0)
                    tags.Add(tagList[tagIndex]);
            }
            return tags;
        }

        private static List<string> BuildPathList(List<TagProductEntry> entries)
        {
            var list = new List<string>(Math.Max(0, entries.Count * 3));
            foreach (var entry in entries)
            {
                list.Add(entry.Prefix ?? string.Empty);
                list.Add(entry.Name ?? string.Empty);
                list.Add(entry.Suffix ?? string.Empty);
            }
            return list;
        }

        private static List<string> NormalizeTagListPreserveOrder(List<string> tags)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string value in tags ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                string clean = value.Trim();
                if (seen.Add(clean))
                    result.Add(clean);
            }

            return result;
        }

        private static List<TagProductEntry> NormalizeEntriesPreserveOrder(List<TagProductEntry> entries)
        {
            var result = new List<TagProductEntry>();
            foreach (var entry in entries ?? new List<TagProductEntry>())
            {
                if (entry == null)
                    continue;

                var tags = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string tag in entry.Tags ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;

                    string clean = tag.Trim();
                    if (seen.Add(clean))
                        tags.Add(clean);
                }

                result.Add(new TagProductEntry()
                {
                    Prefix = entry.Prefix ?? string.Empty,
                    Name = entry.Name ?? string.Empty,
                    Suffix = entry.Suffix ?? string.Empty,
                    Tags = tags,
                });
            }
            return result;
        }

        private static byte[] BuildBitTable(List<TagProductEntry> entries, List<string> tags)
        {
            if (entries == null)
                entries = new List<TagProductEntry>();
            if (tags == null)
                tags = new List<string>();

            int tagCount = tags.Count;
            if (tagCount == 0 || entries.Count == 0)
                return Array.Empty<byte>();

            var tagLookup = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < tags.Count; i++)
            {
                if (!tagLookup.ContainsKey(tags[i]))
                    tagLookup.Add(tags[i], i);
            }

            int totalBits = entries.Count * tagCount;
            byte[] bitTable = new byte[(totalBits + 7) / 8];

            for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                foreach (string tag in entries[entryIndex].Tags)
                {
                    if (!tagLookup.TryGetValue(tag, out int tagIndex))
                        throw new InvalidDataException($"Unknown tag \"{tag}\" referenced by entry \"{entries[entryIndex].ToTagKey()}\".");

                    int bitIndex = entryIndex * tagCount + tagIndex;
                    int byteIndex = bitIndex >> 3;
                    int bitInByte = bitIndex & 7;
                    bitTable[byteIndex] |= (byte)(1 << bitInByte);
                }
            }

            return bitTable;
        }

        private static List<TagProductEntry> CloneEntries(List<TagProductEntry> entries)
        {
            var result = new List<TagProductEntry>(entries.Count);
            foreach (var entry in entries)
            {
                result.Add(new TagProductEntry()
                {
                    Prefix = entry.Prefix ?? string.Empty,
                    Name = entry.Name ?? string.Empty,
                    Suffix = entry.Suffix ?? string.Empty,
                    Tags = new List<string>(entry.Tags ?? new List<string>()),
                });
            }
            return result;
        }
    }

    internal sealed class TagProductEditor : UserControl, IFIleEditor
    {
        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETSCROLLPOS = 0x04DD;
        private const int EM_SETSCROLLPOS = 0x04DE;
        private static readonly Regex YamlAnchorListItemPattern =
            new Regex(@"^(\s*-\s+)&[^\s]+\s+(.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Color TitleColor = Color.FromArgb(53, 110, 190);
        private static readonly Color AttributeColor = Color.FromArgb(207, 127, 34);

        private readonly TagProductRSTBL _fileFormat;
        private readonly Serializer _serializer;

        private readonly RichTextBox _entriesTextBox;
        private readonly RichTextBox _tagsTextBox;
        private readonly Label _statusLabel;
        private Dictionary<string, string> _aliasToTag = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> _tagToAlias = new Dictionary<string, string>(StringComparer.Ordinal);

        private bool _isReloading;
        private bool _isApplyingSyntaxColor;
        private bool _queueInitialColorOnHandle;
        private bool _queueInitialColorOnVisible;
        private bool _hasAskedLoadColorPreference;
        private bool _loadColorOnOpen;

        [StructLayout(LayoutKind.Sequential)]
        private struct ScrollPoint
        {
            public int X;
            public int Y;
        }

        private struct TextViewState
        {
            public int SelectionStart;
            public int SelectionLength;
            public ScrollPoint ScrollPoint;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref ScrollPoint lParam);

        public TagProductEditor(TagProductRSTBL fileFormat)
        {
            _fileFormat = fileFormat;

            var serializerSettings = new SerializerSettings()
            {
                DefaultStyle = YamlStyle.Any,
                ComparerForKeySorting = null,
            };
            _serializer = new Serializer(serializerSettings);

            var controlsPanel = new FlowLayoutPanel()
            {
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(6, 4, 6, 4),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
            };

            var applyButton = new STButton()
            {
                Text = "Apply YAML",
                AutoSize = false,
                Width = 104,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
            };
            applyButton.FlatAppearance.BorderSize = 1;
            applyButton.Click += (s, e) =>
            {
                if (ApplyChanges(out string error))
                    MessageBox.Show("Tag entries were saved!", "Splatoon 3 Tag Editor");
                else
                    MessageBox.Show(error, "Splatoon 3 Tag Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            var reloadButton = new STButton()
            {
                Text = "Reload",
                AutoSize = false,
                Width = 88,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
            };
            reloadButton.FlatAppearance.BorderSize = 1;
            reloadButton.Click += (s, e) => ReloadFromModel();

            controlsPanel.Controls.Add(applyButton);
            controlsPanel.Controls.Add(reloadButton);

            var split = new SplitContainer()
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 560,
            };

            var entriesGroup = new GroupBox()
            {
                Dock = DockStyle.Fill,
                Text = "Entries (YAML)",
            };

            _entriesTextBox = new RichTextBox()
            {
                Dock = DockStyle.Fill,
                ScrollBars = RichTextBoxScrollBars.Both,
                WordWrap = false,
                AcceptsTab = true,
                Font = new Font("Consolas", 9.0f),
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
            };
            _entriesTextBox.TextChanged += (s, e) =>
            {
                if (_isReloading)
                    return;

                UpdateStatus(pending: true);
            };

            entriesGroup.Controls.Add(_entriesTextBox);

            var tagsGroup = new GroupBox()
            {
                Dock = DockStyle.Fill,
                Text = "Tags (YAML)",
            };

            _tagsTextBox = new RichTextBox()
            {
                Dock = DockStyle.Fill,
                ScrollBars = RichTextBoxScrollBars.Both,
                WordWrap = false,
                AcceptsTab = true,
                Font = new Font("Consolas", 9.0f),
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
            };
            _tagsTextBox.TextChanged += (s, e) =>
            {
                if (_isReloading)
                    return;

                UpdateStatus(pending: true);
            };

            tagsGroup.Controls.Add(_tagsTextBox);

            split.Panel1.Controls.Add(entriesGroup);
            split.Panel2.Controls.Add(tagsGroup);

            _statusLabel = new Label()
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Padding = new Padding(8, 4, 8, 4),
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            };

            ApplyTheme(entriesGroup, tagsGroup, split, controlsPanel, applyButton, reloadButton);

            Controls.Add(split);
            Controls.Add(_statusLabel);
            Controls.Add(controlsPanel);
            HandleCreated += (s, e) =>
            {
                if (_queueInitialColorOnHandle)
                {
                    _queueInitialColorOnHandle = false;
                    QueueInitialColoringPass();
                }
            };
            VisibleChanged += (s, e) =>
            {
                if (_queueInitialColorOnVisible && Visible)
                {
                    _queueInitialColorOnVisible = false;
                    QueueInitialColoringPass();
                }
            };

            ReloadFromModel();
        }

        public List<IFileFormat> GetFileFormats()
        {
            return new List<IFileFormat>() { _fileFormat };
        }

        public void ReloadFromModel()
        {
            QueueReloadContentStep();
        }

        private void QueueReloadContentStep()
        {
            if (IsDisposed)
                return;

            _isReloading = true;
            try
            {
                List<TagProductEntry> entries = _fileFormat.GetEntriesSnapshot();
                List<string> tags = _fileFormat.GetTagsSnapshot();
                RebuildAliasMaps(tags);

                _entriesTextBox.Text = NormalizeYamlAnchors(SerializeEntries(entries));
                _tagsTextBox.Text = NormalizeYamlAnchors(SerializeTags(tags));
                UpdateStatus(pending: false);
            }
            finally
            {
                _isReloading = false;
            }

            QueueInitialColoringPass();
        }

        public bool ApplyChanges(out string error)
        {
            try
            {
                List<TagProductEntry> parsedEntries = ParseEntries(_entriesTextBox.Text);
                List<string> parsedTags = ParseTags(_tagsTextBox.Text);

                var tagSet = new HashSet<string>(parsedTags, StringComparer.Ordinal);
                foreach (var entry in parsedEntries)
                {
                    foreach (string tag in entry.Tags)
                    {
                        if (!tagSet.Contains(tag))
                            throw new InvalidDataException($"Entry \"{entry.ToTagKey()}\" references missing tag \"{tag}\".");
                    }
                }

                _fileFormat.SetEntriesAndTags(parsedEntries, parsedTags);
                RebuildAliasMaps(parsedTags);
                UpdateStatus(pending: false);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid YAML content: {ex.Message}";
                return false;
            }
        }

        private void UpdateStatus(bool pending)
        {
            int entryCount = _fileFormat.GetEntriesSnapshot().Count;
            int tagCount = _fileFormat.GetTagsSnapshot().Count;
            int rankSize = _fileFormat.GetRankTableSize();

            string suffix = pending ? " | Pending changes" : string.Empty;
            _statusLabel.Text = $"Entries: {entryCount} | Tags: {tagCount} | RankTable bytes: {rankSize}{suffix}";
        }

        private string SerializeEntries(List<TagProductEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return "{}\n";

            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                string key = QuoteYaml(entry.ToTagKey());
                var attributes = new List<string>();
                foreach (string tag in entry.Tags ?? Enumerable.Empty<string>())
                    attributes.Add(tag ?? string.Empty);

                if (attributes.Count == 0)
                {
                    sb.Append(key).Append(": []").Append('\n');
                    continue;
                }

                sb.Append(key).Append(':').Append('\n');
                foreach (string attribute in attributes)
                    sb.Append("  - ").Append(QuoteYaml(attribute)).Append('\n');
            }

            return sb.ToString();
        }

        private string SerializeTags(List<string> tags)
        {
            var list = new List<string>();
            foreach (string tag in tags ?? Enumerable.Empty<string>())
                list.Add(tag ?? string.Empty);

            if (list.Count == 0)
                return "[]\n";

            var sb = new StringBuilder();
            foreach (string value in list)
                sb.Append("- ").Append(QuoteYaml(value)).Append('\n');
            return sb.ToString();
        }

        private List<TagProductEntry> ParseEntries(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml))
                return new List<TagProductEntry>();

            var parsed = _serializer.Deserialize<Dictionary<string, List<string>>>(NormalizeYamlAnchors(yaml));
            var result = new List<TagProductEntry>();
            if (parsed == null)
                return result;

            foreach (var entry in parsed)
            {
                string[] parts = entry.Key.Split('|');
                if (parts.Length != 3)
                    throw new InvalidDataException($"Invalid entry key \"{entry.Key}\". Expected format: Prefix|Name|Suffix");

                var tags = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (entry.Value != null)
                {
                    foreach (string tag in entry.Value)
                    {
                        if (string.IsNullOrWhiteSpace(tag))
                            continue;

                        string clean = tag.Trim();
                        string resolved = ResolveAttributeTokenToTag(clean);
                        if (seen.Add(resolved))
                            tags.Add(resolved);
                    }
                }

                result.Add(new TagProductEntry()
                {
                    Prefix = parts[0],
                    Name = parts[1],
                    Suffix = parts[2],
                    Tags = tags,
                });
            }

            return result;
        }

        private List<string> ParseTags(string yaml)
        {
            if (string.IsNullOrWhiteSpace(yaml))
                return new List<string>();

            var parsed = _serializer.Deserialize<List<string>>(NormalizeYamlAnchors(yaml)) ?? new List<string>();
            var tags = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (string tag in parsed)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                string clean = tag.Trim();
                string resolved = ResolveAttributeTokenToTag(clean);
                if (seen.Add(resolved))
                    tags.Add(resolved);
            }

            return tags;
        }

        private void RebuildAliasMaps(List<string> tags)
        {
            _aliasToTag = new Dictionary<string, string>(StringComparer.Ordinal);
            _tagToAlias = new Dictionary<string, string>(StringComparer.Ordinal);

            if (tags == null)
                return;

            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i] ?? string.Empty;
                string alias = BuildAlias(i);

                _aliasToTag[alias] = tag;
                if (!_tagToAlias.ContainsKey(tag))
                    _tagToAlias.Add(tag, alias);
            }
        }

        private static string BuildAlias(int index)
        {
            return $"o{index:000}";
        }

        private static string QuoteYaml(string value)
        {
            value = value ?? string.Empty;
            return $"'{value.Replace("'", "''")}'";
        }

        private static string NormalizeYamlAnchors(string yaml)
        {
            if (string.IsNullOrEmpty(yaml))
                return yaml;

            return YamlAnchorListItemPattern.Replace(yaml, "$1$2");
        }

        private void ApplyTheme(GroupBox entriesGroup, GroupBox tagsGroup, SplitContainer split,
            FlowLayoutPanel controlsPanel, Button applyButton, Button reloadButton)
        {
            Color formBack = FormThemes.BaseTheme.FormBackColor;
            Color formFore = FormThemes.BaseTheme.FormForeColor;
            Color textBack = formBack;
            Color textFore = formFore;

            BackColor = formBack;
            ForeColor = formFore;

            controlsPanel.BackColor = formBack;
            controlsPanel.ForeColor = formFore;
            split.BackColor = formBack;

            entriesGroup.BackColor = formBack;
            entriesGroup.ForeColor = formFore;
            tagsGroup.BackColor = formBack;
            tagsGroup.ForeColor = formFore;

            _entriesTextBox.BackColor = textBack;
            _entriesTextBox.ForeColor = textFore;
            _tagsTextBox.BackColor = textBack;
            _tagsTextBox.ForeColor = textFore;

            _statusLabel.BackColor = formBack;
            _statusLabel.ForeColor = formFore;

            applyButton.BackColor = formBack;
            applyButton.ForeColor = formFore;
            reloadButton.BackColor = formBack;
            reloadButton.ForeColor = formFore;
        }

        private string ConvertTagToAlias(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return string.Empty;

            if (_tagToAlias.TryGetValue(tag, out string alias))
                return alias;

            return tag;
        }

        private string ResolveAttributeTokenToTag(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            if (_aliasToTag.TryGetValue(token, out string tag))
                return tag;

            return token;
        }

        private void QueueInitialColoringPass()
        {
            if (IsDisposed)
                return;

            if (!IsHandleCreated)
            {
                _queueInitialColorOnHandle = true;
                return;
            }
            if (!Visible)
            {
                _queueInitialColorOnVisible = true;
                return;
            }

            BeginInvoke((MethodInvoker)(() =>
            {
                if (IsDisposed)
                    return;

                EnsureLoadColorPreference();
                if (_loadColorOnOpen)
                    ApplyFullColoringPass();
            }));
        }

        private void EnsureLoadColorPreference()
        {
            if (_hasAskedLoadColorPreference)
                return;

            const string message =
                "Load syntax colors for this Tag file?\n" +
                "Could result in longer loading";

            DialogResult result = MessageBox.Show(
                this,
                message,
                "Splatoon 3 Tag Editor",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            _loadColorOnOpen = (result == DialogResult.Yes);
            _hasAskedLoadColorPreference = true;
        }

        private void ApplyFullColoringPass()
        {
            if (IsDisposed || !Visible || _isApplyingSyntaxColor)
                return;

            ColorEntireDocument(_entriesTextBox, colorTitles: true);
            ColorEntireDocument(_tagsTextBox, colorTitles: false);
        }

        private void ColorEntireDocument(RichTextBox box, bool colorTitles)
        {
            if (box == null || box.IsDisposed || box.TextLength == 0)
                return;

            int lineCount = box.Lines.Length;
            if (lineCount <= 0)
                return;

            ApplyLineColorsRange(box, colorTitles, 0, lineCount - 1);
        }

        private void ApplyLineColorsRange(RichTextBox box, bool colorTitles, int startLine, int endLine)
        {
            if (_isApplyingSyntaxColor)
                return;
            if (box == null || box.IsDisposed || box.TextLength == 0 || startLine > endLine)
                return;

            _isApplyingSyntaxColor = true;
            TextViewState viewState = CaptureViewState(box);

            try
            {
                SuspendRedraw(box);
                string text = box.Text ?? string.Empty;
                var titleRanges = new List<Tuple<int, int>>();
                var attributeRanges = new List<Tuple<int, int>>();
                int textLength = text.Length;
                int lineCount = box.Lines.Length;
                startLine = Math.Max(0, Math.Min(startLine, lineCount - 1));
                endLine = Math.Max(0, Math.Min(endLine, lineCount - 1));
                if (startLine > endLine)
                    return;

                int rangeStart = box.GetFirstCharIndexFromLine(startLine);
                int rangeEnd;
                if (endLine + 1 < lineCount)
                    rangeEnd = box.GetFirstCharIndexFromLine(endLine + 1);
                else
                    rangeEnd = textLength;

                if (rangeStart < 0 || rangeEnd < rangeStart)
                    return;

                int pos = rangeStart;
                while (pos < rangeEnd)
                {
                    int lineStart = pos;
                    while (pos < rangeEnd && text[pos] != '\n')
                        pos++;

                    int lineEnd = pos;
                    if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
                        lineEnd--;

                    int contentStart = lineStart;
                    while (contentStart < lineEnd && char.IsWhiteSpace(text[contentStart]))
                        contentStart++;

                    if (contentStart < lineEnd)
                    {
                        if (contentStart + 1 < lineEnd &&
                            text[contentStart] == '-' &&
                            text[contentStart + 1] == ' ')
                        {
                            attributeRanges.Add(Tuple.Create(contentStart, lineEnd - contentStart));
                        }
                        else if (colorTitles)
                        {
                            int colonIndex = -1;
                            for (int i = contentStart; i < lineEnd; i++)
                            {
                                if (text[i] == ':')
                                {
                                    colonIndex = i;
                                    break;
                                }
                            }
                            if (colonIndex >= 0)
                                titleRanges.Add(Tuple.Create(contentStart, colonIndex - contentStart + 1));
                        }
                    }

                    if (pos < rangeEnd && text[pos] == '\n')
                        pos++;
                }

                foreach (var range in titleRanges)
                {
                    box.Select(range.Item1, range.Item2);
                    box.SelectionColor = TitleColor;
                }

                foreach (var range in attributeRanges)
                {
                    box.Select(range.Item1, range.Item2);
                    box.SelectionColor = AttributeColor;
                }
            }
            finally
            {
                ResumeRedraw(box);
                RestoreViewState(box, viewState);
                _isApplyingSyntaxColor = false;
            }
        }

        private static TextViewState CaptureViewState(RichTextBox box)
        {
            return new TextViewState()
            {
                SelectionStart = box.SelectionStart,
                SelectionLength = box.SelectionLength,
                ScrollPoint = GetScrollPoint(box),
            };
        }

        private static void RestoreViewState(RichTextBox box, TextViewState state)
        {
            if (box == null || box.IsDisposed)
                return;

            int textLength = box.TextLength;
            int safeSelectionStart = Math.Max(0, Math.Min(state.SelectionStart, textLength));
            int maxSelectionLength = Math.Max(0, textLength - safeSelectionStart);
            int safeSelectionLength = Math.Max(0, Math.Min(state.SelectionLength, maxSelectionLength));

            box.Select(safeSelectionStart, safeSelectionLength);
            SetScrollPoint(box, state.ScrollPoint);
        }

        private static ScrollPoint GetScrollPoint(RichTextBox box)
        {
            var point = new ScrollPoint();
            if (box != null && !box.IsDisposed && box.IsHandleCreated)
                SendMessage(box.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref point);
            return point;
        }

        private static void SetScrollPoint(RichTextBox box, ScrollPoint point)
        {
            if (box != null && !box.IsDisposed && box.IsHandleCreated)
                SendMessage(box.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref point);
        }

        private static void SuspendRedraw(Control control)
        {
            if (control != null && !control.IsDisposed && control.IsHandleCreated)
                SendMessage(control.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }

        private static void ResumeRedraw(Control control)
        {
            if (control != null && !control.IsDisposed && control.IsHandleCreated)
            {
                SendMessage(control.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                control.Invalidate();
            }
        }

    }

    internal sealed class TagProductEntry
    {
        public string Prefix { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Suffix { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new List<string>();

        public string ToTagKey()
        {
            return $"{Prefix}|{Name}|{Suffix}";
        }
    }

    internal sealed class TagProductBymlDocument
    {
        public bool IsBigEndian { get; set; }
        public ushort Version { get; set; } = 7;
        public List<string> PathList { get; set; } = new List<string>();
        public List<string> TagList { get; set; } = new List<string>();
        public byte[] BitTable { get; set; } = Array.Empty<byte>();
        public byte[] RankTable { get; set; } = Array.Empty<byte>();
    }

    internal static class TagProductBymlSerializer
    {
        private const byte NodeArray = 0xC0;
        private const byte NodeMap = 0xC1;
        private const byte NodeStringTable = 0xC2;
        private const byte ValueStringIndex = 0xA0;
        private const byte ValueBinaryData = 0xA1;

        public static TagProductBymlDocument Read(byte[] data)
        {
            using (var mem = new MemoryStream(data, writable: false))
            using (var reader = new BinaryReader(mem, Encoding.UTF8, leaveOpen: true))
            {
                bool isBigEndian = ReadEndian(reader);
                ushort version = ReadUInt16(reader, isBigEndian);
                uint keyTableOffset = ReadUInt32(reader, isBigEndian);
                uint stringTableOffset = ReadUInt32(reader, isBigEndian);
                uint rootOffset = ReadUInt32(reader, isBigEndian);

                List<string> keyTable = ReadStringTable(reader, keyTableOffset, isBigEndian);
                List<string> stringTable = ReadStringTable(reader, stringTableOffset, isBigEndian);
                Dictionary<string, object> root = ReadRootMap(reader, rootOffset, isBigEndian, keyTable, stringTable);

                var knownKeys = new HashSet<string>(StringComparer.Ordinal)
                {
                    "BitTable", "PathList", "RankTable", "TagList",
                };
                var unknown = root.Keys.Where(x => !knownKeys.Contains(x)).ToList();
                if (unknown.Count > 0)
                    throw new InvalidDataException($"Unsupported root keys: {string.Join(", ", unknown)}");

                foreach (string required in knownKeys)
                {
                    if (!root.ContainsKey(required))
                        throw new InvalidDataException($"Missing required root key: {required}");
                }

                var doc = new TagProductBymlDocument()
                {
                    IsBigEndian = isBigEndian,
                    Version = version,
                    PathList = GetStringList(root, "PathList"),
                    TagList = GetStringList(root, "TagList"),
                    BitTable = GetBinary(root, "BitTable"),
                    RankTable = GetBinary(root, "RankTable"),
                };
                return doc;
            }
        }

        public static byte[] Write(TagProductBymlDocument document)
        {
            var keys = new List<string>() { "BitTable", "PathList", "RankTable", "TagList" };

            List<string> strings = document.PathList.Concat(document.TagList)
                                                    .Select(x => x ?? string.Empty)
                                                    .Distinct(StringComparer.Ordinal)
                                                    .OrderBy(x => x, StringComparer.Ordinal)
                                                    .ToList();
            if (strings.Count == 0)
                strings.Add(string.Empty);

            var stringLookup = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < strings.Count; i++)
            {
                if (!stringLookup.ContainsKey(strings[i]))
                    stringLookup.Add(strings[i], i);
            }

            using (var mem = new MemoryStream())
            using (var writer = new BinaryWriter(mem, Encoding.UTF8, leaveOpen: true))
            {
                bool isBigEndian = document.IsBigEndian;

                WriteMagic(writer, isBigEndian);
                WriteUInt16(writer, document.Version, isBigEndian);
                long keyOffsetPos = mem.Position;
                WriteUInt32(writer, 0, isBigEndian);
                long stringOffsetPos = mem.Position;
                WriteUInt32(writer, 0, isBigEndian);
                long rootOffsetPos = mem.Position;
                WriteUInt32(writer, 0, isBigEndian);

                AlignTo4(writer);
                uint keyOffset = (uint)mem.Position;
                WriteStringTable(writer, keys, isBigEndian);

                AlignTo4(writer);
                uint stringOffset = (uint)mem.Position;
                WriteStringTable(writer, strings, isBigEndian);

                AlignTo4(writer);
                uint rootOffset = (uint)mem.Position;

                WriteByte(writer, NodeMap);
                WriteUInt24(writer, (uint)keys.Count, isBigEndian);

                long rootEntriesStart = mem.Position;
                for (int i = 0; i < keys.Count; i++)
                {
                    byte valueType = (i == 0 || i == 2) ? ValueBinaryData : NodeArray;
                    WriteUInt24(writer, (uint)i, isBigEndian);
                    WriteByte(writer, valueType);
                    WriteUInt32(writer, 0, isBigEndian);
                }

                uint bitOffset = (uint)mem.Position;
                WriteBinaryNode(writer, document.BitTable ?? Array.Empty<byte>(), isBigEndian);

                uint pathOffset = (uint)mem.Position;
                WriteStringArrayNode(writer, document.PathList ?? new List<string>(), stringLookup, isBigEndian);

                uint rankOffset = (uint)mem.Position;
                WriteBinaryNode(writer, document.RankTable ?? Array.Empty<byte>(), isBigEndian);

                uint tagOffset = (uint)mem.Position;
                WriteStringArrayNode(writer, document.TagList ?? new List<string>(), stringLookup, isBigEndian);

                PatchRootEntryValue(writer, rootEntriesStart, 0, bitOffset, isBigEndian);
                PatchRootEntryValue(writer, rootEntriesStart, 1, pathOffset, isBigEndian);
                PatchRootEntryValue(writer, rootEntriesStart, 2, rankOffset, isBigEndian);
                PatchRootEntryValue(writer, rootEntriesStart, 3, tagOffset, isBigEndian);

                long end = mem.Position;
                mem.Position = keyOffsetPos;
                WriteUInt32(writer, keyOffset, isBigEndian);
                mem.Position = stringOffsetPos;
                WriteUInt32(writer, stringOffset, isBigEndian);
                mem.Position = rootOffsetPos;
                WriteUInt32(writer, rootOffset, isBigEndian);
                mem.Position = end;

                return mem.ToArray();
            }
        }

        private static bool ReadEndian(BinaryReader reader)
        {
            byte b0 = reader.ReadByte();
            byte b1 = reader.ReadByte();

            if (b0 == 'Y' && b1 == 'B')
                return false;
            if (b0 == 'B' && b1 == 'Y')
                return true;

            throw new InvalidDataException("Invalid BYML magic. Expected YB or BY.");
        }

        private static void WriteMagic(BinaryWriter writer, bool isBigEndian)
        {
            if (isBigEndian)
            {
                writer.Write((byte)'B');
                writer.Write((byte)'Y');
            }
            else
            {
                writer.Write((byte)'Y');
                writer.Write((byte)'B');
            }
        }

        private static List<string> ReadStringTable(BinaryReader reader, uint offset, bool isBigEndian)
        {
            long savePos = reader.BaseStream.Position;
            reader.BaseStream.Position = offset;

            byte nodeType = reader.ReadByte();
            if (nodeType != NodeStringTable)
                throw new InvalidDataException($"Invalid string table node type 0x{nodeType:X2} at 0x{offset:X}.");

            int count = (int)ReadUInt24(reader, isBigEndian);
            var offsets = new uint[count];
            for (int i = 0; i < count; i++)
                offsets[i] = ReadUInt32(reader, isBigEndian);

            long nodeStart = offset;
            var result = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                reader.BaseStream.Position = nodeStart + offsets[i];
                result.Add(ReadZeroTerminatedString(reader));
            }

            reader.BaseStream.Position = savePos;
            return result;
        }

        private static Dictionary<string, object> ReadRootMap(
            BinaryReader reader,
            uint rootOffset,
            bool isBigEndian,
            List<string> keyTable,
            List<string> stringTable)
        {
            long savePos = reader.BaseStream.Position;
            reader.BaseStream.Position = rootOffset;

            byte nodeType = reader.ReadByte();
            if (nodeType != NodeMap)
                throw new InvalidDataException($"Invalid root node type 0x{nodeType:X2} at 0x{rootOffset:X}.");

            int count = (int)ReadUInt24(reader, isBigEndian);
            var root = new Dictionary<string, object>(StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                int keyIndex = (int)ReadUInt24(reader, isBigEndian);
                byte valueType = reader.ReadByte();
                uint valueRaw = ReadUInt32(reader, isBigEndian);

                if (keyIndex < 0 || keyIndex >= keyTable.Count)
                    throw new InvalidDataException($"Root key index out of range: {keyIndex}");

                string key = keyTable[keyIndex];
                object value;

                if (valueType == ValueBinaryData)
                    value = ReadBinaryNode(reader, valueRaw, isBigEndian);
                else if (valueType == NodeArray)
                    value = ReadStringArrayNode(reader, valueRaw, isBigEndian, stringTable);
                else if (valueType == ValueStringIndex)
                    value = (valueRaw < stringTable.Count) ? stringTable[(int)valueRaw] : string.Empty;
                else
                    value = valueRaw;

                root[key] = value;
            }

            reader.BaseStream.Position = savePos;
            return root;
        }

        private static byte[] ReadBinaryNode(BinaryReader reader, uint offset, bool isBigEndian)
        {
            long savePos = reader.BaseStream.Position;
            reader.BaseStream.Position = offset;

            int length = (int)ReadUInt32(reader, isBigEndian);
            if (length < 0 || reader.BaseStream.Position + length > reader.BaseStream.Length)
                throw new InvalidDataException($"Invalid binary node length {length} at 0x{offset:X}.");

            byte[] data = reader.ReadBytes(length);
            reader.BaseStream.Position = savePos;
            return data;
        }

        private static List<string> ReadStringArrayNode(BinaryReader reader, uint offset, bool isBigEndian, List<string> stringTable)
        {
            long savePos = reader.BaseStream.Position;
            reader.BaseStream.Position = offset;

            byte nodeType = reader.ReadByte();
            if (nodeType != NodeArray)
                throw new InvalidDataException($"Invalid array node type 0x{nodeType:X2} at 0x{offset:X}.");

            int count = (int)ReadUInt24(reader, isBigEndian);
            byte[] valueTypes = reader.ReadBytes(count);

            AlignRelativeTo4(reader.BaseStream, offset);

            var values = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                uint raw = ReadUInt32(reader, isBigEndian);
                if (valueTypes[i] == ValueStringIndex && raw < stringTable.Count)
                    values.Add(stringTable[(int)raw] ?? string.Empty);
                else
                    values.Add(string.Empty);
            }

            reader.BaseStream.Position = savePos;
            return values;
        }

        private static List<string> GetStringList(Dictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out object value))
                return new List<string>();

            if (value is List<string> strings)
                return new List<string>(strings);

            if (value is IEnumerable<object> objects)
                return objects.Select(x => x?.ToString() ?? string.Empty).ToList();

            return new List<string>();
        }

        private static byte[] GetBinary(Dictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out object value))
                return Array.Empty<byte>();

            if (value is byte[] bytes)
                return (byte[])bytes.Clone();

            return Array.Empty<byte>();
        }

        private static void WriteStringTable(BinaryWriter writer, List<string> strings, bool isBigEndian)
        {
            long nodeStart = writer.BaseStream.Position;
            WriteByte(writer, NodeStringTable);
            WriteUInt24(writer, (uint)strings.Count, isBigEndian);

            long offsetsPos = writer.BaseStream.Position;
            for (int i = 0; i < strings.Count + 1; i++)
                WriteUInt32(writer, 0, isBigEndian);

            var stringOffsets = new uint[strings.Count + 1];
            for (int i = 0; i < strings.Count; i++)
            {
                stringOffsets[i] = (uint)(writer.BaseStream.Position - nodeStart);
                WriteZeroTerminatedString(writer, strings[i] ?? string.Empty);
            }
            stringOffsets[strings.Count] = (uint)(writer.BaseStream.Position - nodeStart);

            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = offsetsPos;
            for (int i = 0; i < strings.Count + 1; i++)
                WriteUInt32(writer, stringOffsets[i], isBigEndian);
            writer.BaseStream.Position = end;
        }

        private static void WriteStringArrayNode(
            BinaryWriter writer,
            List<string> values,
            Dictionary<string, int> stringLookup,
            bool isBigEndian)
        {
            long nodeStart = writer.BaseStream.Position;
            WriteByte(writer, NodeArray);
            WriteUInt24(writer, (uint)values.Count, isBigEndian);

            for (int i = 0; i < values.Count; i++)
                WriteByte(writer, ValueStringIndex);

            AlignRelativeTo4(writer.BaseStream, nodeStart, writer);

            for (int i = 0; i < values.Count; i++)
            {
                string value = values[i] ?? string.Empty;
                if (!stringLookup.TryGetValue(value, out int index))
                    index = 0;
                WriteUInt32(writer, (uint)index, isBigEndian);
            }
        }

        private static void WriteBinaryNode(BinaryWriter writer, byte[] data, bool isBigEndian)
        {
            data = data ?? Array.Empty<byte>();
            WriteUInt32(writer, (uint)data.Length, isBigEndian);
            if (data.Length > 0)
                writer.Write(data);
            AlignTo4(writer);
        }

        private static void PatchRootEntryValue(BinaryWriter writer, long rootEntriesStart, int entryIndex, uint value, bool isBigEndian)
        {
            long savePos = writer.BaseStream.Position;
            writer.BaseStream.Position = rootEntriesStart + (entryIndex * 8) + 4;
            WriteUInt32(writer, value, isBigEndian);
            writer.BaseStream.Position = savePos;
        }

        private static ushort ReadUInt16(BinaryReader reader, bool isBigEndian)
        {
            byte b0 = reader.ReadByte();
            byte b1 = reader.ReadByte();
            if (isBigEndian)
                return (ushort)((b0 << 8) | b1);
            return (ushort)(b0 | (b1 << 8));
        }

        private static uint ReadUInt24(BinaryReader reader, bool isBigEndian)
        {
            byte b0 = reader.ReadByte();
            byte b1 = reader.ReadByte();
            byte b2 = reader.ReadByte();
            if (isBigEndian)
                return (uint)((b0 << 16) | (b1 << 8) | b2);
            return (uint)(b0 | (b1 << 8) | (b2 << 16));
        }

        private static uint ReadUInt32(BinaryReader reader, bool isBigEndian)
        {
            byte b0 = reader.ReadByte();
            byte b1 = reader.ReadByte();
            byte b2 = reader.ReadByte();
            byte b3 = reader.ReadByte();
            if (isBigEndian)
                return (uint)((b0 << 24) | (b1 << 16) | (b2 << 8) | b3);
            return (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        }

        private static void WriteUInt16(BinaryWriter writer, ushort value, bool isBigEndian)
        {
            if (isBigEndian)
            {
                writer.Write((byte)((value >> 8) & 0xFF));
                writer.Write((byte)(value & 0xFF));
            }
            else
            {
                writer.Write((byte)(value & 0xFF));
                writer.Write((byte)((value >> 8) & 0xFF));
            }
        }

        private static void WriteUInt24(BinaryWriter writer, uint value, bool isBigEndian)
        {
            if (isBigEndian)
            {
                writer.Write((byte)((value >> 16) & 0xFF));
                writer.Write((byte)((value >> 8) & 0xFF));
                writer.Write((byte)(value & 0xFF));
            }
            else
            {
                writer.Write((byte)(value & 0xFF));
                writer.Write((byte)((value >> 8) & 0xFF));
                writer.Write((byte)((value >> 16) & 0xFF));
            }
        }

        private static void WriteUInt32(BinaryWriter writer, uint value, bool isBigEndian)
        {
            if (isBigEndian)
            {
                writer.Write((byte)((value >> 24) & 0xFF));
                writer.Write((byte)((value >> 16) & 0xFF));
                writer.Write((byte)((value >> 8) & 0xFF));
                writer.Write((byte)(value & 0xFF));
            }
            else
            {
                writer.Write((byte)(value & 0xFF));
                writer.Write((byte)((value >> 8) & 0xFF));
                writer.Write((byte)((value >> 16) & 0xFF));
                writer.Write((byte)((value >> 24) & 0xFF));
            }
        }

        private static string ReadZeroTerminatedString(BinaryReader reader)
        {
            using (var mem = new MemoryStream())
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    byte b = reader.ReadByte();
                    if (b == 0)
                        break;
                    mem.WriteByte(b);
                }
                return Encoding.UTF8.GetString(mem.ToArray());
            }
        }

        private static void WriteZeroTerminatedString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > 0)
                writer.Write(bytes);
            writer.Write((byte)0);
        }

        private static void WriteByte(BinaryWriter writer, byte value)
        {
            writer.Write(value);
        }

        private static void AlignTo4(BinaryWriter writer)
        {
            while ((writer.BaseStream.Position & 3) != 0)
                writer.Write((byte)0);
        }

        private static void AlignRelativeTo4(Stream stream, long nodeStart)
        {
            while (((stream.Position - nodeStart) & 3) != 0)
                stream.Position++;
        }

        private static void AlignRelativeTo4(Stream stream, long nodeStart, BinaryWriter writer)
        {
            while (((stream.Position - nodeStart) & 3) != 0)
                writer.Write((byte)0);
        }
    }
}
