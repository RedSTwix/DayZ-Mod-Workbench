using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DayZModWorkbench
{
    internal sealed class PathItem
    {
        public string Name;
        public string FullPath;
        public override string ToString() { return Name; }
    }

    internal sealed class MainForm : Form
    {
        private readonly Color _background = Color.FromArgb(22, 25, 30);
        private readonly Color _panel = Color.FromArgb(31, 35, 42);
        private readonly Color _field = Color.FromArgb(42, 47, 56);
        private readonly Color _accent = Color.FromArgb(0, 190, 230);
        private readonly Color _success = Color.FromArgb(64, 205, 140);
        private readonly Color _text = Color.FromArgb(230, 235, 242);

        private ToolSettings _settings;
        private ComboBox _projectCombo;
        private ListBox _pboList;
        private ComboBox _sourceCombo;
        private TextBox _prefixText;
        private TextBox _privateKeyText;
        private CheckBox _loadingTestCheck;
        private CheckBox _filePatchingCheck;
        private RichTextBox _log;
        private ToolStripStatusLabel _status;
        private TabControl _tabs;
        private TextBox _steamImportSearch;
        private CheckedListBox _steamImportList;
        private readonly List<ModChoice> _steamImportMods = new List<ModChoice>();
        private readonly HashSet<string> _steamImportSelected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _steamImportWarnings = new List<string>();
        private bool _updatingSteamImportList;

        private TextBox _benchText;
        private TextBox _bankRevText;
        private TextBox _fileBankText;
        private TextBox _signerText;
        private TextBox _dayZText;
        private TextBox _dayZDiagText;
        private TextBox _editorText;
        private TextBox _workshopText;
        private TextBox _editorDependenciesText;
        private TextBox _serverKeysText;
        private TextBox _backupText;
        private TextBox _launchArgsText;

        private readonly List<Control> _operationControls = new List<Control>();
        private bool _busy;

        public MainForm()
        {
            _settings = ToolSettings.Load();
            Text = "DayZ Mod Workbench — SharpAxe";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1040, 720);
            Size = new Size(1220, 820);
            BackColor = _background;
            ForeColor = _text;
            Font = new Font("Segoe UI", 9.5f);

            BuildInterface();
            ApplySettingsToFields();
            ApplyTheme(this);
            RefreshProjects();
            LoadSteamImportMods();
        }

        private void BuildInterface()
        {
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 175));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(BuildProjectTab());
            _tabs.TabPages.Add(BuildLaunchTab());
            _tabs.TabPages.Add(BuildSteamImportTab());
            _tabs.TabPages.Add(BuildSettingsTab());
            root.Controls.Add(_tabs, 0, 1);

            GroupBox logGroup = new GroupBox { Text = "Registro das operações", Dock = DockStyle.Fill };
            _log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(14, 17, 21),
                ForeColor = Color.FromArgb(190, 225, 230),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f)
            };
            logGroup.Controls.Add(_log);
            root.Controls.Add(logGroup, 0, 2);

            StatusStrip strip = new StatusStrip { SizingGrip = false };
            _status = new ToolStripStatusLabel("Pronto") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            strip.Items.Add(_status);
            root.Controls.Add(strip, 0, 3);
        }

        private Control BuildHeader()
        {
            TableLayoutPanel header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 6,
                Padding = new Padding(4)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

            Label title = new Label
            {
                Text = "DAYZ MOD\nWORKBENCH",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
                ForeColor = _accent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(new Label { Text = "Projeto:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 1, 0);

            _projectCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(8, 12, 8, 10) };
            _projectCombo.SelectedIndexChanged += delegate { RefreshProjectContents(); };
            header.Controls.Add(_projectCombo, 2, 0);

            Button refresh = MakeButton("Atualizar", _accent);
            refresh.Click += delegate { RefreshProjects(); };
            header.Controls.Add(refresh, 3, 0);
            _operationControls.Add(refresh);

            Button open = MakeButton("Abrir projeto", _field);
            open.Click += delegate { OpenSelectedProject(); };
            header.Controls.Add(open, 4, 0);

            Button settings = MakeButton("Configurações", _field);
            settings.Click += delegate { _tabs.SelectedIndex = 3; };
            header.Controls.Add(settings, 5, 0);
            return header;
        }

        private TabPage BuildProjectTab()
        {
            TabPage page = new TabPage("Extrair e compilar");
            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 465,
                FixedPanel = FixedPanel.Panel1
            };
            page.Controls.Add(split);

            GroupBox pboGroup = new GroupBox { Text = "PBOs encontrados no projeto", Dock = DockStyle.Fill, Padding = new Padding(10) };
            TableLayoutPanel pboLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            pboLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pboLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            pboLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            _pboList = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
            _pboList.SelectedIndexChanged += async delegate { await ReadSelectedPboProperties(); };
            pboLayout.Controls.Add(_pboList, 0, 0);
            pboLayout.Controls.Add(new Label
            {
                Text = "O conteúdo será extraído para source\\NomeDoPBO.",
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 1);
            Button extract = MakeButton("Extrair PBO selecionado → source", _accent);
            extract.Click += async delegate { await ExtractSelectedPbo(); };
            pboLayout.Controls.Add(extract, 0, 2);
            _operationControls.Add(extract);
            pboGroup.Controls.Add(pboLayout);
            split.Panel1.Controls.Add(pboGroup);

            GroupBox buildGroup = new GroupBox { Text = "Compilar source → PBO / BISIGN", Dock = DockStyle.Fill, Padding = new Padding(12) };
            TableLayoutPanel build = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 8 };
            build.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            build.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            build.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            for (int i = 0; i < 6; i++) build.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            build.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            build.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

            build.Controls.Add(FieldLabel("Source:"), 0, 0);
            _sourceCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            _sourceCombo.SelectedIndexChanged += delegate { LoadSourcePrefix(); };
            build.Controls.Add(_sourceCombo, 1, 0);
            Button openSource = MakeButton("Abrir", _field);
            openSource.Click += delegate { OpenSelectedSource(); };
            build.Controls.Add(openSource, 2, 0);

            build.Controls.Add(FieldLabel("Prefix do PBO:"), 0, 1);
            _prefixText = new TextBox { Dock = DockStyle.Fill };
            build.Controls.Add(_prefixText, 1, 1);
            build.SetColumnSpan(_prefixText, 2);

            build.Controls.Add(FieldLabel("Chave privada:"), 0, 2);
            _privateKeyText = new TextBox { Dock = DockStyle.Fill };
            build.Controls.Add(_privateKeyText, 1, 2);
            Button keyBrowse = MakeButton("Procurar", _field);
            keyBrowse.Click += delegate { BrowseFile(_privateKeyText, "Chave privada DayZ|*.biprivatekey|Todos os arquivos|*.*"); };
            build.Controls.Add(keyBrowse, 2, 2);

            build.Controls.Add(FieldLabel("Saída:"), 0, 3);
            Label output = new Label
            {
                Text = "Projeto\\PBO  (a chave nunca é copiada)",
                Dock = DockStyle.Fill,
                ForeColor = _success,
                TextAlign = ContentAlignment.MiddleLeft
            };
            build.Controls.Add(output, 1, 3);
            build.SetColumnSpan(output, 2);

            Button buildOnly = MakeButton("Compilar PBO", _field);
            buildOnly.Click += async delegate { await BuildSelectedSource(false); };
            build.Controls.Add(buildOnly, 1, 4);
            Button buildSign = MakeButton("PBO + BISIGN", _success);
            buildSign.Click += async delegate { await BuildSelectedSource(true); };
            build.Controls.Add(buildSign, 2, 4);
            _operationControls.Add(buildOnly);
            _operationControls.Add(buildSign);

            Button buildAll = MakeButton("Compilar e assinar todos os sources", _accent);
            buildAll.Click += async delegate { await BuildAllSources(); };
            build.Controls.Add(buildAll, 1, 5);
            build.SetColumnSpan(buildAll, 2);
            _operationControls.Add(buildAll);

            Label note = new Label
            {
                Text = "BankRev extrai todos os arquivos existentes no PBO. Config.bin e modelos ODOL permanecem binarizados quando o autor original os publicou dessa forma.",
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver,
                AutoSize = false
            };
            build.Controls.Add(note, 0, 6);
            build.SetColumnSpan(note, 3);

            Button verify = MakeButton("Verificar assinaturas da pasta PBO", _field);
            verify.Click += async delegate { await VerifyProjectSignatures(); };
            build.Controls.Add(verify, 1, 7);
            build.SetColumnSpan(verify, 2);
            _operationControls.Add(verify);

            buildGroup.Controls.Add(build);
            split.Panel2.Controls.Add(buildGroup);
            return page;
        }

        private TabPage BuildLaunchTab()
        {
            TabPage page = new TabPage("Executar e testar");
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20),
                ColumnCount = 3,
                RowCount = 6
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            layout.Controls.Add(FieldLabel("Opções:"), 0, 0);
            FlowLayoutPanel options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            _loadingTestCheck = new CheckBox { Text = "-loadingTest", AutoSize = true, Checked = true };
            _filePatchingCheck = new CheckBox { Text = "-filePatching", AutoSize = true, Checked = false };
            options.Controls.Add(_loadingTestCheck);
            options.Controls.Add(_filePatchingCheck);
            layout.Controls.Add(options, 1, 0);
            layout.SetColumnSpan(options, 2);

            Label testInfo = new Label
            {
                Text = "Ao executar, todos os PBOs de Projeto\\PBO são copiados para Projeto\\_test\\@Nome\\Addons. Essa pasta é descartável e não altera os arquivos de edição.",
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver
            };
            layout.Controls.Add(testInfo, 0, 1);
            layout.SetColumnSpan(testInfo, 3);

            Button prepare = MakeButton("Preparar mod de teste", _field);
            prepare.Click += delegate { PrepareTestMod(true); };
            layout.Controls.Add(prepare, 1, 2);
            Button openTest = MakeButton("Abrir pasta _test", _field);
            openTest.Click += delegate { OpenTestFolder(); };
            layout.Controls.Add(openTest, 2, 2);
            _operationControls.Add(prepare);

            Button diag = MakeButton("TESTAR NO DAYZDIAG", _accent);
            diag.Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
            diag.Click += delegate { Launch(false, true); };
            layout.Controls.Add(diag, 1, 3);
            layout.SetColumnSpan(diag, 2);
            _operationControls.Add(diag);

            Button editor = MakeButton("ABRIR COM DAYZ EDITOR", Color.FromArgb(155, 90, 230));
            editor.Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
            editor.Click += delegate { LaunchWithModSelection(); };
            layout.Controls.Add(editor, 1, 4);
            Button client = MakeButton("Abrir DayZ normal", _success);
            client.Click += delegate { Launch(false, false); };
            layout.Controls.Add(client, 2, 4);
            _operationControls.Add(editor);
            _operationControls.Add(client);

            Label editorInfo = new Label
            {
                Text = "Modo Editor abre uma janela para escolher os mods da Steam e os projetos da bancada. CF, Dabs Framework, @DayZ-Editor e o projeto atual começam marcados.",
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver
            };
            layout.Controls.Add(editorInfo, 0, 5);
            layout.SetColumnSpan(editorInfo, 3);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildSettingsTab()
        {
            TabPage page = new TabPage("Configurações");
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14),
                AutoScroll = true,
                ColumnCount = 3,
                RowCount = 14
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            for (int i = 0; i < 13; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            int row = 0;
            _benchText = AddPathRow(grid, row++, "Bancada de mods:", true, false);
            _bankRevText = AddPathRow(grid, row++, "BankRev.exe:", false, false);
            _fileBankText = AddPathRow(grid, row++, "FileBank.exe:", false, false);
            _signerText = AddPathRow(grid, row++, "DSSignFile.exe:", false, false);
            _dayZText = AddPathRow(grid, row++, "DayZ_x64.exe:", false, false);
            _dayZDiagText = AddPathRow(grid, row++, "DayZDiag_x64.exe:", false, false);
            _editorText = AddPathRow(grid, row++, "Pasta @DayZ-Editor:", true, false);
            _workshopText = AddPathRow(grid, row++, "Pasta !Workshop:", true, false);
            _editorDependenciesText = AddPathRow(grid, row++, "Dependências Editor:", false, true);
            _serverKeysText = AddPathRow(grid, row++, "Chaves públicas servidor:", true, false);
            _backupText = AddPathRow(grid, row++, "Pasta de backups:", true, false);

            grid.Controls.Add(FieldLabel("Argumentos padrão:"), 0, row);
            _launchArgsText = new TextBox { Dock = DockStyle.Fill };
            grid.Controls.Add(_launchArgsText, 1, row);
            grid.SetColumnSpan(_launchArgsText, 2);
            row++;

            Button save = MakeButton("Salvar configurações", _accent);
            save.Click += delegate { SaveSettings(); };
            grid.Controls.Add(save, 1, row);
            Button validate = MakeButton("Validar", _success);
            validate.Click += delegate { ValidateSettings(true); };
            grid.Controls.Add(validate, 2, row);
            page.Controls.Add(grid);
            return page;
        }

        private TabPage BuildSteamImportTab()
        {
            TabPage page = new TabPage("Importar da Steam");
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 3,
                RowCount = 5
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

            layout.Controls.Add(FieldLabel("Pesquisar mod Steam:"), 0, 0);
            _steamImportSearch = new TextBox { Dock = DockStyle.Fill };
            _steamImportSearch.TextChanged += delegate { RefreshSteamImportList(); };
            layout.Controls.Add(_steamImportSearch, 1, 0);
            layout.SetColumnSpan(_steamImportSearch, 2);

            _steamImportList = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                HorizontalScrollbar = true
            };
            _steamImportList.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
            {
                if (_updatingSteamImportList || e.Index < 0 || e.Index >= _steamImportList.Items.Count) return;
                ModChoice item = _steamImportList.Items[e.Index] as ModChoice;
                if (item == null) return;
                if (e.NewValue == CheckState.Checked) _steamImportSelected.Add(item.FullPath);
                else _steamImportSelected.Remove(item.FullPath);
            };
            layout.Controls.Add(_steamImportList, 0, 1);
            layout.SetColumnSpan(_steamImportList, 3);

            Label info = new Label
            {
                Text = "Destino: edit mod\\NomeDoMod\\PBO. A extração cria source\\NomeDoPBO. Chaves não são copiadas.",
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver,
                TextAlign = ContentAlignment.MiddleLeft
            };
            layout.Controls.Add(info, 0, 2);
            layout.SetColumnSpan(info, 3);

            Button refresh = MakeButton("Atualizar lista Steam", _field);
            refresh.Click += delegate { LoadSteamImportMods(); };
            layout.Controls.Add(refresh, 1, 3);
            Button copy = MakeButton("COPIAR PARA BANCADA", _accent);
            copy.Click += async delegate { await ImportSelectedSteamMods(false); };
            layout.Controls.Add(copy, 2, 3);

            Button openBench = MakeButton("Abrir bancada", _field);
            openBench.Click += delegate { OpenFolder(_settings.BenchPath); };
            layout.Controls.Add(openBench, 1, 4);
            Button extract = MakeButton("COPIAR + EXTRAIR PBOS", _success);
            extract.Click += async delegate { await ImportSelectedSteamMods(true); };
            layout.Controls.Add(extract, 2, 4);

            _operationControls.Add(refresh);
            _operationControls.Add(copy);
            _operationControls.Add(extract);
            page.Controls.Add(layout);
            return page;
        }

        private TextBox AddPathRow(TableLayoutPanel grid, int row, string label, bool folder, bool noBrowse)
        {
            grid.Controls.Add(FieldLabel(label), 0, row);
            TextBox box = new TextBox { Dock = DockStyle.Fill };
            grid.Controls.Add(box, 1, row);
            if (noBrowse)
            {
                Label hint = new Label { Text = "separar por ;", Dock = DockStyle.Fill, ForeColor = Color.Silver, TextAlign = ContentAlignment.MiddleLeft };
                grid.Controls.Add(hint, 2, row);
            }
            else
            {
                Button browse = MakeButton("Procurar", _field);
                if (folder)
                    browse.Click += delegate { BrowseFolder(box); };
                else
                    browse.Click += delegate { BrowseFile(box, "Executável|*.exe|Todos os arquivos|*.*"); };
                grid.Controls.Add(browse, 2, row);
            }
            return box;
        }

        private void LoadSteamImportMods()
        {
            _steamImportMods.Clear();
            if (Directory.Exists(_settings.WorkshopPath))
            {
                foreach (string directory in Directory.GetDirectories(_settings.WorkshopPath)
                    .Where(path => !Path.GetFileName(path).StartsWith("!", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase))
                {
                    _steamImportMods.Add(ModChoice.FromSteamPath(directory));
                }
            }
            RefreshSteamImportList();
            Log("Lista Steam atualizada: " + _steamImportMods.Count + " mod(s).", _accent);
        }

        private void RefreshSteamImportList()
        {
            if (_steamImportList == null) return;
            string filter = _steamImportSearch == null ? string.Empty : _steamImportSearch.Text.Trim();
            _updatingSteamImportList = true;
            try
            {
                _steamImportList.Items.Clear();
                foreach (ModChoice mod in _steamImportMods.Where(item => item.Matches(filter)))
                {
                    _steamImportList.Items.Add(mod, _steamImportSelected.Contains(mod.FullPath));
                }
            }
            finally
            {
                _updatingSteamImportList = false;
            }
        }

        private async Task ImportSelectedSteamMods(bool extract)
        {
            // Commit a check that may still be pending when the button is clicked.
            List<ModChoice> selected = _steamImportMods.Where(mod => _steamImportSelected.Contains(mod.FullPath)).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Marque pelo menos um mod da Steam.", "Importar da Steam", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult confirm = MessageBox.Show(this,
                (extract ? "Copiar e extrair " : "Copiar ") + selected.Count + " mod(s) para a bancada?\n\n" +
                string.Join("\n", selected.Select(mod => "• " + mod.Name).ToArray()),
                "Confirmar importação", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (confirm != DialogResult.OK) return;

            try
            {
                SetBusy(true, extract ? "Copiando e extraindo mods Steam" : "Copiando mods Steam");
                _steamImportWarnings.Clear();
                List<string> importedProjects = new List<string>();
                foreach (ModChoice mod in selected)
                {
                    string project = await ImportSteamMod(mod, extract);
                    importedProjects.Add(project);
                }

                RefreshProjects();
                _steamImportSelected.Clear();
                RefreshSteamImportList();
                string warnings = _steamImportWarnings.Count == 0
                    ? string.Empty
                    : "\n\nAvisos:\n" + string.Join("\n", _steamImportWarnings.Select(item => "• " + item).ToArray());
                MessageBox.Show(this,
                    "Importação concluída:\n\n" + string.Join("\n", importedProjects) + warnings,
                    "Importar da Steam", MessageBoxButtons.OK,
                    _steamImportWarnings.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ShowError("Falha ao importar mod da Steam.", ex);
            }
            finally
            {
                SetBusy(false, "Pronto");
            }
        }

        private async Task<string> ImportSteamMod(ModChoice mod, bool extract)
        {
            string projectName = MakeProjectFolderName(mod.Name.TrimStart('@'));
            string projectRoot = Path.Combine(_settings.BenchPath, projectName);
            string pboOutput = Path.Combine(projectRoot, "PBO");
            string addons = Path.Combine(mod.FullPath, "Addons");
            if (!Directory.Exists(addons)) addons = Path.Combine(mod.FullPath, "addons");
            if (!Directory.Exists(addons)) throw new DirectoryNotFoundException("O mod não possui pasta Addons: " + mod.FullPath);

            string[] pbos = Directory.GetFiles(addons, "*.pbo", SearchOption.TopDirectoryOnly);
            if (pbos.Length == 0) throw new InvalidOperationException("Nenhum PBO encontrado em " + addons);

            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(pboOutput);
            string backupRoot = Path.Combine(_settings.BackupPath, "Steam Imports", projectName, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            bool createdBackup = false;

            Log("Importando " + mod.Name + " (" + pbos.Length + " PBOs)", _accent);
            foreach (string pbo in pbos)
            {
                string destinationPbo = Path.Combine(pboOutput, Path.GetFileName(pbo));
                if (File.Exists(destinationPbo))
                {
                    Directory.CreateDirectory(backupRoot);
                    File.Copy(destinationPbo, Path.Combine(backupRoot, Path.GetFileName(destinationPbo)), true);
                    foreach (string oldSign in Directory.GetFiles(pboOutput, Path.GetFileName(pbo) + ".*.bisign"))
                        File.Copy(oldSign, Path.Combine(backupRoot, Path.GetFileName(oldSign)), true);
                    createdBackup = true;
                }

                File.Copy(pbo, destinationPbo, true);
                foreach (string signature in Directory.GetFiles(addons, Path.GetFileName(pbo) + ".*.bisign"))
                    File.Copy(signature, Path.Combine(pboOutput, Path.GetFileName(signature)), true);

                if (extract) await ExtractImportedPbo(projectRoot, destinationPbo);
            }

            CopyIfExists(Path.Combine(mod.FullPath, "mod.cpp"), Path.Combine(projectRoot, "mod.cpp"));
            CopyIfExists(Path.Combine(mod.FullPath, "meta.cpp"), Path.Combine(projectRoot, "meta.cpp"));
            if (createdBackup) Log("Versão anterior preservada em: " + backupRoot, Color.Gold);
            Log("Importado para: " + projectRoot, _success);
            return projectRoot;
        }

        internal async Task<bool> RunAutomatedSteamImport(string modName)
        {
            try
            {
                ModChoice mod = _steamImportMods.FirstOrDefault(item => item.Name.Equals(modName, StringComparison.OrdinalIgnoreCase)
                    || item.Name.TrimStart('@').Equals(modName.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
                if (mod == null) throw new InvalidOperationException("Mod Steam não encontrado: " + modName);
                _steamImportWarnings.Clear();
                SetBusy(true, "Teste interno: copiando e extraindo " + mod.Name);
                string project = await ImportSteamMod(mod, true);
                string status = _steamImportWarnings.Count == 0 ? "APROVADO" : "APROVADO COM AVISO";
                string details = _steamImportWarnings.Count == 0 ? string.Empty : " | " + string.Join(" | ", _steamImportWarnings.ToArray());
                Log("TESTE INTERNO " + status + ": " + project + details, _steamImportWarnings.Count == 0 ? _success : Color.Gold);
                DiagnosticLog("Teste de importação " + status.ToLowerInvariant() + ": " + project + details);
                return true;
            }
            catch (Exception ex)
            {
                Log("TESTE INTERNO FALHOU: " + ex, Color.Salmon);
                DiagnosticLog("Teste de importação falhou: " + ex);
                return false;
            }
            finally
            {
                SetBusy(false, "Pronto");
            }
        }

        private async Task ExtractImportedPbo(string projectRoot, string pbo)
        {
            if (!File.Exists(_settings.BankRevPath)) throw new FileNotFoundException("BankRev.exe não encontrado.", _settings.BankRevPath);
            string pboName = Path.GetFileNameWithoutExtension(pbo);
            ProcessResult properties = await ProcessRunner.RunAsync(_settings.BankRevPath,
                "-p " + ProcessRunner.Quote(pbo), projectRoot, LogLine);
            string prefix = ParsePrefix(properties.Output);
            if (string.IsNullOrWhiteSpace(prefix)) prefix = pboName;

            if (properties.Output.IndexOf("= obfuscated", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string warning = pboName + ": PBO protegido/ofuscado. O PBO e o BISIGN foram copiados, mas o source não pode ser extraído de forma utilizável.";
                _steamImportWarnings.Add(warning);
                Log(warning, Color.Gold);
                return;
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "DayZModWorkbench", "SteamImport_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(_settings.BankRevPath,
                    "-f " + ProcessRunner.Quote(tempRoot) + " -t " + ProcessRunner.Quote(pbo), projectRoot, LogLine);
                if (result.ExitCode != 0) throw new InvalidOperationException("BankRev terminou com código " + result.ExitCode + " ao extrair " + pboName);

                string extractedRoot = FindExtractedRoot(tempRoot, prefix);
                if (extractedRoot == null) throw new InvalidOperationException("A extração de " + pboName + " ficou vazia.");
                string sourceRoot = Path.Combine(projectRoot, "source");
                string destination = Path.Combine(sourceRoot, pboName);
                if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                {
                    string sourceBackup = Path.Combine(projectRoot, "source_backups", pboName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(Path.GetDirectoryName(sourceBackup));
                    Directory.Move(destination, sourceBackup);
                    Log("Source anterior preservado em: " + sourceBackup, Color.Gold);
                }
                Directory.CreateDirectory(destination);
                CopyDirectoryContents(extractedRoot, destination);
                SaveProjectPrefix(projectRoot, pboName, prefix);
                Log("Extraído: " + pboName + " → " + destination + " (prefix=" + prefix + ")", _success);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static string FindExtractedRoot(string tempRoot, string prefix)
        {
            string first = FirstPrefixFolder(prefix);
            if (!string.IsNullOrWhiteSpace(first))
            {
                string expected = Path.Combine(tempRoot, first);
                if (Directory.Exists(expected)) return expected;
            }
            string directory = Directory.GetDirectories(tempRoot).FirstOrDefault();
            if (directory != null) return directory;
            return Directory.GetFiles(tempRoot).Length > 0 ? tempRoot : null;
        }

        private static void CopyDirectoryContents(string source, string destination)
        {
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, RelativePath(source, directory)));
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, RelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static string MakeProjectFolderName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "ImportedMod" : value.Trim();
        }

        private Label FieldLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
        }

        private Button MakeButton(string text, Color color)
        {
            Button button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                BackColor = color,
                ForeColor = Color.White,
                Margin = new Padding(4)
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        private void ApplyTheme(Control root)
        {
            foreach (Control control in root.Controls)
            {
                if (control is TabPage || control is GroupBox || control is Panel || control is TableLayoutPanel || control is SplitContainer)
                    control.BackColor = _panel;
                if (control is TextBox || control is ComboBox || control is ListBox)
                {
                    control.BackColor = _field;
                    control.ForeColor = _text;
                }
                control.ForeColor = control.ForeColor == SystemColors.ControlText ? _text : control.ForeColor;
                ApplyTheme(control);
            }
        }

        private void ApplySettingsToFields()
        {
            _benchText.Text = _settings.BenchPath;
            _bankRevText.Text = _settings.BankRevPath;
            _fileBankText.Text = _settings.FileBankPath;
            _signerText.Text = _settings.SignerPath;
            _privateKeyText.Text = _settings.PrivateKeyPath;
            _dayZText.Text = _settings.DayZPath;
            _dayZDiagText.Text = _settings.DayZDiagPath;
            _editorText.Text = _settings.EditorPath;
            _workshopText.Text = _settings.WorkshopPath;
            _editorDependenciesText.Text = _settings.EditorDependencies;
            _serverKeysText.Text = _settings.ServerKeysPath;
            _backupText.Text = _settings.BackupPath;
            _launchArgsText.Text = _settings.ExtraLaunchArgs;
        }

        private void ReadSettingsFromFields()
        {
            _settings.BenchPath = _benchText.Text.Trim();
            _settings.BankRevPath = _bankRevText.Text.Trim();
            _settings.FileBankPath = _fileBankText.Text.Trim();
            _settings.SignerPath = _signerText.Text.Trim();
            _settings.PrivateKeyPath = _privateKeyText.Text.Trim();
            _settings.DayZPath = _dayZText.Text.Trim();
            _settings.DayZDiagPath = _dayZDiagText.Text.Trim();
            _settings.EditorPath = _editorText.Text.Trim();
            _settings.WorkshopPath = _workshopText.Text.Trim();
            _settings.EditorDependencies = _editorDependenciesText.Text.Trim();
            _settings.ServerKeysPath = _serverKeysText.Text.Trim();
            _settings.BackupPath = _backupText.Text.Trim();
            _settings.ExtraLaunchArgs = _launchArgsText.Text.Trim();
        }

        private void SaveSettings()
        {
            try
            {
                ReadSettingsFromFields();
                _settings.Save();
                _privateKeyText.Text = _settings.PrivateKeyPath;
                RefreshProjects();
                ValidateSettings(true);
                Log("Configurações salvas em " + ToolSettings.SettingsFile, _success);
            }
            catch (Exception ex)
            {
                ShowError("Não foi possível salvar as configurações.", ex);
            }
        }

        private bool ValidateSettings(bool showResult)
        {
            ReadSettingsFromFields();
            List<string> missing = new List<string>();
            CheckDirectory(_settings.BenchPath, "Bancada", missing);
            CheckFile(_settings.BankRevPath, "BankRev", missing);
            CheckFile(_settings.FileBankPath, "FileBank", missing);
            CheckFile(_settings.SignerPath, "DSSignFile", missing);
            CheckFile(_settings.DayZPath, "DayZ", missing);
            CheckFile(_settings.DayZDiagPath, "DayZDiag", missing);
            CheckDirectory(_settings.EditorPath, "DayZ Editor", missing);
            CheckDirectory(_settings.WorkshopPath, "Pasta !Workshop", missing);

            if (showResult)
            {
                if (missing.Count == 0)
                    MessageBox.Show(this, "Todos os caminhos principais estão válidos.", "Validação", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "Caminhos ausentes:\n\n" + string.Join("\n", missing), "Validação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return missing.Count == 0;
        }

        private static void CheckFile(string path, string name, List<string> missing)
        {
            if (!File.Exists(path)) missing.Add(name + ": " + path);
        }

        private static void CheckDirectory(string path, string name, List<string> missing)
        {
            if (!Directory.Exists(path)) missing.Add(name + ": " + path);
        }

        private void RefreshProjects()
        {
            string previous = SelectedProject == null ? null : SelectedProject.FullPath;
            _projectCombo.Items.Clear();
            if (!Directory.Exists(_settings.BenchPath))
            {
                SetStatus("Bancada não encontrada");
                return;
            }

            foreach (string directory in Directory.GetDirectories(_settings.BenchPath).OrderBy(x => Path.GetFileName(x), StringComparer.CurrentCultureIgnoreCase))
            {
                string name = Path.GetFileName(directory);
                if (name.Equals("source_backups", StringComparison.OrdinalIgnoreCase)) continue;
                _projectCombo.Items.Add(new PathItem { Name = name, FullPath = directory });
            }

            int selected = -1;
            for (int i = 0; i < _projectCombo.Items.Count; i++)
            {
                PathItem item = (PathItem)_projectCombo.Items[i];
                if (string.Equals(item.FullPath, previous, StringComparison.OrdinalIgnoreCase)) selected = i;
            }
            if (_projectCombo.Items.Count > 0) _projectCombo.SelectedIndex = selected >= 0 ? selected : 0;
            SetStatus(_projectCombo.Items.Count + " projeto(s) encontrado(s)");
        }

        private PathItem SelectedProject { get { return _projectCombo.SelectedItem as PathItem; } }
        private PathItem SelectedPbo { get { return _pboList.SelectedItem as PathItem; } }
        private PathItem SelectedSource { get { return _sourceCombo.SelectedItem as PathItem; } }

        private void RefreshProjectContents()
        {
            _pboList.Items.Clear();
            _sourceCombo.Items.Clear();
            PathItem project = SelectedProject;
            if (project == null) return;

            IEnumerable<string> pbos = Directory.GetFiles(project.FullPath, "*.pbo", SearchOption.AllDirectories)
                .Where(path => !IsDerivedOrSourcePath(project.FullPath, path))
                .OrderBy(path => RelativePath(project.FullPath, path), StringComparer.CurrentCultureIgnoreCase);
            foreach (string pbo in pbos)
                _pboList.Items.Add(new PathItem { Name = RelativePath(project.FullPath, pbo), FullPath = pbo });
            if (_pboList.Items.Count > 0) _pboList.SelectedIndex = 0;

            string sourceRoot = Path.Combine(project.FullPath, "source");
            if (Directory.Exists(sourceRoot))
            {
                foreach (string source in Directory.GetDirectories(sourceRoot).OrderBy(x => Path.GetFileName(x), StringComparer.CurrentCultureIgnoreCase))
                    _sourceCombo.Items.Add(new PathItem { Name = Path.GetFileName(source), FullPath = source });
            }
            if (_sourceCombo.Items.Count > 0) _sourceCombo.SelectedIndex = 0;
            SetStatus(project.Name + ": " + _pboList.Items.Count + " PBO(s), " + _sourceCombo.Items.Count + " source(s)");
        }

        private static bool IsDerivedOrSourcePath(string projectRoot, string path)
        {
            string relative = RelativePath(projectRoot, path).Replace('/', '\\').ToLowerInvariant();
            return relative.StartsWith("source\\") || relative.StartsWith("_test\\") || relative.StartsWith("source_backups\\");
        }

        private async Task ReadSelectedPboProperties()
        {
            PathItem pbo = SelectedPbo;
            if (pbo == null || !File.Exists(_settings.BankRevPath)) return;
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(_settings.BankRevPath, "-p " + ProcessRunner.Quote(pbo.FullPath), Path.GetDirectoryName(pbo.FullPath), null);
                string prefix = ParsePrefix(result.Output);
                if (!string.IsNullOrWhiteSpace(prefix)) _prefixText.Text = prefix;
            }
            catch { }
        }

        private async Task ExtractSelectedPbo()
        {
            PathItem project = SelectedProject;
            PathItem pbo = SelectedPbo;
            if (project == null || pbo == null)
            {
                MessageBox.Show(this, "Selecione um projeto e um PBO.", "Extração", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(_settings.BankRevPath))
            {
                MessageBox.Show(this, "BankRev.exe não foi encontrado. Confira Configurações.", "Extração", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string sourceRoot = Path.Combine(project.FullPath, "source");

            try
            {
                SetBusy(true, "Lendo propriedades de " + Path.GetFileName(pbo.FullPath));
                ProcessResult properties = await ProcessRunner.RunAsync(_settings.BankRevPath, "-p " + ProcessRunner.Quote(pbo.FullPath), project.FullPath, LogLine);
                string prefix = ParsePrefix(properties.Output);
                string name = FirstPrefixFolder(prefix);
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(pbo.FullPath);
                string destination = Path.Combine(sourceRoot, name);

                SetStatus("Extraindo " + Path.GetFileName(pbo.FullPath));
                if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                {
                    DialogResult answer = MessageBox.Show(this,
                        "O source já existe. Deseja movê-lo para source_backups e extrair novamente?",
                        "Source existente", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes) return;

                    string backupRoot = Path.Combine(project.FullPath, "source_backups");
                    Directory.CreateDirectory(backupRoot);
                    string backup = Path.Combine(backupRoot, name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.Move(destination, backup);
                    Log("Source anterior preservado em: " + backup, Color.Gold);
                }

                if (string.IsNullOrWhiteSpace(prefix)) prefix = name;
                Directory.CreateDirectory(sourceRoot);
                string arguments = "-f " + ProcessRunner.Quote(sourceRoot) + " -t " + ProcessRunner.Quote(pbo.FullPath);
                ProcessResult extract = await ProcessRunner.RunAsync(_settings.BankRevPath, arguments, project.FullPath, LogLine);
                if (extract.ExitCode != 0) throw new InvalidOperationException("BankRev terminou com código " + extract.ExitCode);
                if (!Directory.EnumerateFileSystemEntries(destination).Any()) throw new InvalidOperationException("A pasta source ficou vazia.");

                SaveProjectPrefix(project.FullPath, name, prefix);
                _prefixText.Text = prefix;
                RefreshProjectContents();
                SelectSourceByName(name);
                Log("Extração concluída: " + destination, _success);
                SetStatus("PBO extraído com sucesso");
            }
            catch (Exception ex)
            {
                ShowError("Falha ao extrair o PBO.", ex);
            }
            finally
            {
                SetBusy(false, "Pronto");
            }
        }

        private async Task BuildSelectedSource(bool sign)
        {
            PathItem source = SelectedSource;
            if (source == null)
            {
                MessageBox.Show(this, "Selecione um source.", "Compilação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            await BuildSource(source, sign, true);
        }

        private async Task BuildAllSources()
        {
            List<PathItem> sources = _sourceCombo.Items.Cast<PathItem>().ToList();
            if (sources.Count == 0)
            {
                MessageBox.Show(this, "O projeto não possui pastas em source.", "Compilação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                SetBusy(true, "Compilando todos os sources");
                foreach (PathItem source in sources)
                {
                    bool ok = await BuildSource(source, true, false);
                    if (!ok) return;
                }
                RefreshProjectContents();
                MessageBox.Show(this, "Todos os sources foram compilados e assinados.", "Compilação", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                SetBusy(false, "Pronto");
            }
        }

        private async Task<bool> BuildSource(PathItem source, bool sign, bool manageBusy)
        {
            PathItem project = SelectedProject;
            if (project == null) return false;
            string prefix = _prefixText.Text.Trim();
            if (source != SelectedSource) prefix = LoadProjectPrefix(project.FullPath, source.Name);
            if (string.IsNullOrWhiteSpace(prefix)) prefix = source.Name;
            prefix = prefix.TrimEnd('\\', '/');

            if (!File.Exists(_settings.FileBankPath))
            {
                MessageBox.Show(this, "FileBank.exe não foi encontrado.", "Compilação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (sign && !File.Exists(_privateKeyText.Text.Trim()))
            {
                MessageBox.Show(this, "A chave privada selecionada não existe.", "Assinatura", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "DayZModWorkbench", Guid.NewGuid().ToString("N"));
            string tempOut = Path.Combine(tempRoot, "out");
            Directory.CreateDirectory(tempOut);
            try
            {
                if (manageBusy) SetBusy(true, "Compilando " + source.Name);
                Log("Compilando source: " + source.FullPath, _accent);
                string arguments = "-property " + ProcessRunner.Quote("prefix=" + prefix) + " -dst " + ProcessRunner.Quote(tempOut) + " " + ProcessRunner.Quote(source.FullPath);
                ProcessResult result = await ProcessRunner.RunAsync(_settings.FileBankPath, arguments, project.FullPath, LogLine);
                if (result.ExitCode != 0) throw new InvalidOperationException("FileBank terminou com código " + result.ExitCode);

                string expected = Path.Combine(tempOut, source.Name + ".pbo");
                string builtPbo = File.Exists(expected) ? expected : Directory.GetFiles(tempOut, "*.pbo").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (builtPbo == null) throw new InvalidOperationException("O FileBank não criou um PBO.");
                FileInfo info = new FileInfo(builtPbo);
                if (info.Length < 128) throw new InvalidOperationException("O PBO criado parece vazio (" + info.Length + " bytes).");

                ProcessResult list = await ProcessRunner.RunAsync(_settings.BankRevPath, "-l " + ProcessRunner.Quote(builtPbo), tempOut, LogLine);
                if (list.ExitCode != 0 || string.IsNullOrWhiteSpace(list.Output)) throw new InvalidOperationException("Não foi possível validar o conteúdo do PBO criado.");

                string signature = null;
                if (sign)
                {
                    ProcessResult signed = await ProcessRunner.RunAsync(_settings.SignerPath,
                        ProcessRunner.Quote(_privateKeyText.Text.Trim()) + " " + ProcessRunner.Quote(builtPbo), tempOut, LogLine);
                    if (signed.ExitCode != 0) throw new InvalidOperationException("DSSignFile terminou com código " + signed.ExitCode);
                    signature = Directory.GetFiles(tempOut, Path.GetFileName(builtPbo) + ".*.bisign").FirstOrDefault();
                    if (signature == null) throw new InvalidOperationException("A assinatura BISIGN não foi criada.");
                }

                string outputFolder = Path.Combine(project.FullPath, "PBO");
                Directory.CreateDirectory(outputFolder);
                BackupExistingBuild(project.Name, outputFolder, Path.GetFileName(builtPbo));
                string destinationPbo = Path.Combine(outputFolder, Path.GetFileName(builtPbo));
                File.Copy(builtPbo, destinationPbo, true);
                if (signature != null) File.Copy(signature, Path.Combine(outputFolder, Path.GetFileName(signature)), true);

                SaveProjectPrefix(project.FullPath, source.Name, prefix);
                Log("PBO pronto: " + destinationPbo + " (" + new FileInfo(destinationPbo).Length + " bytes)", _success);
                if (signature != null) Log("BISIGN pronta: " + Path.GetFileName(signature), _success);
                if (manageBusy)
                {
                    RefreshProjectContents();
                    MessageBox.Show(this, "Build concluído com sucesso.", "Compilação", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return true;
            }
            catch (Exception ex)
            {
                ShowError("Falha ao compilar " + source.Name + ".", ex);
                return false;
            }
            finally
            {
                if (manageBusy) SetBusy(false, "Pronto");
                TryDeleteDirectory(tempRoot);
            }
        }

        private void BackupExistingBuild(string projectName, string outputFolder, string pboName)
        {
            string existing = Path.Combine(outputFolder, pboName);
            string[] signatures = Directory.Exists(outputFolder) ? Directory.GetFiles(outputFolder, pboName + ".*.bisign") : new string[0];
            if (!File.Exists(existing) && signatures.Length == 0) return;

            string backup = Path.Combine(_settings.BackupPath, SafeName(projectName), DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(backup);
            if (File.Exists(existing)) File.Copy(existing, Path.Combine(backup, Path.GetFileName(existing)), true);
            foreach (string signature in signatures) File.Copy(signature, Path.Combine(backup, Path.GetFileName(signature)), true);
            Log("Build anterior preservado em: " + backup, Color.Gold);
        }

        private async Task VerifyProjectSignatures()
        {
            PathItem project = SelectedProject;
            if (project == null) return;
            string pboFolder = Path.Combine(project.FullPath, "PBO");
            string checker = Path.Combine(Path.GetDirectoryName(_settings.SignerPath), "DSCheckSignatures.exe");
            if (!Directory.Exists(pboFolder) || !File.Exists(checker) || !Directory.Exists(_settings.ServerKeysPath))
            {
                MessageBox.Show(this, "Confira a pasta PBO, DSCheckSignatures e a pasta de chaves públicas.", "Verificação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                SetBusy(true, "Verificando assinaturas");
                ProcessResult result = await ProcessRunner.RunAsync(checker,
                    ProcessRunner.Quote(pboFolder) + " " + ProcessRunner.Quote(_settings.ServerKeysPath), project.FullPath, LogLine);
                if (result.ExitCode != 0) throw new InvalidOperationException("DSCheckSignatures terminou com código " + result.ExitCode);
                MessageBox.Show(this, "Verificação concluída. Consulte o registro abaixo.", "Assinaturas", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowError("Falha ao verificar assinaturas.", ex);
            }
            finally
            {
                SetBusy(false, "Pronto");
            }
        }

        private string PrepareTestMod(bool showMessage)
        {
            PathItem project = SelectedProject;
            return PrepareTestModForProject(project, showMessage);
        }

        private string PrepareTestModForProject(PathItem project, bool showMessage)
        {
            if (project == null) return null;
            string pboFolder = Path.Combine(project.FullPath, "PBO");
            if (!Directory.Exists(pboFolder) || Directory.GetFiles(pboFolder, "*.pbo").Length == 0)
            {
                MessageBox.Show(this, "Compile ao menos um PBO antes de testar.", "Teste", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            string modRoot = Path.Combine(project.FullPath, "_test", "@" + SafeName(project.Name.TrimStart('@')));
            string addons = Path.Combine(modRoot, "Addons");
            Directory.CreateDirectory(addons);
            foreach (string oldPbo in Directory.GetFiles(addons, "*.pbo")) File.Delete(oldPbo);
            foreach (string oldSign in Directory.GetFiles(addons, "*.bisign")) File.Delete(oldSign);
            foreach (string pbo in Directory.GetFiles(pboFolder, "*.pbo")) File.Copy(pbo, Path.Combine(addons, Path.GetFileName(pbo)), true);
            foreach (string sign in Directory.GetFiles(pboFolder, "*.bisign")) File.Copy(sign, Path.Combine(addons, Path.GetFileName(sign)), true);

            CopyIfExists(Path.Combine(project.FullPath, "mod.cpp"), Path.Combine(modRoot, "mod.cpp"));
            CopyIfExists(Path.Combine(project.FullPath, "meta.cpp"), Path.Combine(modRoot, "meta.cpp"));
            if (showMessage) MessageBox.Show(this, "Mod de teste preparado em:\n" + modRoot, "Teste", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Log("Mod de teste preparado: " + modRoot, _success);
            return modRoot;
        }

        private void LaunchWithModSelection()
        {
            try
            {
                DiagnosticLog("Clique em Abrir com DayZ Editor recebido.");
                if (!Directory.Exists(_settings.WorkshopPath))
                    throw new DirectoryNotFoundException("Pasta !Workshop não encontrada: " + _settings.WorkshopPath);

                List<ModChoice> steamMods = Directory.GetDirectories(_settings.WorkshopPath)
                    .Where(path => !Path.GetFileName(path).StartsWith("!", StringComparison.OrdinalIgnoreCase))
                    .Select(ModChoice.FromSteamPath)
                    .ToList();
                List<ModChoice> benchMods = Directory.GetDirectories(_settings.BenchPath)
                    .Select(path => new ModChoice { Name = Path.GetFileName(path), FullPath = path })
                    .ToList();

                List<string> defaultSteam = new List<string>();
                AddModPaths(defaultSteam, _settings.EditorDependencies);
                defaultSteam.Add(_settings.EditorPath);
                List<string> defaultBench = SelectedProject == null ? new List<string>() : new List<string> { SelectedProject.FullPath };
                DiagnosticLog("Abrindo seletor: " + steamMods.Count + " mods Steam e " + benchMods.Count + " projetos da bancada.");

                using (ModSelectionForm dialog = new ModSelectionForm(steamMods, benchMods, defaultSteam, defaultBench))
                {
                    dialog.TopMost = true;
                    dialog.Shown += delegate { dialog.Activate(); dialog.BringToFront(); };
                    DialogResult selected = dialog.ShowDialog(this);
                    DiagnosticLog("Seletor fechado com resultado: " + selected);
                    if (selected != DialogResult.OK) return;

                    List<string> mods = new List<string>();
                    // Dependências principais do Editor são sempre carregadas.
                    AddModPaths(mods, _settings.EditorDependencies);
                    mods.Add(_settings.EditorPath);
                    mods.AddRange(dialog.SelectedSteamPaths);
                    List<string> unavailable = new List<string>();
                    foreach (string projectPath in dialog.SelectedBenchPaths)
                    {
                        string resolved = ResolveBenchMod(projectPath);
                        if (resolved == null) unavailable.Add(Path.GetFileName(projectPath));
                        else mods.Add(resolved);
                    }

                    if (unavailable.Count > 0)
                    {
                        MessageBox.Show(this,
                            "Estes projetos não possuem Addons ou PBO compilado e não serão carregados:\n\n" + string.Join("\n", unavailable),
                            "Projetos sem build", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }

                    List<string> finalMods = mods.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    string selectionSummary = string.Join("\n", finalMods.Select(path => "• " + Path.GetFileName(path)).ToArray());
                    DialogResult confirm = MessageBox.Show(this,
                        "Estes mods serão carregados com o DayZ Editor:\n\n" + selectionSummary + "\n\nTotal: " + finalMods.Count,
                        "Confirmar mods do Editor", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                    if (confirm != DialogResult.OK)
                    {
                        DiagnosticLog("Lançamento cancelado na confirmação dos mods.");
                        return;
                    }

                    DiagnosticLog("Mods confirmados: " + string.Join(";", finalMods));
                    StartDayZ(_settings.DayZPath, finalMods, true);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog("Erro ao abrir seletor/editor: " + ex);
                ShowError("Não foi possível preparar o DayZ Editor.", ex);
            }
        }

        private static void DiagnosticLog(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workbench.log"),
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine);
            }
            catch { }
        }

        private string ResolveBenchMod(string projectPath)
        {
            if (!Directory.Exists(projectPath)) return null;

            string addons = Path.Combine(projectPath, "Addons");
            if (Directory.Exists(addons) && Directory.GetFiles(addons, "*.pbo").Length > 0)
                return projectPath;

            string pboFolder = Path.Combine(projectPath, "PBO");
            if (Directory.Exists(pboFolder) && Directory.GetFiles(pboFolder, "*.pbo").Length > 0)
                return PrepareTestModForProject(new PathItem { Name = Path.GetFileName(projectPath), FullPath = projectPath }, false);

            foreach (string child in Directory.GetDirectories(projectPath))
            {
                string childAddons = Path.Combine(child, "Addons");
                if (Directory.Exists(childAddons) && Directory.GetFiles(childAddons, "*.pbo").Length > 0)
                    return child;
            }
            return null;
        }

        private void Launch(bool withEditor, bool diagnostic)
        {
            try
            {
                string testMod = PrepareTestMod(false);
                if (testMod == null) return;
                string executable = diagnostic ? _settings.DayZDiagPath : _settings.DayZPath;
                if (!File.Exists(executable)) throw new FileNotFoundException("Executável do DayZ não encontrado.", executable);

                List<string> mods = new List<string>();
                if (withEditor)
                {
                    AddModPaths(mods, _settings.EditorDependencies);
                    if (Directory.Exists(_settings.EditorPath)) mods.Add(_settings.EditorPath);
                    else throw new DirectoryNotFoundException("Pasta do DayZ Editor não encontrada: " + _settings.EditorPath);
                }
                mods.Add(testMod);

                StartDayZ(executable, mods, withEditor);
            }
            catch (Exception ex)
            {
                ShowError("Não foi possível iniciar o DayZ.", ex);
            }
        }

        private void StartDayZ(string executable, IEnumerable<string> mods, bool withEditor)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("Executável do DayZ não encontrado.", executable);
            if (!EnsureNoRunningDayZ())
            {
                DiagnosticLog("Lançamento cancelado porque já havia uma instância do DayZ aberta.");
                return;
            }

            string profilesRoot = SelectedProject != null ? SelectedProject.FullPath : _settings.BenchPath;
            string profiles = Path.Combine(profilesRoot, "profiles");
            Directory.CreateDirectory(profiles);

            StringBuilder args = new StringBuilder(_settings.ExtraLaunchArgs ?? string.Empty);
            // DayZ requires the complete option to be quoted when its value has
            // spaces: "-profiles=C:\\path with spaces", not -profiles="...".
            args.Append(" ").Append(ProcessRunner.Quote("-profiles=" + profiles));
            args.Append(" ").Append(ProcessRunner.Quote("-mod=" + string.Join(";", mods.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))));
            if (_loadingTestCheck.Checked && !withEditor) args.Append(" -loadingTest");
            if (_filePatchingCheck.Checked || withEditor) args.Append(" -filePatching");

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args.ToString(),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = true
            };
            Process process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("O Windows não retornou o processo iniciado.");
            DiagnosticLog("DayZ iniciado. PID=" + process.Id + " comando=" + executable + " " + args);
            Log("DayZ iniciado (PID " + process.Id + "): " + executable + " " + args, _accent);
            SetStatus(withEditor ? "DayZ Editor iniciado" : "DayZ iniciado para teste");
            MonitorStartedDayZ(process, profiles, withEditor, mods.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        private bool EnsureNoRunningDayZ()
        {
            List<Process> running = Process.GetProcesses()
                .Where(p => p.ProcessName.Equals("DayZDiag_x64", StringComparison.OrdinalIgnoreCase)
                    || p.ProcessName.Equals("DayZ_x64", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (running.Count == 0) return true;

            DialogResult answer = MessageBox.Show(this,
                "Existem " + running.Count + " instância(s) do DayZ abertas. Isso pode fazer o novo teste iniciar invisível.\n\nDeseja encerrar essas instâncias e continuar?",
                "DayZ já está aberto", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return false;

            foreach (Process process in running)
            {
                try
                {
                    DiagnosticLog("Encerrando instância antiga: " + process.ProcessName + " PID=" + process.Id);
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    DiagnosticLog("Falha ao encerrar PID " + process.Id + ": " + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }
            return !Process.GetProcessesByName("DayZDiag_x64").Any() && !Process.GetProcessesByName("DayZ_x64").Any();
        }

        private void MonitorStartedDayZ(Process process, string profiles, bool withEditor, List<string> requestedMods)
        {
            int pid = process.Id;
            DateTime started = DateTime.Now;
            Task.Run(delegate
            {
                System.Threading.Thread.Sleep(6000);
                bool exited;
                try { exited = process.HasExited; }
                catch { exited = true; }
                string rpt = Directory.Exists(profiles)
                    ? Directory.GetFiles(profiles, "*.RPT", SearchOption.AllDirectories)
                        .Where(path => File.GetLastWriteTime(path) >= started.AddSeconds(-2))
                        .OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                    : null;
                string result = "Verificação PID=" + pid + ": " + (exited ? "processo encerrou cedo" : "processo ativo")
                    + ", RPT=" + (rpt ?? "não criado");
                DiagnosticLog(result);
                Log(result, exited || rpt == null ? Color.Gold : _success);
                List<string> loaded = new List<string>();
                List<string> missing = new List<string>();
                if (rpt != null)
                {
                    string rptText = ReadFileShared(rpt);
                    foreach (string modPath in requestedMods)
                    {
                        string name = Path.GetFileName(modPath);
                        if (rptText.IndexOf(modPath, StringComparison.OrdinalIgnoreCase) >= 0)
                            loaded.Add(name);
                        else
                            missing.Add(name);
                    }
                    DiagnosticLog("Mods confirmados no RPT: " + string.Join(", ", loaded));
                    if (missing.Count > 0) DiagnosticLog("Mods ausentes no RPT: " + string.Join(", ", missing));
                    Log("Carregados pelo DayZ: " + string.Join(", ", loaded), loaded.Count > 0 ? _success : Color.Gold);
                    if (missing.Count > 0) Log("Não confirmados no RPT: " + string.Join(", ", missing), Color.Salmon);
                }
                if (!exited && rpt == null)
                    SetStatus("DayZ iniciou, mas ainda não criou RPT");
                else if (!exited)
                    SetStatus(withEditor ? "DayZ Editor ativo e RPT criado" : "DayZ ativo e RPT criado");

                if (withEditor && rpt != null)
                {
                    BeginInvoke(new Action(delegate
                    {
                        string message = "Mods confirmados no RPT do DayZ:\n\n" +
                            (loaded.Count > 0 ? string.Join("\n", loaded.Select(x => "✓ " + x).ToArray()) : "Nenhum") +
                            (missing.Count > 0 ? "\n\nNão confirmados:\n" + string.Join("\n", missing.Select(x => "✗ " + x).ToArray()) : string.Empty);
                        MessageBox.Show(this, message, "Validação dos mods carregados",
                            MessageBoxButtons.OK, missing.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    }));
                }
            });
        }

        private static string ReadFileShared(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream))
                    return reader.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void AddModPaths(List<string> target, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (string item in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string path = item.Trim().Trim('"');
                if (path.Length > 0) target.Add(path);
            }
        }

        private void LoadSourcePrefix()
        {
            PathItem project = SelectedProject;
            PathItem source = SelectedSource;
            if (project == null || source == null) return;
            string prefix = LoadProjectPrefix(project.FullPath, source.Name);
            _prefixText.Text = string.IsNullOrWhiteSpace(prefix) ? source.Name : prefix;
        }

        private static string ProjectConfigPath(string projectRoot)
        {
            return Path.Combine(projectRoot, ".dayzworkbench.ini");
        }

        private static Dictionary<string, string> LoadProjectConfiguration(string projectRoot)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = ProjectConfigPath(projectRoot);
            if (!File.Exists(path)) return values;
            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                values[line.Substring(0, separator)] = line.Substring(separator + 1);
            }
            return values;
        }

        private static string LoadProjectPrefix(string projectRoot, string sourceName)
        {
            string value;
            Dictionary<string, string> values = LoadProjectConfiguration(projectRoot);
            return values.TryGetValue("Prefix." + sourceName, out value) ? value : sourceName;
        }

        private static void SaveProjectPrefix(string projectRoot, string sourceName, string prefix)
        {
            Dictionary<string, string> values = LoadProjectConfiguration(projectRoot);
            values["Prefix." + sourceName] = prefix.TrimEnd('\\', '/');
            File.WriteAllLines(ProjectConfigPath(projectRoot), values.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value).ToArray());
        }

        private static string ParsePrefix(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            foreach (string line in output.Replace("\r", string.Empty).Split('\n'))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                if (!line.Substring(0, separator).Trim().Equals("prefix", StringComparison.OrdinalIgnoreCase)) continue;
                return line.Substring(separator + 1).Trim().TrimEnd('\\', '/');
            }
            return null;
        }

        private static string FirstPrefixFolder(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return null;
            string normalized = prefix.Trim().Trim('\\', '/');
            int slash = normalized.IndexOfAny(new[] { '\\', '/' });
            return slash > 0 ? normalized.Substring(0, slash) : normalized;
        }

        private void SelectSourceByName(string name)
        {
            for (int i = 0; i < _sourceCombo.Items.Count; i++)
            {
                PathItem item = (PathItem)_sourceCombo.Items[i];
                if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    _sourceCombo.SelectedIndex = i;
                    return;
                }
            }
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            foreach (Control control in _operationControls) control.Enabled = !busy;
            UseWaitCursor = busy;
            SetStatus(message);
        }

        private void LogLine(string text)
        {
            Log(text, Color.FromArgb(185, 205, 215));
        }

        private void Log(string text, Color color)
        {
            if (_log.InvokeRequired)
            {
                _log.BeginInvoke(new Action<string, Color>(Log), text, color);
                return;
            }
            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = color;
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
            _log.SelectionColor = _log.ForeColor;
            _log.ScrollToCaret();
        }

        private void SetStatus(string text)
        {
            if (_status == null) return;
            if (_status.GetCurrentParent() != null && _status.GetCurrentParent().InvokeRequired)
            {
                _status.GetCurrentParent().BeginInvoke(new Action<string>(SetStatus), text);
                return;
            }
            _status.Text = text;
        }

        private void ShowError(string message, Exception ex)
        {
            Log(message + " " + ex.Message, Color.Salmon);
            MessageBox.Show(this, message + "\n\n" + ex.Message, "DayZ Mod Workbench", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void OpenSelectedProject()
        {
            if (SelectedProject != null) OpenFolder(SelectedProject.FullPath);
        }

        private void OpenSelectedSource()
        {
            if (SelectedSource != null) OpenFolder(SelectedSource.FullPath);
        }

        private void OpenTestFolder()
        {
            if (SelectedProject != null) OpenFolder(Path.Combine(SelectedProject.FullPath, "_test"));
        }

        private static void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", ProcessRunner.Quote(path)) { UseShellExecute = true });
        }

        private void BrowseFolder(TextBox target)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.SelectedPath = Directory.Exists(target.Text) ? target.Text : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
            }
        }

        private void BrowseFile(TextBox target, string filter)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = filter;
                if (File.Exists(target.Text))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(target.Text);
                    dialog.FileName = Path.GetFileName(target.Text);
                }
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
            }
        }

        private static void CopyIfExists(string source, string destination)
        {
            if (File.Exists(source)) File.Copy(source, destination, true);
        }

        private static string SafeName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value.Replace(' ', '_');
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { }
        }

        private static string RelativePath(string root, string path)
        {
            Uri rootUri = new Uri(AppendSeparator(Path.GetFullPath(root)));
            Uri pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString()) ? path : path + Path.DirectorySeparatorChar;
        }
    }
}
