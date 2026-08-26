using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DayZModWorkbench
{
    internal sealed class ModChoice
    {
        public string Name;
        public string FullPath;
        public string WorkshopId;

        public static ModChoice FromSteamPath(string path)
        {
            return new ModChoice
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                WorkshopId = ReadWorkshopId(path)
            };
        }

        public bool Matches(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return true;
            string value = filter.Trim();
            return Name.IndexOf(value, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (!string.IsNullOrWhiteSpace(WorkshopId)
                    && WorkshopId.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(WorkshopId)
                ? Name
                : Name + "   │   Workshop ID: " + WorkshopId;
        }

        private static string ReadWorkshopId(string modPath)
        {
            string meta = Path.Combine(modPath, "meta.cpp");
            if (!File.Exists(meta)) return string.Empty;
            try
            {
                Match match = Regex.Match(File.ReadAllText(meta), @"\bpublishedid\s*=\s*[\""']?(\d+)", RegexOptions.IgnoreCase);
                return match.Success ? match.Groups[1].Value : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    internal sealed class ModSelectionForm : Form
    {
        private readonly List<ModChoice> _steamMods;
        private readonly List<ModChoice> _benchMods;
        private readonly HashSet<string> _checkedSteam;
        private readonly HashSet<string> _checkedBench;
        private CheckedListBox _steamList;
        private CheckedListBox _benchList;
        private TextBox _steamSearch;
        private TextBox _benchSearch;
        private bool _updating;

        private readonly Color _background = Color.FromArgb(22, 25, 30);
        private readonly Color _panel = Color.FromArgb(31, 35, 42);
        private readonly Color _field = Color.FromArgb(42, 47, 56);
        private readonly Color _accent = Color.FromArgb(0, 190, 230);
        private readonly Color _text = Color.FromArgb(230, 235, 242);

        public IEnumerable<string> SelectedSteamPaths { get { return _checkedSteam.ToArray(); } }
        public IEnumerable<string> SelectedBenchPaths { get { return _checkedBench.ToArray(); } }

        public ModSelectionForm(IEnumerable<ModChoice> steamMods, IEnumerable<ModChoice> benchMods,
            IEnumerable<string> defaultSteam, IEnumerable<string> defaultBench)
        {
            _steamMods = steamMods.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _benchMods = benchMods.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _checkedSteam = new HashSet<string>(defaultSteam ?? new string[0], StringComparer.OrdinalIgnoreCase);
            _checkedBench = new HashSet<string>(defaultBench ?? new string[0], StringComparer.OrdinalIgnoreCase);

            Text = "Escolher mods para abrir com DayZ Editor";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(920, 620);
            Size = new Size(1080, 700);
            BackColor = _background;
            ForeColor = _text;
            Font = new Font("Segoe UI", 9.5f);
            BuildInterface();
            RefreshLists();
        }

        public static ModSelectionForm CreateFromPaths(string workshopPath, string benchPath, string defaultSteamPaths, string defaultBenchPaths)
        {
            List<ModChoice> steam = Directory.Exists(workshopPath)
                ? Directory.GetDirectories(workshopPath)
                    .Where(path => !Path.GetFileName(path).StartsWith("!", StringComparison.OrdinalIgnoreCase))
                    .Select(ModChoice.FromSteamPath).ToList()
                : new List<ModChoice>();
            List<ModChoice> bench = Directory.Exists(benchPath)
                ? Directory.GetDirectories(benchPath)
                    .Select(path => new ModChoice { Name = Path.GetFileName(path), FullPath = path }).ToList()
                : new List<ModChoice>();
            string[] defaultSteam = (defaultSteamPaths ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            string[] defaultBench = (defaultBenchPaths ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            return new ModSelectionForm(steam, bench, defaultSteam, defaultBench);
        }

        private void BuildInterface()
        {
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                RowCount = 4,
                ColumnCount = 2,
                BackColor = _background
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            Controls.Add(root);

            Label title = new Label
            {
                Text = "SELECIONE OS MODS QUE SERÃO CARREGADOS",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
                ForeColor = _accent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(title, 0, 0);
            root.SetColumnSpan(title, 2);

            root.Controls.Add(BuildSide(true), 0, 1);
            root.Controls.Add(BuildSide(false), 1, 1);

            Label order = new Label
            {
                Text = "Ordem de carregamento: mods Steam selecionados → projetos da bancada selecionados.\nOs projetos da bancada serão convertidos automaticamente em mods de teste quando possuírem uma pasta PBO.",
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver,
                TextAlign = ContentAlignment.MiddleLeft
            };
            root.Controls.Add(order, 0, 2);
            root.SetColumnSpan(order, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 7, 0, 0)
            };
            Button launch = MakeButton("Abrir DayZ Editor", _accent, 180);
            launch.DialogResult = DialogResult.OK;
            Button cancel = MakeButton("Cancelar", _field, 110);
            cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(launch);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 3);
            root.SetColumnSpan(buttons, 2);
            AcceptButton = launch;
            CancelButton = cancel;
        }

        private Control BuildSide(bool steam)
        {
            GroupBox group = new GroupBox
            {
                Text = steam ? "Mods baixados pela Steam (!Workshop)" : "Projetos da bancada (edit mod)",
                Dock = DockStyle.Fill,
                BackColor = _panel,
                ForeColor = _text,
                Padding = new Padding(10)
            };
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            TextBox search = new TextBox { Dock = DockStyle.Fill, BackColor = _field, ForeColor = _text };
            search.TextChanged += delegate { RefreshLists(); };
            layout.Controls.Add(search, 0, 0);
            layout.SetColumnSpan(search, 2);

            CheckedListBox list = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                BackColor = _field,
                ForeColor = _text,
                BorderStyle = BorderStyle.FixedSingle,
                HorizontalScrollbar = true
            };
            if (steam)
            {
                _steamSearch = search;
                _steamList = list;
                list.ItemCheck += delegate(object sender, ItemCheckEventArgs e) { UpdateChecked(_steamList, _checkedSteam, e); };
            }
            else
            {
                _benchSearch = search;
                _benchList = list;
                list.ItemCheck += delegate(object sender, ItemCheckEventArgs e) { UpdateChecked(_benchList, _checkedBench, e); };
            }
            layout.Controls.Add(list, 0, 1);
            layout.SetColumnSpan(list, 2);

            Button all = MakeButton("Marcar todos", _field, 100);
            all.Click += delegate { SetAll(steam, true); };
            Button none = MakeButton("Limpar", _field, 100);
            none.Click += delegate { SetAll(steam, false); };
            layout.Controls.Add(all, 0, 2);
            layout.Controls.Add(none, 1, 2);
            group.Controls.Add(layout);
            return group;
        }

        private Button MakeButton(string text, Color color, int width)
        {
            Button button = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                ForeColor = Color.White
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        private void UpdateChecked(CheckedListBox list, HashSet<string> selected, ItemCheckEventArgs e)
        {
            if (_updating || e.Index < 0 || e.Index >= list.Items.Count) return;
            ModChoice item = list.Items[e.Index] as ModChoice;
            if (item == null) return;
            if (e.NewValue == CheckState.Checked) selected.Add(item.FullPath);
            else selected.Remove(item.FullPath);
        }

        private void RefreshLists()
        {
            if (_steamList == null || _benchList == null) return;
            FillList(_steamList, _steamMods, _steamSearch.Text, _checkedSteam);
            FillList(_benchList, _benchMods, _benchSearch.Text, _checkedBench);
        }

        private void FillList(CheckedListBox list, IEnumerable<ModChoice> all, string filter, HashSet<string> selected)
        {
            _updating = true;
            try
            {
                list.Items.Clear();
                IEnumerable<ModChoice> visible = all;
                if (!string.IsNullOrWhiteSpace(filter))
                    visible = visible.Where(x => x.Matches(filter));
                foreach (ModChoice item in visible) list.Items.Add(item, selected.Contains(item.FullPath));
            }
            finally
            {
                _updating = false;
            }
        }

        private void SetAll(bool steam, bool check)
        {
            CheckedListBox list = steam ? _steamList : _benchList;
            HashSet<string> selected = steam ? _checkedSteam : _checkedBench;
            foreach (ModChoice item in list.Items)
            {
                if (check) selected.Add(item.FullPath);
                else selected.Remove(item.FullPath);
            }
            RefreshLists();
        }
    }
}
