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
        private readonly List<string> _steamImportVerifications = new List<string>();
        private bool _updatingSteamImportList;

        private TextBox _benchText;
        private TextBox _bankRevText;
        private TextBox _cfgConvertText;
        private TextBox _pythonText;
        private TextBox _odolConverterText;
        private TextBox _addonBuilderText;
        private TextBox _projectDriveText;
        private TextBox _fileBankText;
        private TextBox _signerText;
        private TextBox _dayZText;
        private TextBox _dayZDiagText;
        private TextBox _editorText;
        private TextBox _workshopText;
        private TextBox _editorDependenciesText;
        private TextBox _serverKeysText;
        private TextBox _launchArgsText;
        private Button _workDriveButton;
        private string _privateKeyStoreNotice;
        private SplitContainer _projectSplit;

        private const int DefaultPboPanelWidth = 360;

        private readonly List<Control> _operationControls = new List<Control>();
        private bool _busy;

        public MainForm()
        {
            _settings = ToolSettings.Load();
            try
            {
                string previousKey = _settings.PrivateKeyPath;
                _settings.PrivateKeyPath = PrivateKeyStore.EnsureLocal(previousKey);
                if (!string.IsNullOrWhiteSpace(_settings.PrivateKeyPath) &&
                    !_settings.PrivateKeyPath.Equals(previousKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Save();
                    _privateKeyStoreNotice = "Chave privada disponibilizada na pasta local do Workbench: " + _settings.PrivateKeyPath;
                }
            }
            catch (Exception ex)
            {
                _settings.PrivateKeyPath = string.Empty;
                _privateKeyStoreNotice = "Não foi possível preparar a pasta local de chaves: " + ex.Message;
            }
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
            if (!string.IsNullOrWhiteSpace(_privateKeyStoreNotice))
                Log(_privateKeyStoreNotice, string.IsNullOrWhiteSpace(_settings.PrivateKeyPath) ? Color.Gold : _success);
            RefreshProjects();
            LoadSteamImportMods();
            RefreshWorkDriveButton();
            Activated += delegate { RefreshWorkDriveButton(); };
            _tabs.SelectedIndexChanged += delegate { RefreshWorkDriveButton(); };
            Shown += delegate { ApplyDefaultProjectSplit(); };
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
            _projectSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1
            };
            SplitContainer split = _projectSplit;
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
            TableLayoutPanel build = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 9 };
            build.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            build.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            build.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            for (int i = 0; i < 7; i++) build.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
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

            build.Controls.Add(FieldLabel("Chave privada (keys):"), 0, 2);
            _privateKeyText = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
            build.Controls.Add(_privateKeyText, 1, 2);
            Button keyBrowse = MakeButton("Adicionar", _field);
            keyBrowse.Click += delegate { ImportPrivateKey(); };
            build.Controls.Add(keyBrowse, 2, 2);

            build.Controls.Add(FieldLabel("Saída:"), 0, 3);
            Label output = new Label
            {
                Text = "Projeto\\PBO  (chave permanece em Workbench\\keys)",
                Dock = DockStyle.Fill,
                ForeColor = _success,
                TextAlign = ContentAlignment.MiddleLeft
            };
            build.Controls.Add(output, 1, 3);
            build.SetColumnSpan(output, 2);

            Button prepareSource = MakeButton("Desbinarizar source selecionado", Color.FromArgb(155, 90, 230));
            prepareSource.Click += async delegate { await PrepareSelectedSource(); };
            build.Controls.Add(prepareSource, 1, 4);
            build.SetColumnSpan(prepareSource, 2);
            _operationControls.Add(prepareSource);

            Button buildOnly = MakeButton("Compilar PBO", _field);
            buildOnly.Click += async delegate { await BuildSelectedSource(false); };
            build.Controls.Add(buildOnly, 1, 5);
            Button buildSign = MakeButton("PBO + BISIGN", _success);
            buildSign.Click += async delegate { await BuildSelectedSource(true); };
            build.Controls.Add(buildSign, 2, 5);
            _operationControls.Add(buildOnly);
            _operationControls.Add(buildSign);

            Button buildAll = MakeButton("Compilar e assinar todos os sources", _accent);
            buildAll.Click += async delegate { await BuildAllSources(); };
            build.Controls.Add(buildAll, 1, 6);
            build.SetColumnSpan(buildAll, 2);
            _operationControls.Add(buildAll);

            Label note = new Label
            {
                Text = "Após extrair, o Workbench converte arquivos RaP e reconstrói ODOL53/54/55 como MLOD, recuperando model.cfg animado. Se a validação falhar, preserva o ODOL original.",
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver,
                AutoSize = false
            };
            build.Controls.Add(note, 0, 7);
            build.SetColumnSpan(note, 3);

            Button verify = MakeButton("Verificar assinaturas da pasta PBO", _field);
            verify.Click += async delegate { await VerifyProjectSignatures(); };
            build.Controls.Add(verify, 1, 8);
            build.SetColumnSpan(verify, 2);
            _operationControls.Add(verify);

            _workDriveButton = MakeButton("Montar P:", Color.FromArgb(155, 90, 230));
            _workDriveButton.Click += async delegate { await ToggleWorkDrive(); };
            build.Controls.Add(_workDriveButton, 0, 8);
            _operationControls.Add(_workDriveButton);

            buildGroup.Controls.Add(build);
            split.Panel2.Controls.Add(buildGroup);
            return page;
        }

        private void ApplyDefaultProjectSplit()
        {
            if (_projectSplit == null || _projectSplit.ClientSize.Width <= 0) return;
            _projectSplit.Panel1MinSize = 300;
            _projectSplit.Panel2MinSize = 600;
            int maximum = _projectSplit.ClientSize.Width - _projectSplit.SplitterWidth - _projectSplit.Panel2MinSize;
            int desired = Math.Min(DefaultPboPanelWidth, maximum);
            if (desired >= _projectSplit.Panel1MinSize)
                _projectSplit.SplitterDistance = desired;
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
                RowCount = 17
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            for (int i = 0; i < 16; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            int row = 0;
            _benchText = AddPathRow(grid, row++, "Bancada de mods:", true, false);
            _bankRevText = AddPathRow(grid, row++, "BankRev.exe:", false, false);
            _cfgConvertText = AddPathRow(grid, row++, "CfgConvert.exe:", false, false);
            _pythonText = AddPathRow(grid, row++, "Python.exe:", false, false);
            int odolAddonRow = row++;
            _odolConverterText = AddPathRow(grid, odolAddonRow, "Addon ODOL.py:", false, true);
            _odolConverterText.ReadOnly = true;
            Label odolAddonHint = grid.GetControlFromPosition(2, odolAddonRow) as Label;
            if (odolAddonHint != null) odolAddonHint.Text = "gerenciado";
            _addonBuilderText = AddPathRow(grid, row++, "AddonBuilder.exe:", false, false);
            _projectDriveText = AddPathRow(grid, row++, "Work drive / projeto:", true, false);
            _fileBankText = AddPathRow(grid, row++, "FileBank.exe:", false, false);
            _signerText = AddPathRow(grid, row++, "DSSignFile.exe:", false, false);
            _dayZText = AddPathRow(grid, row++, "DayZ_x64.exe:", false, false);
            _dayZDiagText = AddPathRow(grid, row++, "DayZDiag_x64.exe:", false, false);
            _editorText = AddPathRow(grid, row++, "Pasta @DayZ-Editor:", true, false);
            _workshopText = AddPathRow(grid, row++, "Pasta !Workshop:", true, false);
            _editorDependenciesText = AddPathRow(grid, row++, "Dependências Editor:", false, true);
            _serverKeysText = AddPathRow(grid, row++, "Chaves públicas servidor:", true, false);

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
            Button autoDetect = MakeButton("Auto detectar", Color.FromArgb(155, 90, 230));
            autoDetect.Click += delegate { AutoDetectSettings(); };
            grid.Controls.Add(autoDetect, 0, row);
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
            _steamImportMods.AddRange(ModChoice.DiscoverSteamMods(_settings.WorkshopPath));
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
                _steamImportVerifications.Clear();
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
                    : "\n\nPendências para uma source totalmente editável:\n" +
                        string.Join("\n", _steamImportWarnings.Select(item => "• " + item).ToArray());
                string verifications = _steamImportVerifications.Count == 0
                    ? string.Empty
                    : "\n\nVerificações de integridade:\n" +
                        string.Join("\n", _steamImportVerifications.Select(item => "✓ " + item).ToArray());
                MessageBox.Show(this,
                    "Importação concluída. Os arquivos dos PBOs foram preservados; as pendências abaixo " +
                    "indicam apenas formatos que ainda não ficaram editáveis.\n\n" +
                    string.Join("\n", importedProjects) + verifications + warnings,
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

            Log("Importando " + mod.Name + " (" + pbos.Length + " PBOs)", _accent);
            foreach (string pbo in pbos)
            {
                string destinationPbo = Path.Combine(pboOutput, Path.GetFileName(pbo));
                foreach (string oldSign in Directory.GetFiles(pboOutput, Path.GetFileName(pbo) + ".*.bisign"))
                    File.Delete(oldSign);
                File.Copy(pbo, destinationPbo, true);
                foreach (string signature in Directory.GetFiles(addons, Path.GetFileName(pbo) + ".*.bisign"))
                    File.Copy(signature, Path.Combine(pboOutput, Path.GetFileName(signature)), true);

                if (extract) await ExtractImportedPbo(projectRoot, destinationPbo);
            }

            CopyIfExists(Path.Combine(mod.FullPath, "mod.cpp"), Path.Combine(projectRoot, "mod.cpp"));
            CopyIfExists(Path.Combine(mod.FullPath, "meta.cpp"), Path.Combine(projectRoot, "meta.cpp"));
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
                _steamImportVerifications.Clear();
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

        internal async Task<bool> RunAutomatedSourcePreparation(string sourcePath)
        {
            try
            {
                if (!Directory.Exists(sourcePath)) throw new DirectoryNotFoundException("Source não encontrado: " + sourcePath);
                DirectoryInfo sourceParent = Directory.GetParent(sourcePath);
                if (sourceParent == null || !sourceParent.Name.Equals("source", StringComparison.OrdinalIgnoreCase) || sourceParent.Parent == null)
                    throw new InvalidOperationException("O caminho de teste deve ser uma pasta Projeto\\source\\NomeDoPBO.");

                SetBusy(true, "Teste interno: desbinarizando " + Path.GetFileName(sourcePath));
                SourcePreparationResult preparation = await SourcePreparer.PrepareAsync(sourcePath,
                    _settings.CfgConvertPath, LogLine);
                string converterPath = preparation.OdolPreserved > 0
                    ? await EnsureOdolConverterAvailable()
                    : _settings.OdolConverterPath;
                OdolReconstructionResult reconstruction = await OdolSourceReconstructor.ReconstructAsync(sourcePath,
                    _settings.PythonPath, converterPath, LogLine);
                if (reconstruction.Changed)
                    ReplaceDirectoryByMove(reconstruction.OutputRoot, sourcePath);
                List<string> warnings = new List<string>(preparation.Warnings);
                warnings.AddRange(reconstruction.Warnings);
                string details = preparation.Summary + " " + reconstruction.Summary +
                    (warnings.Count == 0 ? string.Empty : " | " + string.Join(" | ", warnings.ToArray()));
                Log("TESTE INTERNO SOURCE: " + details, warnings.Count == 0 ? _success : Color.Gold);
                DiagnosticLog("Teste de preparação de source: " + sourcePath + " | " + details);
                return warnings.Count == 0;
            }
            catch (Exception ex)
            {
                Log("TESTE INTERNO SOURCE FALHOU: " + ex, Color.Salmon);
                DiagnosticLog("Teste de preparação de source falhou: " + ex);
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
            string tempRoot = Path.Combine(Path.GetTempPath(), "DayZModWorkbench", "SteamImport_" + Guid.NewGuid().ToString("N"));

            if (IsProtectedPbo(properties.Output))
            {
                Directory.CreateDirectory(tempRoot);
                try
                {
                    Log(pboName + ": ofuscação detectada; iniciando recuperação pelo addon Python.", _accent);
                    PboSourceRecoveryResult recovery = await RecoverProtectedPbo(pbo, tempRoot);
                    if (!string.IsNullOrWhiteSpace(recovery.Prefix)) prefix = recovery.Prefix;
                    string sourceRoot = Path.Combine(projectRoot, "source");
                    string destination = Path.Combine(sourceRoot, pboName);
                    ReplaceDirectoryByCopy(recovery.SourceRoot, destination);
                    SaveProjectPrefix(projectRoot, pboName, prefix);
                    Log("PBO ofuscado recuperado: " + pboName + " → " + destination + " — " + recovery.Summary, _success);
                    foreach (string item in recovery.Warnings)
                    {
                        string warning = pboName + ": " + item;
                        _steamImportWarnings.Add(warning);
                        Log(warning, Color.Gold);
                    }
                }
                catch (Exception ex)
                {
                    string warning = pboName + ": recuperação do PBO ofuscado falhou; original preservado. " + ex.Message;
                    _steamImportWarnings.Add(warning);
                    Log(warning, Color.Gold);
                }
                finally
                {
                    TryDeleteDirectory(tempRoot);
                }
                return;
            }

            if (!File.Exists(_settings.CfgConvertPath)) throw new FileNotFoundException("CfgConvert.exe não encontrado.", _settings.CfgConvertPath);
            Directory.CreateDirectory(tempRoot);
            try
            {
                ProcessResult result = await ProcessRunner.RunAsync(_settings.BankRevPath,
                    "-f " + ProcessRunner.Quote(tempRoot) + " -t " + ProcessRunner.Quote(pbo), projectRoot, LogLine);
                if (result.ExitCode != 0) throw new InvalidOperationException("BankRev terminou com código " + result.ExitCode + " ao extrair " + pboName);

                string extractedRoot = FindExtractedRoot(tempRoot, prefix, pboName);
                if (extractedRoot == null) throw new InvalidOperationException("A extração de " + pboName + " ficou vazia.");
                if (Directory.GetFiles(extractedRoot, "*", SearchOption.AllDirectories).Length == 0)
                {
                    string warning = pboName + ": a extração não produziu arquivos utilizáveis. O PBO pode estar protegido/ofuscado.";
                    _steamImportWarnings.Add(warning);
                    Log(warning, Color.Gold);
                    return;
                }

                if (HasEmptyRootConfigPlaceholder(extractedRoot))
                {
                    try
                    {
                        Log(pboName + ": config.cpp vazio detectado; recuperando scripts/config pelo addon Python v4.", _accent);
                        PboSourceRecoveryResult recovery = await RecoverProtectedPbo(pbo, tempRoot);
                        PboExtractionAuditResult audit = PboExtractionAuditor.Validate(recovery.ManifestPath, extractedRoot);
                        Log(pboName + ": " + audit.Summary + ".", _success);
                        _steamImportVerifications.Add(pboName + ": " + audit.VerifiedFiles + "/" +
                            audit.PayloadFiles + " arquivos conferidos por SHA-1");
                        _steamImportVerifications.Add(pboName + ": v5 " + recovery.VerificationStatus +
                            " — scripts " + recovery.RecoveredScripts + ", P3D " + recovery.VerifiedP3ds + "/" +
                            recovery.P3dCount + ", configs " + recovery.VerifiedConfigs + "/" + recovery.ConfigCount);
                        MergeRecoveredPboSource(recovery.SourceRoot, extractedRoot);
                        if (!string.IsNullOrWhiteSpace(recovery.Prefix)) prefix = recovery.Prefix;
                        Log(pboName + ": recuperação complementar aplicada — " + recovery.Summary, _success);
                        foreach (string item in recovery.Warnings)
                        {
                            string warning = pboName + ": " + item;
                            _steamImportWarnings.Add(warning);
                            Log(warning, Color.Gold);
                        }
                    }
                    catch (Exception ex)
                    {
                        string warning = pboName + ": recuperação complementar pelo Python falhou; continuando com a extração normal. " + ex.Message;
                        _steamImportWarnings.Add(warning);
                        Log(warning, Color.Gold);
                    }
                }

                SourcePreparationResult preparation = await SourcePreparer.PrepareAsync(extractedRoot,
                    _settings.CfgConvertPath, LogLine);
                Log("Source preparada: " + pboName + " — " + preparation.Summary,
                    preparation.IsComplete && preparation.Warnings.Count == 0 ? _success : Color.Gold);
                foreach (string item in preparation.Warnings)
                {
                    string warning = pboName + ": " + item;
                    _steamImportWarnings.Add(warning);
                    Log(warning, Color.Gold);
                }

                string preparedRoot = extractedRoot;
                if (preparation.OdolPreserved > 0)
                {
                    if (!File.Exists(_settings.PythonPath))
                    {
                        string warning = pboName + ": Python não configurado; modelos ODOL foram preservados.";
                        _steamImportWarnings.Add(warning);
                        Log(warning, Color.Gold);
                    }
                    else
                    {
                        try
                        {
                            string converterPath = await EnsureOdolConverterAvailable();
                            OdolReconstructionResult reconstruction = await OdolSourceReconstructor.ReconstructAsync(
                                extractedRoot, _settings.PythonPath, converterPath, LogLine);
                            if (reconstruction.Changed) preparedRoot = reconstruction.OutputRoot;
                            Log("Reconstrução de modelos: " + pboName + " — " + reconstruction.Summary, _success);
                            foreach (string item in reconstruction.Warnings)
                            {
                                string warning = pboName + ": " + item;
                                _steamImportWarnings.Add(warning);
                                Log(warning, Color.Gold);
                            }
                        }
                        catch (Exception ex)
                        {
                            string warning = pboName + ": ODOL não reconstruído; originais preservados. " + ex.Message;
                            _steamImportWarnings.Add(warning);
                            Log(warning, Color.Gold);
                        }
                    }
                }

                string sourceRoot = Path.Combine(projectRoot, "source");
                string destination = Path.Combine(sourceRoot, pboName);
                ReplaceDirectoryByCopy(preparedRoot, destination);
                if (!preparedRoot.Equals(extractedRoot, StringComparison.OrdinalIgnoreCase)) TryDeleteDirectory(preparedRoot);
                SaveProjectPrefix(projectRoot, pboName, prefix);
                Log((preparation.IsComplete ? "Extraído: " : "Extraído com pendências: ") + pboName +
                    " → " + destination + " (prefix=" + prefix + ")",
                    preparation.IsComplete ? _success : Color.Gold);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static bool IsProtectedPbo(string properties)
        {
            if (string.IsNullOrWhiteSpace(properties)) return false;
            return properties.IndexOf("= obfuscated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                properties.IndexOf("MPG Packer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<PboSourceRecoveryResult> RecoverProtectedPbo(string pboPath, string tempRoot)
        {
            if (!File.Exists(_settings.PythonPath))
                throw new FileNotFoundException("Python não configurado; confira a aba Configurações.", _settings.PythonPath);
            if (!File.Exists(_settings.CfgConvertPath))
                throw new FileNotFoundException("CfgConvert.exe não configurado; confira a aba Configurações.", _settings.CfgConvertPath);
            string converterPath = await EnsureOdolConverterAvailable();
            string recoveryRoot = Path.Combine(tempRoot, "PboRecovery");
            PboSourceRecoveryResult recovery = await PboSourceReconstructor.RecoverAsync(pboPath, recoveryRoot,
                _settings.PythonPath, converterPath, _settings.CfgConvertPath, LogLine);
            RemoveVerifiedConfigBins(recovery.SourceRoot);
            SourcePreparationResult preparation = await SourcePreparer.PrepareAsync(recovery.SourceRoot,
                _settings.CfgConvertPath, LogLine);
            recovery.Warnings.AddRange(preparation.Warnings);
            return recovery;
        }

        private static bool HasEmptyRootConfigPlaceholder(string sourceRoot)
        {
            string configBin = Path.Combine(sourceRoot, "config.bin");
            string configCpp = Path.Combine(sourceRoot, "config.cpp");
            return File.Exists(configBin) && new FileInfo(configBin).Length > 0 &&
                File.Exists(configCpp) && new FileInfo(configCpp).Length == 0;
        }

        internal static void MergeRecoveredPboSource(string recoveredRoot, string extractedRoot)
        {
            string recoveredScripts = Path.Combine(recoveredRoot, "scripts");
            if (Directory.Exists(recoveredScripts))
            {
                string extractedScripts = Directory.GetDirectories(extractedRoot, "scripts", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault() ?? Path.Combine(extractedRoot, "scripts");
                TryDeleteDirectory(extractedScripts);
                Directory.CreateDirectory(extractedScripts);
                CopyDirectoryContents(recoveredScripts, extractedScripts);
            }

            foreach (string recoveredCpp in Directory.GetFiles(recoveredRoot, "config.cpp", SearchOption.AllDirectories))
            {
                string relative = RelativePath(recoveredRoot, recoveredCpp);
                string extractedCpp = Path.Combine(extractedRoot, relative);
                string extractedBin = Path.ChangeExtension(extractedCpp, ".bin");
                Directory.CreateDirectory(Path.GetDirectoryName(extractedCpp));
                File.Copy(recoveredCpp, extractedCpp, true);
                if (File.Exists(extractedBin)) File.Delete(extractedBin);
            }

            string recoveredRootCpp = Path.Combine(recoveredRoot, "config.cpp");
            string recoveredRootBin = Path.Combine(recoveredRoot, "config.bin");
            if (!File.Exists(recoveredRootCpp) && File.Exists(recoveredRootBin))
            {
                string extractedCpp = Path.Combine(extractedRoot, "config.cpp");
                string extractedBin = Path.Combine(extractedRoot, "config.bin");
                File.Copy(recoveredRootBin, extractedBin, true);
                if (File.Exists(extractedCpp) && new FileInfo(extractedCpp).Length == 0) File.Delete(extractedCpp);
            }
        }

        private static void RemoveVerifiedConfigBins(string sourceRoot)
        {
            foreach (string configBin in Directory.GetFiles(sourceRoot, "config.bin", SearchOption.AllDirectories))
            {
                string configCpp = Path.Combine(Path.GetDirectoryName(configBin), "config.cpp");
                if (File.Exists(configCpp) && new FileInfo(configCpp).Length > 0) File.Delete(configBin);
            }
        }

        private static string FindExtractedRoot(string tempRoot, string prefix, string pboName)
        {
            List<string> candidates = new List<string>();
            string fullTemp = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Action<string> addCandidate = delegate(string relative)
            {
                if (string.IsNullOrWhiteSpace(relative)) return;
                try
                {
                    string candidate = Path.GetFullPath(Path.Combine(tempRoot, relative.Trim('\\', '/')));
                    if (candidate.StartsWith(fullTemp, StringComparison.OrdinalIgnoreCase) &&
                        !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidates.Add(candidate);
                }
                catch { }
            };

            addCandidate(prefix);
            addCandidate(pboName);
            string[] prefixParts = (prefix ?? string.Empty).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (prefixParts.Length > 0)
            {
                addCandidate(prefixParts[prefixParts.Length - 1]);
                addCandidate(prefixParts[0]);
            }

            foreach (string candidate in candidates)
                if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*", SearchOption.AllDirectories).Length > 0)
                    return candidate;

            if (Directory.GetFiles(tempRoot).Length > 0) return tempRoot;
            return Directory.GetDirectories(tempRoot)
                .FirstOrDefault(directory => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length > 0);
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

        private static void ReplaceDirectoryByCopy(string replacementSource, string destination)
        {
            if (!Directory.Exists(replacementSource))
                throw new DirectoryNotFoundException("Source preparado não encontrado: " + replacementSource);
            if (File.Exists(destination))
                throw new IOException("Existe um arquivo no caminho reservado para a source: " + destination);

            string displaced = null;
            if (Directory.Exists(destination))
            {
                displaced = destination + ".dayzworkbench-replace-" + Guid.NewGuid().ToString("N");
                Directory.Move(destination, displaced);
            }

            try
            {
                Directory.CreateDirectory(destination);
                CopyDirectoryContents(replacementSource, destination);
            }
            catch
            {
                TryDeleteDirectory(destination);
                if (displaced != null && Directory.Exists(displaced) && !Directory.Exists(destination))
                    Directory.Move(displaced, destination);
                throw;
            }

            if (displaced != null) TryDeleteDirectory(displaced);
        }

        private static void ReplaceDirectoryByMove(string replacementSource, string destination)
        {
            if (!Directory.Exists(replacementSource))
                throw new DirectoryNotFoundException("Source reconstruído não encontrado: " + replacementSource);
            if (File.Exists(destination))
                throw new IOException("Existe um arquivo no caminho reservado para a source: " + destination);

            string displaced = null;
            if (Directory.Exists(destination))
            {
                displaced = destination + ".dayzworkbench-replace-" + Guid.NewGuid().ToString("N");
                Directory.Move(destination, displaced);
            }

            try
            {
                Directory.Move(replacementSource, destination);
            }
            catch
            {
                if (displaced != null && Directory.Exists(displaced) && !Directory.Exists(destination))
                    Directory.Move(displaced, destination);
                throw;
            }

            if (displaced != null) TryDeleteDirectory(displaced);
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
            _cfgConvertText.Text = _settings.CfgConvertPath;
            _pythonText.Text = _settings.PythonPath;
            _odolConverterText.Text = _settings.OdolConverterPath;
            _addonBuilderText.Text = _settings.AddonBuilderPath;
            _projectDriveText.Text = _settings.ProjectDrivePath;
            _fileBankText.Text = _settings.FileBankPath;
            _signerText.Text = _settings.SignerPath;
            _privateKeyText.Text = _settings.PrivateKeyPath;
            _dayZText.Text = _settings.DayZPath;
            _dayZDiagText.Text = _settings.DayZDiagPath;
            _editorText.Text = _settings.EditorPath;
            _workshopText.Text = _settings.WorkshopPath;
            _editorDependenciesText.Text = _settings.EditorDependencies;
            _serverKeysText.Text = _settings.ServerKeysPath;
            _launchArgsText.Text = _settings.ExtraLaunchArgs;
        }

        private void ReadSettingsFromFields()
        {
            _settings.BenchPath = _benchText.Text.Trim();
            _settings.BankRevPath = _bankRevText.Text.Trim();
            _settings.CfgConvertPath = _cfgConvertText.Text.Trim();
            _settings.PythonPath = _pythonText.Text.Trim();
            _settings.OdolConverterPath = OdolConverterProvisioner.ManagedAddonPath;
            _settings.AddonBuilderPath = _addonBuilderText.Text.Trim();
            _settings.ProjectDrivePath = _projectDriveText.Text.Trim();
            _settings.FileBankPath = _fileBankText.Text.Trim();
            _settings.SignerPath = _signerText.Text.Trim();
            _settings.PrivateKeyPath = PrivateKeyStore.IsStoredPrivateKey(_privateKeyText.Text.Trim())
                ? Path.GetFullPath(_privateKeyText.Text.Trim())
                : string.Empty;
            _settings.DayZPath = _dayZText.Text.Trim();
            _settings.DayZDiagPath = _dayZDiagText.Text.Trim();
            _settings.EditorPath = _editorText.Text.Trim();
            _settings.WorkshopPath = _workshopText.Text.Trim();
            _settings.EditorDependencies = _editorDependenciesText.Text.Trim();
            _settings.ServerKeysPath = _serverKeysText.Text.Trim();
            _settings.ExtraLaunchArgs = _launchArgsText.Text.Trim();
        }

        private void AutoDetectSettings()
        {
            try
            {
                DetectedPaths detected = SteamPathDetector.Detect();
                List<string> found = new List<string>();
                List<string> missing = new List<string>();

                ApplyDetectedPath(_bankRevText, detected.BankRevPath, "BankRev", found, missing);
                ApplyDetectedPath(_cfgConvertText, detected.CfgConvertPath, "CfgConvert", found, missing);
                ApplyDetectedPath(_pythonText, detected.PythonPath, "Python", found, missing);
                _odolConverterText.Text = OdolConverterProvisioner.ManagedAddonPath;
                found.Add(File.Exists(OdolConverterProvisioner.ManagedAddonPath)
                    ? "addon ODOL"
                    : "addon ODOL (será baixado no primeiro uso)");
                ApplyDetectedPath(_addonBuilderText, detected.AddonBuilderPath, "Addon Builder", found, missing);
                ApplyDetectedPath(_fileBankText, detected.FileBankPath, "FileBank", found, missing);
                ApplyDetectedPath(_signerText, detected.SignerPath, "DSSignFile", found, missing);
                ApplyDetectedPath(_dayZText, detected.DayZPath, "DayZ", found, missing);
                ApplyDetectedPath(_dayZDiagText, detected.DayZDiagPath, "DayZDiag", found, missing);
                ApplyDetectedPath(_workshopText, detected.WorkshopPath, "!Workshop", found, missing);
                ApplyDetectedPath(_editorText, detected.EditorPath, "DayZ Editor", found, missing);
                ApplyDetectedPath(_editorDependenciesText, detected.EditorDependencies, "dependências do Editor", found, missing);

                string message = found.Count == 0
                    ? "Nenhum caminho foi encontrado. Confirme se o Steam, o DayZ e o DayZ Tools estão instalados."
                    : "Preenchido automaticamente:\n\n• " + string.Join("\n• ", found.ToArray());
                if (missing.Count > 0)
                    message += "\n\nNão encontrado:\n\n• " + string.Join("\n• ", missing.ToArray());
                message += "\n\nBancada de mods e chaves públicas não foram alteradas. A chave privada é gerenciada na pasta keys do Workbench. Clique em Salvar configurações para confirmar.";

                MessageBox.Show(this, message, "Auto detectar", MessageBoxButtons.OK,
                    found.Count > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                Log("Auto detecção: " + found.Count + " caminho(s) preenchido(s), " + missing.Count + " não encontrado(s).", _accent);
                RefreshWorkDriveButton();
            }
            catch (Exception ex)
            {
                ShowError("Não foi possível detectar as instalações da Steam.", ex);
            }
        }

        private async Task<string> EnsureOdolConverterAvailable()
        {
            string resolved = await OdolConverterProvisioner.EnsureAvailableAsync(_settings.OdolConverterPath, LogLine);
            if (!resolved.Equals(_settings.OdolConverterPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                _settings.OdolConverterPath = resolved;
                if (_odolConverterText != null) _odolConverterText.Text = resolved;
                try { _settings.Save(); }
                catch (Exception ex) { DiagnosticLog("Conversor baixado, mas o caminho não pôde ser salvo: " + ex.Message); }
            }
            return resolved;
        }

        private static void ApplyDetectedPath(TextBox field, string path, string label, List<string> found, List<string> missing)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                missing.Add(label);
                return;
            }
            field.Text = path;
            found.Add(label);
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
                RefreshWorkDriveButton();
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
            CheckFile(_settings.CfgConvertPath, "CfgConvert", missing);
            CheckFile(_settings.PythonPath, "Python", missing);
            CheckFile(_settings.AddonBuilderPath, "Addon Builder", missing);
            CheckDirectory(_settings.ProjectDrivePath, "Work drive / projeto", missing);
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
            return relative.StartsWith("source\\") || relative.StartsWith("_test\\");
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
            string name = Path.GetFileNameWithoutExtension(pbo.FullPath);
            string destination = Path.Combine(sourceRoot, name);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            {
                DialogResult answer = MessageBox.Show(this,
                    "O source já existe. Deseja substituí-lo pela nova extração?",
                    "Source existente", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "DayZModWorkbench", "ManualExtract_" + Guid.NewGuid().ToString("N"));

            try
            {
                SetBusy(true, "Lendo propriedades de " + Path.GetFileName(pbo.FullPath));
                ProcessResult properties = await ProcessRunner.RunAsync(_settings.BankRevPath, "-p " + ProcessRunner.Quote(pbo.FullPath), project.FullPath, LogLine);
                string prefix = ParsePrefix(properties.Output);
                if (IsProtectedPbo(properties.Output))
                {
                    SetStatus("Recuperando PBO ofuscado " + Path.GetFileName(pbo.FullPath));
                    Directory.CreateDirectory(tempRoot);
                    Log(name + ": ofuscação detectada; iniciando recuperação pelo addon Python.", _accent);
                    PboSourceRecoveryResult recovery = await RecoverProtectedPbo(pbo.FullPath, tempRoot);
                    if (!string.IsNullOrWhiteSpace(recovery.Prefix)) prefix = recovery.Prefix;
                    if (string.IsNullOrWhiteSpace(prefix)) prefix = name;
                    ReplaceDirectoryByCopy(recovery.SourceRoot, destination);
                    SaveProjectPrefix(project.FullPath, name, prefix);
                    _prefixText.Text = prefix;
                    RefreshProjectContents();
                    SelectSourceByName(name);
                    Log("PBO ofuscado recuperado: " + destination + " — " + recovery.Summary, _success);
                    foreach (string warning in recovery.Warnings) Log(warning, Color.Gold);
                    string recoveryWarnings = recovery.Warnings.Count == 0
                        ? string.Empty
                        : "\n\nAvisos:\n• " + string.Join("\n• ", recovery.Warnings.ToArray());
                    MessageBox.Show(this, recovery.Summary + recoveryWarnings,
                        "Source ofuscada recuperada", MessageBoxButtons.OK,
                        recovery.Warnings.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    SetStatus("PBO ofuscado recuperado para source");
                    return;
                }
                if (string.IsNullOrWhiteSpace(prefix)) prefix = name;
                if (!File.Exists(_settings.CfgConvertPath))
                    throw new FileNotFoundException("CfgConvert.exe não foi encontrado. Confira Configurações.", _settings.CfgConvertPath);

                SetStatus("Extraindo e desbinarizando " + Path.GetFileName(pbo.FullPath));
                Directory.CreateDirectory(tempRoot);
                string arguments = "-f " + ProcessRunner.Quote(tempRoot) + " -t " + ProcessRunner.Quote(pbo.FullPath);
                ProcessResult extract = await ProcessRunner.RunAsync(_settings.BankRevPath, arguments, project.FullPath, LogLine);
                if (extract.ExitCode != 0) throw new InvalidOperationException("BankRev terminou com código " + extract.ExitCode);
                string extractedRoot = FindExtractedRoot(tempRoot, prefix, name);
                if (extractedRoot == null || Directory.GetFiles(extractedRoot, "*", SearchOption.AllDirectories).Length == 0)
                    throw new InvalidOperationException("A extração não produziu arquivos utilizáveis. O PBO pode estar protegido/ofuscado.");

                List<string> supplementalWarnings = new List<string>();
                if (HasEmptyRootConfigPlaceholder(extractedRoot))
                {
                    try
                    {
                        Log(name + ": config.cpp vazio detectado; recuperando scripts/config pelo addon Python v4.", _accent);
                        PboSourceRecoveryResult recovery = await RecoverProtectedPbo(pbo.FullPath, tempRoot);
                        PboExtractionAuditResult audit = PboExtractionAuditor.Validate(recovery.ManifestPath, extractedRoot);
                        Log(name + ": " + audit.Summary + ".", _success);
                        MergeRecoveredPboSource(recovery.SourceRoot, extractedRoot);
                        if (!string.IsNullOrWhiteSpace(recovery.Prefix)) prefix = recovery.Prefix;
                        supplementalWarnings.AddRange(recovery.Warnings);
                        Log(name + ": recuperação complementar aplicada — " + recovery.Summary, _success);
                    }
                    catch (Exception ex)
                    {
                        supplementalWarnings.Add("Recuperação complementar pelo Python falhou; a extração normal foi mantida. " + ex.Message);
                    }
                }

                SourcePreparationResult preparation = await SourcePreparer.PrepareAsync(extractedRoot,
                    _settings.CfgConvertPath, LogLine);

                string preparedRoot = extractedRoot;
                OdolReconstructionResult reconstruction = null;
                List<string> preparationWarnings = new List<string>(supplementalWarnings);
                preparationWarnings.AddRange(preparation.Warnings);
                if (preparation.OdolPreserved > 0)
                {
                    if (!File.Exists(_settings.PythonPath))
                        preparationWarnings.Add("Python não configurado; modelos ODOL foram preservados.");
                    else
                    {
                        try
                        {
                            string converterPath = await EnsureOdolConverterAvailable();
                            reconstruction = await OdolSourceReconstructor.ReconstructAsync(extractedRoot,
                                _settings.PythonPath, converterPath, LogLine);
                            if (reconstruction.Changed) preparedRoot = reconstruction.OutputRoot;
                            preparationWarnings.AddRange(reconstruction.Warnings);
                        }
                        catch (Exception ex)
                        {
                            preparationWarnings.Add("ODOL não reconstruído; originais preservados. " + ex.Message);
                        }
                    }
                }

                ReplaceDirectoryByCopy(preparedRoot, destination);
                if (!preparedRoot.Equals(extractedRoot, StringComparison.OrdinalIgnoreCase)) TryDeleteDirectory(preparedRoot);

                SaveProjectPrefix(project.FullPath, name, prefix);
                _prefixText.Text = prefix;
                RefreshProjectContents();
                SelectSourceByName(name);
                string reconstructionSummary = reconstruction == null ? string.Empty : "\n" + reconstruction.Summary;
                bool sourceComplete = preparation.IsComplete && preparationWarnings.Count == 0;
                Log((sourceComplete ? "Extração concluída e source preparada: " :
                    "Extração concluída com pendências: ") + destination + " — " + preparation.Summary + " " +
                    reconstructionSummary, sourceComplete ? _success : Color.Gold);
                foreach (string warning in preparationWarnings) Log(warning, Color.Gold);
                string warningText = preparationWarnings.Count == 0
                    ? string.Empty
                    : "\n\nAvisos:\n• " + string.Join("\n• ", preparationWarnings.ToArray());
                MessageBox.Show(this, preparation.Summary + reconstructionSummary + warningText,
                    sourceComplete ? "Source extraída e preparada" : "Source extraída com pendências",
                    MessageBoxButtons.OK,
                    preparationWarnings.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                SetStatus(sourceComplete ? "PBO extraído e source desbinarizada" :
                    "PBO extraído; source possui arquivos binários preservados");
            }
            catch (Exception ex)
            {
                ShowError("Falha ao extrair o PBO.", ex);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
                SetBusy(false, "Pronto");
            }
        }

        private async Task PrepareSelectedSource()
        {
            PathItem project = SelectedProject;
            PathItem source = SelectedSource;
            if (project == null || source == null)
            {
                MessageBox.Show(this, "Selecione um projeto e um source.", "Desbinarização", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(_settings.CfgConvertPath))
            {
                MessageBox.Show(this, "CfgConvert.exe não foi encontrado. Confira Configurações.", "Desbinarização", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(_settings.PythonPath))
            {
                MessageBox.Show(this, "Python não foi encontrado. Confira Configurações.", "Desbinarização", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult answer = MessageBox.Show(this,
                "Converter config.bin e materiais RaP para texto e reconstruir modelos ODOL53/54/55 como MLOD?\n\nA source atual será substituída após a reconstrução ser validada.",
                "Preparar source editável", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (answer != DialogResult.OK) return;

            try
            {
                SetBusy(true, "Desbinarizando " + source.Name);
                SourcePreparationResult preparation = await SourcePreparer.PrepareAsync(source.FullPath,
                    _settings.CfgConvertPath, LogLine);

                string converterPath = preparation.OdolPreserved > 0
                    ? await EnsureOdolConverterAvailable()
                    : _settings.OdolConverterPath;
                OdolReconstructionResult reconstruction = await OdolSourceReconstructor.ReconstructAsync(source.FullPath,
                    _settings.PythonPath, converterPath, LogLine);
                if (reconstruction.Changed)
                    ReplaceDirectoryByMove(reconstruction.OutputRoot, source.FullPath);

                Log("Source preparada: " + source.FullPath + " — " + preparation.Summary + " " + reconstruction.Summary, _success);
                foreach (string warning in preparation.Warnings) Log(warning, Color.Gold);
                foreach (string warning in reconstruction.Warnings) Log(warning, Color.Gold);
                List<string> allWarnings = new List<string>(preparation.Warnings);
                allWarnings.AddRange(reconstruction.Warnings);
                string warningText = allWarnings.Count == 0
                    ? string.Empty
                    : "\n\nAvisos:\n• " + string.Join("\n• ", allWarnings.ToArray());
                MessageBox.Show(this, preparation.Summary + "\n" + reconstruction.Summary + warningText,
                    "Source preparada", MessageBoxButtons.OK,
                    allWarnings.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                SetStatus("Source preparada para edição");
            }
            catch (Exception ex)
            {
                ShowError("Falha ao desbinarizar o source.", ex);
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
            bool hasMlodModels = ContainsMlodModels(source.FullPath);

            if (hasMlodModels && (!File.Exists(_settings.AddonBuilderPath) || !Directory.Exists(_settings.ProjectDrivePath)))
            {
                MessageBox.Show(this, "A source contém modelos MLOD. Configure AddonBuilder.exe e o work drive para binarizá-los.",
                    "Compilação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (!hasMlodModels && !File.Exists(_settings.FileBankPath))
            {
                MessageBox.Show(this, "FileBank.exe não foi encontrado.", "Compilação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string privateKeyPath = _privateKeyText.Text.Trim();
            if (sign && !PrivateKeyStore.IsStoredPrivateKey(privateKeyPath))
            {
                MessageBox.Show(this, "Adicione uma chave .biprivatekey à pasta keys do Workbench.", "Assinatura", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string tempRoot = Path.Combine(Path.GetTempPath(), "DayZModWorkbench", Guid.NewGuid().ToString("N"));
            string tempOut = Path.Combine(tempRoot, "out");
            string stagedSource = null;
            string projectDrive = null;
            Directory.CreateDirectory(tempOut);
            try
            {
                if (manageBusy) SetBusy(true, "Compilando " + source.Name);
                Log("Compilando source: " + source.FullPath, _accent);
                ProcessResult result;
                if (hasMlodModels)
                {
                    projectDrive = Path.GetFullPath(_settings.ProjectDrivePath);
                    stagedSource = StageSourceForAddonBuilder(source.FullPath, projectDrive, prefix);
                    string arguments = ProcessRunner.Quote(stagedSource) + " " + ProcessRunner.Quote(tempOut) +
                        " -clear -prefix=" + ProcessRunner.Quote(prefix) +
                        " -project=" + ProcessRunner.Quote(projectDrive.TrimEnd('\\', '/')) +
                        " -temp=" + ProcessRunner.Quote(Path.Combine(tempRoot, "binarized")) + " -binarizeFullLogs";
                    result = await ProcessRunner.RunAsync(_settings.AddonBuilderPath, arguments, projectDrive, LogLine);
                    if (result.ExitCode != 0 || result.Output.IndexOf("Build Successful", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidOperationException("Addon Builder não concluiu a binarização dos modelos MLOD.");
                }
                else
                {
                    string arguments = "-property " + ProcessRunner.Quote("prefix=" + prefix) + " -dst " + ProcessRunner.Quote(tempOut) + " " + ProcessRunner.Quote(source.FullPath);
                    result = await ProcessRunner.RunAsync(_settings.FileBankPath, arguments, project.FullPath, LogLine);
                    if (result.ExitCode != 0) throw new InvalidOperationException("FileBank terminou com código " + result.ExitCode);
                }

                string expected = Path.Combine(tempOut, source.Name + ".pbo");
                string builtPbo = File.Exists(expected) ? expected : Directory.GetFiles(tempOut, "*.pbo").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (builtPbo == null) throw new InvalidOperationException("A ferramenta de compilação não criou um PBO.");
                FileInfo info = new FileInfo(builtPbo);
                if (info.Length < 128) throw new InvalidOperationException("O PBO criado parece vazio (" + info.Length + " bytes).");

                ProcessResult list = await ProcessRunner.RunAsync(_settings.BankRevPath, "-l " + ProcessRunner.Quote(builtPbo), tempOut, LogLine);
                if (list.ExitCode != 0 || string.IsNullOrWhiteSpace(list.Output)) throw new InvalidOperationException("Não foi possível validar o conteúdo do PBO criado.");

                string signature = null;
                if (sign)
                {
                    ProcessResult signed = await ProcessRunner.RunAsync(_settings.SignerPath,
                        ProcessRunner.Quote(privateKeyPath) + " " + ProcessRunner.Quote(builtPbo), tempOut, LogLine);
                    if (signed.ExitCode != 0) throw new InvalidOperationException("DSSignFile terminou com código " + signed.ExitCode);
                    signature = Directory.GetFiles(tempOut, Path.GetFileName(builtPbo) + ".*.bisign").FirstOrDefault();
                    if (signature == null) throw new InvalidOperationException("A assinatura BISIGN não foi criada.");
                }

                string outputFolder = Path.Combine(project.FullPath, "PBO");
                Directory.CreateDirectory(outputFolder);
                string destinationPbo = Path.Combine(outputFolder, Path.GetFileName(builtPbo));
                foreach (string oldSignature in Directory.GetFiles(outputFolder, Path.GetFileName(builtPbo) + ".*.bisign"))
                    File.Delete(oldSignature);
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
                if (stagedSource != null && !stagedSource.Equals(source.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(stagedSource);
                    TryDeleteEmptyParents(Path.GetDirectoryName(stagedSource), projectDrive);
                }
                TryDeleteDirectory(tempRoot);
            }
        }

        private static bool ContainsMlodModels(string sourceRoot)
        {
            foreach (string model in Directory.GetFiles(sourceRoot, "*.p3d", SearchOption.AllDirectories))
            {
                try
                {
                    byte[] signature = new byte[4];
                    using (FileStream stream = new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.Read))
                        if (stream.Read(signature, 0, signature.Length) == signature.Length && Encoding.ASCII.GetString(signature) == "MLOD") return true;
                }
                catch { }
            }
            return false;
        }

        private static string StageSourceForAddonBuilder(string sourceRoot, string projectDrive, string prefix)
        {
            string fullProject = Path.GetFullPath(projectDrive).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string relativePrefix = prefix.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
            string staged = Path.GetFullPath(Path.Combine(fullProject, relativePrefix));
            if (!staged.StartsWith(fullProject, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Prefixo do PBO aponta para fora do work drive: " + prefix);
            if (Path.GetFullPath(sourceRoot).TrimEnd('\\', '/').Equals(staged.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return sourceRoot;
            if (Directory.Exists(staged) || File.Exists(staged))
                throw new InvalidOperationException("O caminho temporário do prefixo já existe no work drive: " + staged);

            try
            {
                Directory.CreateDirectory(staged);
                CopyDirectoryContents(sourceRoot, staged);
                return staged;
            }
            catch
            {
                TryDeleteDirectory(staged);
                TryDeleteEmptyParents(Path.GetDirectoryName(staged), fullProject);
                throw;
            }
        }

        private static void TryDeleteEmptyParents(string directory, string stopAt)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stopAt)) return;
            string stop = Path.GetFullPath(stopAt).TrimEnd('\\', '/');
            string current = Path.GetFullPath(directory).TrimEnd('\\', '/');
            try
            {
                while (!current.Equals(stop, StringComparison.OrdinalIgnoreCase) && Directory.Exists(current) &&
                    !Directory.EnumerateFileSystemEntries(current).Any())
                {
                    Directory.Delete(current);
                    DirectoryInfo parent = Directory.GetParent(current);
                    if (parent == null) break;
                    current = parent.FullName.TrimEnd('\\', '/');
                }
            }
            catch { }
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

        private void RefreshWorkDriveButton()
        {
            if (_workDriveButton == null) return;
            try
            {
                string configuredDrive = _projectDriveText != null && !string.IsNullOrWhiteSpace(_projectDriveText.Text)
                    ? _projectDriveText.Text.Trim()
                    : _settings.ProjectDrivePath;
                WorkDriveState state = WorkDriveManager.GetState(configuredDrive);
                string executable = WorkDriveManager.FindExecutable(_settings.AddonBuilderPath,
                    _settings.BankRevPath, _settings.CfgConvertPath);
                bool managed = WorkDriveManager.IsManagedMapping(executable, state);
                if (!state.IsAvailable)
                {
                    _workDriveButton.Text = "Montar " + state.DriveName;
                    _workDriveButton.BackColor = Color.FromArgb(155, 90, 230);
                    _workDriveButton.Enabled = !_busy;
                    _workDriveButton.Tag = "WorkDrive desmontado";
                }
                else if (managed)
                {
                    _workDriveButton.Text = "Desmontar " + state.DriveName;
                    _workDriveButton.BackColor = _success;
                    _workDriveButton.Enabled = !_busy;
                    _workDriveButton.Tag = state.DriveName + " → " + state.TargetPath;
                }
                else
                {
                    _workDriveButton.Text = state.DriveName + " em uso";
                    _workDriveButton.BackColor = Color.DarkOrange;
                    _workDriveButton.Enabled = false;
                    _workDriveButton.Tag = "A letra está ocupada por outra unidade: " + state.TargetPath;
                }
            }
            catch (Exception ex)
            {
                _workDriveButton.Text = "WorkDrive inválido";
                _workDriveButton.BackColor = Color.DarkOrange;
                _workDriveButton.Enabled = false;
                _workDriveButton.Tag = ex.Message;
            }
        }

        private async Task ToggleWorkDrive()
        {
            try
            {
                string configuredDrive = _projectDriveText != null && !string.IsNullOrWhiteSpace(_projectDriveText.Text)
                    ? _projectDriveText.Text.Trim()
                    : _settings.ProjectDrivePath;
                WorkDriveState before = WorkDriveManager.GetState(configuredDrive);
                string executable = WorkDriveManager.FindExecutable(_settings.AddonBuilderPath,
                    _settings.BankRevPath, _settings.CfgConvertPath);
                if (before.IsAvailable && !WorkDriveManager.IsManagedMapping(executable, before))
                    throw new InvalidOperationException(before.DriveName + " está ocupado por outra unidade e não será desmontado.");

                bool mount = !before.IsAvailable;
                SetBusy(true, (mount ? "Montando " : "Desmontando ") + before.DriveName);
                Log((mount ? "Montando" : "Desmontando") + " WorkDrive " + before.DriveName + " pelo DayZ Tools...", _accent);
                await WorkDriveManager.SetMountedAsync(executable, mount, LogLine);

                WorkDriveState after = null;
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    after = WorkDriveManager.GetState(configuredDrive);
                    bool reached = mount
                        ? after.IsAvailable && WorkDriveManager.IsManagedMapping(executable, after)
                        : !after.IsAvailable;
                    if (reached) break;
                    await Task.Delay(100);
                }
                if (mount && (!after.IsAvailable || !WorkDriveManager.IsManagedMapping(executable, after)))
                    throw new InvalidOperationException("O DayZ Tools terminou, mas " + before.DriveName + " não foi montado corretamente.");
                if (!mount && after.IsAvailable)
                    throw new InvalidOperationException("O DayZ Tools terminou, mas " + before.DriveName + " continua montado.");

                string message = mount
                    ? after.DriveName + " montado em " + after.TargetPath
                    : before.DriveName + " desmontado";
                Log(message, _success);
                SetStatus(message);
            }
            catch (Exception ex)
            {
                ShowError("Não foi possível alterar o WorkDrive do DayZ Tools.", ex);
            }
            finally
            {
                SetBusy(false, "Pronto");
                RefreshWorkDriveButton();
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
                List<ModChoice> steamMods = ModChoice.DiscoverSteamMods(_settings.WorkshopPath);
                if (steamMods.Count == 0)
                    throw new DirectoryNotFoundException("Nenhum mod foi encontrado em !Workshop nem em workshop\\content\\221100.");
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

        private void ImportPrivateKey()
        {
            try
            {
                Directory.CreateDirectory(PrivateKeyStore.DirectoryPath);
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Filter = "Chave privada DayZ|*.biprivatekey";
                    dialog.InitialDirectory = PrivateKeyStore.DirectoryPath;
                    if (File.Exists(_privateKeyText.Text))
                        dialog.FileName = Path.GetFileName(_privateKeyText.Text);
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;

                    string localPath = PrivateKeyStore.Import(dialog.FileName);
                    _privateKeyText.Text = localPath;
                    _settings.PrivateKeyPath = localPath;
                    _settings.Save();
                    Log("Chave privada adicionada à pasta local do Workbench: " + localPath, _success);
                    SetStatus("Chave privada pronta para assinatura");
                }
            }
            catch (Exception ex)
            {
                ShowError("Não foi possível adicionar a chave privada à pasta keys.", ex);
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
