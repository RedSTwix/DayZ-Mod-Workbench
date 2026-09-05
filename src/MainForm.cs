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
        private sealed class SteamImportProgressState
        {
            public int TotalPbos;
            public int CompletedPbos;
            public bool Extract;
            public bool IncludesCopy;
        }

        private sealed class BuildProgressState
        {
            public int TotalSources;
            public int CompletedSources;
            public int ExpectedSyncFiles;
            public int SyncedFiles;
            public string CurrentSource;
        }

        private readonly Color _background = Color.FromArgb(22, 25, 30);
        private readonly Color _panel = Color.FromArgb(31, 35, 42);
        private readonly Color _field = Color.FromArgb(42, 47, 56);
        private readonly Color _accent = Color.FromArgb(0, 190, 230);
        private readonly Color _success = Color.FromArgb(64, 205, 140);
        private readonly Color _text = Color.FromArgb(230, 235, 242);

        private ToolSettings _settings;
        private ComboBox _projectCombo;
        private TextBox _pboSearch;
        private ListBox _pboList;
        private readonly List<PathItem> _projectPbos = new List<PathItem>();
        private ComboBox _sourceCombo;
        private GroupBox _pboActionsGroup;
        private GroupBox _buildActionsGroup;
        private Button _extractPboButton;
        private Button _openProjectButton;
        private Button _buildSignButton;
        private Button _buildAllButton;
        private TextBox _prefixText;
        private TextBox _privateKeyText;
        private CheckBox _loadingTestCheck;
        private CheckBox _filePatchingCheck;
        private RichTextBox _log;
        private ToolStripStatusLabel _status;
        private TabControl _tabs;
        private TableLayoutPanel _rootLayout;
        private GroupBox _steamProgressGroup;
        private TableLayoutPanel _steamProgressLayout;
        private Label _steamOverallProgressLabel;
        private Label _steamDetailProgressLabel;
        private ProgressBar _steamOverallProgressBar;
        private ProgressBar _steamDetailProgressBar;
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
        private TextBox _backupText;
        private TextBox _launchArgsText;
        private Button _workDriveButton;
        private string _privateKeyStoreNotice;
        private SplitContainer _projectSplit;
        private int _steamProgressPanelHeight;
        private int _heightBeforeSteamProgress;
        private bool _restoreHeightAfterSteamProgress;
        private bool _steamImportProgressActive;
        private bool _steamImportHasDetailProgress;
        private bool _suppressWindowPlacementSave;

        private const int DefaultPboPanelWidth = 360;
        private const int SteamMainProgressHeight = 72;
        private const int SteamDetailProgressHeight = 118;

        // Estes arquivos são entradas do Binarize e não devem ser copiados diretamente.
        // Todas as demais extensões encontradas no source são incluídas automaticamente.
        private static readonly string[] AddonBuilderProcessedExtensions = { ".cpp", ".cfg", ".p3d" };

        // Arquivos de controle e provas produzidos pelo Workbench/addon Python não pertencem ao mod.
        private static readonly string[] BuildAuxiliaryFileNames =
        {
            "$pboprefix$", "include.lst", ".dayzworkbench.ini",
            "MODEL_CFG_EQUIVALENCE_VERIFICATION.txt", "DEBINARIZE_REPORT.txt",
            "PBO_EQUIVALENCE_VERIFICATION.txt", "PBO_RECOVERY_REPORT.txt", "PBO_MANIFEST.json"
        };

        private static readonly string[] BuildAuxiliaryFileSuffixes =
        {
            ".embedded.json", ".forensic.json", ".verification.txt"
        };

        private readonly List<Control> _operationControls = new List<Control>();
        private readonly Dictionary<Button, Color> _busyButtonColors = new Dictionary<Button, Color>();
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
            RestoreWindowPlacement();
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
            FormClosing += delegate { SaveWindowPlacement(); };
        }

        private void RestoreWindowPlacement()
        {
            if (!_settings.WindowX.HasValue || !_settings.WindowY.HasValue) return;
            Rectangle candidate = new Rectangle(_settings.WindowX.Value, _settings.WindowY.Value,
                Width, Height);
            Rectangle titleArea = new Rectangle(candidate.X, candidate.Y,
                Math.Min(240, candidate.Width), Math.Min(64, candidate.Height));
            bool visible = Screen.AllScreens.Any(delegate(Screen screen)
            {
                Rectangle intersection = Rectangle.Intersect(screen.WorkingArea, titleArea);
                return intersection.Width >= 80 && intersection.Height >= 32;
            });
            if (!visible) return;

            StartPosition = FormStartPosition.Manual;
            Location = candidate.Location;
            if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
        }

        private void SaveWindowPlacement()
        {
            if (_suppressWindowPlacementSave) return;
            try
            {
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
                _settings.WindowX = bounds.X;
                _settings.WindowY = bounds.Y;
                _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
                _settings.Save();
            }
            catch (Exception ex)
            {
                DiagnosticLog("Não foi possível salvar a posição da janela: " + ex.Message);
            }
        }

        private void BuildInterface()
        {
            _rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(10)
            };
            TableLayoutPanel root = _rootLayout;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
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

            root.Controls.Add(BuildSteamProgressPanel(), 0, 2);

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
            root.Controls.Add(logGroup, 0, 3);

            StatusStrip strip = new StatusStrip { SizingGrip = false };
            _status = new ToolStripStatusLabel("Pronto") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            strip.Items.Add(_status);
            root.Controls.Add(strip, 0, 4);
        }

        private Control BuildSteamProgressPanel()
        {
            _steamProgressGroup = new GroupBox
            {
                Text = "Progresso da importação Steam",
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                Visible = false
            };
            _steamProgressLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4
            };
            _steamProgressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
            _steamProgressLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            _steamProgressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            _steamProgressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            _steamProgressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            _steamProgressLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));

            _steamOverallProgressLabel = new Label
            {
                Text = "Progresso total — 0%",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _steamOverallProgressBar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous
            };
            _steamDetailProgressLabel = new Label
            {
                Text = "Verificação — 0%",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Visible = false
            };
            _steamDetailProgressBar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Style = ProgressBarStyle.Continuous,
                Visible = false
            };
            _steamProgressLayout.Controls.Add(_steamOverallProgressLabel, 0, 0);
            _steamProgressLayout.Controls.Add(_steamOverallProgressBar, 0, 1);
            _steamProgressLayout.Controls.Add(_steamDetailProgressLabel, 0, 2);
            _steamProgressLayout.Controls.Add(_steamDetailProgressBar, 0, 3);
            _steamProgressLayout.SetColumnSpan(_steamOverallProgressLabel, 2);
            _steamProgressLayout.SetColumnSpan(_steamOverallProgressBar, 2);
            _steamProgressLayout.SetColumnSpan(_steamDetailProgressLabel, 2);
            _steamProgressLayout.SetColumnSpan(_steamDetailProgressBar, 2);
            _steamProgressGroup.Controls.Add(_steamProgressLayout);
            return _steamProgressGroup;
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

            _openProjectButton = MakeButton("Abrir projeto", _field);
            _openProjectButton.Click += delegate { OpenSelectedProject(); };
            header.Controls.Add(_openProjectButton, 4, 0);
            _operationControls.Add(_openProjectButton);

            Button settings = MakeButton("Configurações", _field);
            settings.Click += delegate { _tabs.SelectedIndex = 3; };
            header.Controls.Add(settings, 5, 0);
            _operationControls.Add(settings);
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

            _pboActionsGroup = new GroupBox { Text = "PBOs encontrados no projeto", Dock = DockStyle.Fill, Padding = new Padding(10) };
            GroupBox pboGroup = _pboActionsGroup;
            TableLayoutPanel pboLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
            pboLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            pboLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pboLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            pboLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            TableLayoutPanel pboSearchLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            pboSearchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            pboSearchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pboSearchLayout.Controls.Add(FieldLabel("Pesquisar PBO:"), 0, 0);
            _pboSearch = new TextBox { Dock = DockStyle.Fill };
            _pboSearch.TextChanged += delegate { RefreshPboList(); };
            pboSearchLayout.Controls.Add(_pboSearch, 1, 0);
            pboLayout.Controls.Add(pboSearchLayout, 0, 0);
            _pboList = new ListBox
            {
                Dock = DockStyle.Fill,
                HorizontalScrollbar = true,
                SelectionMode = SelectionMode.MultiExtended
            };
            _pboList.SelectedIndexChanged += delegate { UpdateProjectActionAvailability(); };
            pboLayout.Controls.Add(_pboList, 0, 1);
            pboLayout.Controls.Add(new Label
            {
                Text = "O conteúdo será extraído para source\\NomeDoPBO.",
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 2);
            _extractPboButton = MakeButton("Extrair PBO(s) selecionado(s) → source", _accent);
            _extractPboButton.Click += async delegate { await ExtractSelectedPbo(); };
            pboLayout.Controls.Add(_extractPboButton, 0, 3);
            _operationControls.Add(_extractPboButton);
            pboGroup.Controls.Add(pboLayout);
            split.Panel1.Controls.Add(pboGroup);

            _buildActionsGroup = new GroupBox { Text = "Compilar source → PBO / BISIGN", Dock = DockStyle.Fill, Padding = new Padding(12) };
            GroupBox buildGroup = _buildActionsGroup;
            TableLayoutPanel build = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 9 };
            build.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            build.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            build.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            for (int i = 0; i < 7; i++) build.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            build.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            build.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

            build.Controls.Add(FieldLabel("Source:"), 0, 0);
            _sourceCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            _sourceCombo.SelectedIndexChanged += delegate
            {
                LoadSourcePrefix();
                UpdateProjectActionAvailability();
            };
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
            prepareSource.Enabled = false;
            prepareSource.Visible = false;
            prepareSource.Click += async delegate { await PrepareSelectedSource(); };
            build.Controls.Add(prepareSource, 1, 4);
            build.SetColumnSpan(prepareSource, 2);

            Button buildOnly = MakeButton("Compilar PBO", _field);
            buildOnly.Click += async delegate { await BuildSelectedSource(false); };
            build.Controls.Add(buildOnly, 1, 5);
            _buildSignButton = MakeButton("PBO + BISIGN", _success);
            _buildSignButton.Click += async delegate { await BuildSelectedSource(true); };
            build.Controls.Add(_buildSignButton, 2, 5);
            _operationControls.Add(buildOnly);
            _operationControls.Add(_buildSignButton);

            _buildAllButton = MakeButton("Compilar e assinar todos os sources", _accent);
            _buildAllButton.Click += async delegate { await BuildAllSources(); };
            build.Controls.Add(_buildAllButton, 1, 6);
            build.SetColumnSpan(_buildAllButton, 2);
            _operationControls.Add(_buildAllButton);

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
            UpdateProjectActionAvailability();
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
                RowCount = 19
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            for (int i = 0; i < 18; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
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
                Text = "Destino: edit mod\\NomeDoMod\\PBO. A extração cria source\\NomeDoPBO e guarda mod.cpp/meta.cpp em source. Chaves não são copiadas.",
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

        private void BeginSteamImportProgress(bool extract, int totalPbos, string panelTitle = null)
        {
            _steamImportProgressActive = true;
            _steamImportHasDetailProgress = extract;
            _heightBeforeSteamProgress = Height;
            _restoreHeightAfterSteamProgress = WindowState == FormWindowState.Normal;
            _steamProgressGroup.Text = string.IsNullOrWhiteSpace(panelTitle)
                ? "Progresso da importação Steam"
                : panelTitle;
            _steamProgressGroup.Visible = true;
            _steamOverallProgressBar.Value = 0;
            _steamOverallProgressLabel.Text = (extract ? "Copiando e extraindo" : "Copiando") +
                " " + totalPbos + " PBO(s) — 0%";
            _steamDetailProgressBar.Value = 0;
            _steamDetailProgressBar.Style = ProgressBarStyle.Continuous;
            _steamDetailProgressBar.MarqueeAnimationSpeed = 0;
            _steamDetailProgressLabel.Text = "Verificação do PBO atual — aguardando — 0%";
            _steamDetailProgressLabel.Visible = extract;
            _steamDetailProgressBar.Visible = extract;
            _steamProgressLayout.RowStyles[2].Height = extract ? 22 : 0;
            _steamProgressLayout.RowStyles[3].Height = extract ? 24 : 0;
            SetSteamProgressPanelHeight(extract ? SteamDetailProgressHeight : SteamMainProgressHeight);
        }

        private void BeginBuildProgress(int totalSources, bool sign)
        {
            _steamImportProgressActive = true;
            _steamImportHasDetailProgress = true;
            _heightBeforeSteamProgress = Height;
            _restoreHeightAfterSteamProgress = WindowState == FormWindowState.Normal;
            _steamProgressGroup.Text = "Progresso da compilação";
            _steamProgressGroup.Visible = true;
            _steamOverallProgressBar.Style = ProgressBarStyle.Continuous;
            _steamOverallProgressBar.Value = 0;
            _steamOverallProgressLabel.Text = (sign ? "Compilando e assinando" : "Compilando") +
                " " + totalSources + " source(s) — 0%";
            _steamDetailProgressBar.Style = ProgressBarStyle.Continuous;
            _steamDetailProgressBar.MarqueeAnimationSpeed = 0;
            _steamDetailProgressBar.Value = 0;
            _steamDetailProgressLabel.Text = "Source atual — aguardando — 0%";
            _steamDetailProgressLabel.Visible = true;
            _steamDetailProgressBar.Visible = true;
            _steamProgressLayout.RowStyles[2].Height = 22;
            _steamProgressLayout.RowStyles[3].Height = 24;
            SetSteamProgressPanelHeight(SteamDetailProgressHeight);
        }

        private void ReportBuildSourceProgress(BuildProgressState progress, int sourcePercentage,
            string stage, bool indeterminate = false)
        {
            if (progress == null || progress.TotalSources <= 0) return;
            sourcePercentage = Math.Max(0, Math.Min(100, sourcePercentage));
            string sourceText = progress.CurrentSource + " — " + stage;
            if (indeterminate)
                SetSteamDetailIndeterminate(sourceText);
            else
                UpdateSteamDetailProgress(sourcePercentage, sourceText);

            int overall = (int)Math.Floor((progress.CompletedSources + sourcePercentage / 100d) *
                100d / progress.TotalSources);
            SetSteamOverallProgress(overall, "Source " + (progress.CompletedSources + 1) + "/" +
                progress.TotalSources + ": " + progress.CurrentSource);
        }

        private void ReportAddonBuilderProgress(BuildProgressState progress, string line)
        {
            if (progress == null || string.IsNullOrWhiteSpace(line)) return;
            string value = line.ToLowerInvariant();
            if (value.Contains("syncing folders"))
                ReportBuildSourceProgress(progress, 10, "copiando arquivos");
            else if (value.Contains("syncing file"))
            {
                progress.SyncedFiles++;
                int percentage = progress.ExpectedSyncFiles <= 0 ? 42 :
                    10 + (int)Math.Floor(Math.Min(progress.SyncedFiles, progress.ExpectedSyncFiles) *
                        34d / progress.ExpectedSyncFiles);
                ReportBuildSourceProgress(progress, percentage, "copiando arquivos " +
                    progress.SyncedFiles + "/" + Math.Max(progress.ExpectedSyncFiles, progress.SyncedFiles));
            }
            else if (value.Contains("converting configs"))
                ReportBuildSourceProgress(progress, 48, "convertendo configs");
            else if (value.Contains("binarizing texture headers"))
                ReportBuildSourceProgress(progress, 72, "binarizando cabeçalhos de texturas", true);
            else if (value.Contains("binarizing"))
                ReportBuildSourceProgress(progress, 58, "binarizando source", true);
            else if (value.Contains("packing \"") || value.Contains("packing '"))
                ReportBuildSourceProgress(progress, 82, "empacotando PBO", true);
            else if (value.Contains("copying pbo"))
                ReportBuildSourceProgress(progress, 88, "finalizando PBO");
            else if (value.Contains("build successful"))
                ReportBuildSourceProgress(progress, 90, "binarização concluída");
        }

        private void CompleteBuildSourceProgress(BuildProgressState progress)
        {
            if (progress == null) return;
            UpdateSteamDetailProgress(100, progress.CurrentSource + " — concluído");
            progress.CompletedSources++;
            int overall = progress.TotalSources <= 0 ? 100 :
                (int)Math.Floor(progress.CompletedSources * 100d / progress.TotalSources);
            SetSteamOverallProgress(overall, "Compilação concluída " + progress.CompletedSources + "/" +
                progress.TotalSources);
        }

        private void EndSteamImportProgress()
        {
            if (!_steamImportProgressActive) return;
            _steamImportProgressActive = false;
            _steamImportHasDetailProgress = false;
            _steamDetailProgressLabel.Visible = false;
            _steamDetailProgressBar.Visible = false;
            _steamProgressLayout.RowStyles[2].Height = 0;
            _steamProgressLayout.RowStyles[3].Height = 0;
            _rootLayout.RowStyles[2].Height = 0;
            _steamProgressPanelHeight = 0;
            _steamProgressGroup.Visible = false;
            if (_restoreHeightAfterSteamProgress && WindowState == FormWindowState.Normal)
                Height = _heightBeforeSteamProgress;
            _restoreHeightAfterSteamProgress = false;
        }

        private void SetSteamProgressPanelHeight(int height)
        {
            if (_rootLayout == null || _steamProgressPanelHeight == height) return;
            int difference = height - _steamProgressPanelHeight;
            _rootLayout.RowStyles[2].Height = height;
            _steamProgressPanelHeight = height;
            if (!_restoreHeightAfterSteamProgress || WindowState != FormWindowState.Normal) return;

            Rectangle workArea = Screen.FromControl(this).WorkingArea;
            int maximumHeight = Math.Max(MinimumSize.Height, workArea.Bottom - Top);
            Height = Math.Max(MinimumSize.Height, Math.Min(maximumHeight, Height + difference));
        }

        private void SetSteamOverallProgress(int percentage, string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int, string>(SetSteamOverallProgress), percentage, text);
                return;
            }
            if (!_steamImportProgressActive) return;
            percentage = Math.Max(0, Math.Min(100, percentage));
            _steamOverallProgressBar.Value = percentage;
            _steamOverallProgressLabel.Text = text + " — " + percentage + "%";
        }

        private void ReportSteamPboProgress(SteamImportProgressState progress, double currentPboFraction,
            string text)
        {
            if (progress == null || progress.TotalPbos <= 0) return;
            if (progress.Extract && !progress.IncludesCopy)
                currentPboFraction = (currentPboFraction - 0.22d) / 0.78d;
            currentPboFraction = Math.Max(0d, Math.Min(1d, currentPboFraction));
            double completed = progress.CompletedPbos + currentPboFraction;
            int percentage = (int)Math.Floor(completed * 100d / progress.TotalPbos);
            SetSteamOverallProgress(percentage, text);
        }

        private void CompleteSteamPboProgress(SteamImportProgressState progress, string text)
        {
            if (progress == null) return;
            progress.CompletedPbos++;
            int percentage = progress.TotalPbos <= 0 ? 100 :
                (int)Math.Floor(progress.CompletedPbos * 100d / progress.TotalPbos);
            SetSteamOverallProgress(percentage, text);
            if (progress.Extract)
                UpdateSteamDetailProgress(100, text + " — verificação concluída");
        }

        private void ShowSteamDetailProgress(int percentage, string text)
        {
            UpdateSteamDetailProgress(percentage, text);
        }

        private void UpdateSteamDetailProgress(int percentage, string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<int, string>(UpdateSteamDetailProgress), percentage, text);
                return;
            }
            if (!_steamImportProgressActive || !_steamImportHasDetailProgress) return;
            percentage = Math.Max(0, Math.Min(100, percentage));
            if (_steamDetailProgressBar.Style != ProgressBarStyle.Continuous)
            {
                _steamDetailProgressBar.MarqueeAnimationSpeed = 0;
                _steamDetailProgressBar.Style = ProgressBarStyle.Continuous;
            }
            _steamDetailProgressBar.Value = percentage;
            _steamDetailProgressLabel.Text = text + " — " + percentage + "%";
        }

        private void SetSteamDetailIndeterminate(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(SetSteamDetailIndeterminate), text);
                return;
            }
            if (!_steamImportProgressActive || !_steamImportHasDetailProgress) return;
            _steamDetailProgressBar.Style = ProgressBarStyle.Marquee;
            _steamDetailProgressBar.MarqueeAnimationSpeed = 25;
            _steamDetailProgressLabel.Text = text + " — em andamento";
        }

        private void ResetSteamDetailProgress(string text)
        {
            UpdateSteamDetailProgress(0, text + " — aguardando verificação");
        }

        private void HideSteamDetailProgress()
        {
            if (_steamDetailProgressLabel == null) return;
            if (_steamImportProgressActive && _steamImportHasDetailProgress) return;
            _steamDetailProgressLabel.Visible = false;
            _steamDetailProgressBar.Visible = false;
            _steamProgressLayout.RowStyles[2].Height = 0;
            _steamProgressLayout.RowStyles[3].Height = 0;
        }

        internal bool RunSteamProgressLayoutTest()
        {
            _suppressWindowPlacementSave = true;
            int originalHeight = Height;
            BeginSteamImportProgress(true, 4);
            Application.DoEvents();
            int fixedHeight = Height;
            SetSteamDetailIndeterminate("Recuperando payloads");
            Application.DoEvents();
            bool indeterminate = _steamDetailProgressBar.Style == ProgressBarStyle.Marquee &&
                Height == fixedHeight;
            ShowSteamDetailProgress(47, "PBO atual");
            Application.DoEvents();
            int heightWhileUpdating = Height;
            HideSteamDetailProgress();
            Application.DoEvents();
            bool fixedDetail = Height == fixedHeight && heightWhileUpdating == fixedHeight &&
                _steamDetailProgressBar.Visible && _steamDetailProgressBar.Value == 47 &&
                _steamOverallProgressBar.Width == _steamDetailProgressBar.Width;

            Button coloredButton = _operationControls.OfType<Button>()
                .FirstOrDefault(button => button.BackColor.ToArgb() == _accent.ToArgb());
            Color coloredButtonOriginal = coloredButton == null ? Color.Empty : coloredButton.BackColor;
            SetBusy(true, "Teste da interface");
            bool controlsLocked = !_tabs.Enabled && !_projectCombo.Enabled;
            bool buttonMuted = coloredButton != null &&
                coloredButton.BackColor.ToArgb() == _field.ToArgb();
            SetBusy(false, "Pronto");
            bool buttonColorRestored = coloredButton != null &&
                coloredButton.BackColor.ToArgb() == coloredButtonOriginal.ToArgb();
            EndSteamImportProgress();
            Application.DoEvents();
            bool restored = Height == originalHeight && !_steamProgressGroup.Visible;

            BeginSteamImportProgress(false, 2);
            Application.DoEvents();
            bool copyOnly = !_steamDetailProgressBar.Visible &&
                _steamProgressPanelHeight == SteamMainProgressHeight;
            EndSteamImportProgress();
            BeginBuildProgress(3, true);
            BuildProgressState buildProgress = new BuildProgressState
            {
                TotalSources = 3,
                CurrentSource = "Teste"
            };
            ReportBuildSourceProgress(buildProgress, 58, "binarizando source", true);
            Application.DoEvents();
            bool buildPanel = _steamProgressGroup.Visible && _steamDetailProgressBar.Visible &&
                _steamDetailProgressBar.Style == ProgressBarStyle.Marquee &&
                _steamOverallProgressBar.Value == 19 &&
                _steamOverallProgressBar.Width == _steamDetailProgressBar.Width;
            EndSteamImportProgress();
            return indeterminate && fixedDetail && controlsLocked && buttonMuted &&
                buttonColorRestored && restored && copyOnly && buildPanel;
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

            int totalPbos = selected.Sum(mod => FindSteamPbos(mod).Length);
            SteamImportProgressState progress = new SteamImportProgressState
            {
                TotalPbos = Math.Max(1, totalPbos),
                Extract = extract,
                IncludesCopy = true
            };
            try
            {
                SetBusy(true, extract ? "Copiando e extraindo mods Steam" : "Copiando mods Steam");
                BeginSteamImportProgress(extract, totalPbos);
                _steamImportWarnings.Clear();
                _steamImportVerifications.Clear();
                List<string> importedProjects = new List<string>();
                foreach (ModChoice mod in selected)
                {
                    string project = await ImportSteamMod(mod, extract, progress);
                    importedProjects.Add(project);
                }

                SetSteamOverallProgress(100, "Importação concluída");
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
                EndSteamImportProgress();
                SetBusy(false, "Pronto");
            }
        }

        private static string[] FindSteamPbos(ModChoice mod)
        {
            string addons = Path.Combine(mod.FullPath, "Addons");
            if (!Directory.Exists(addons)) addons = Path.Combine(mod.FullPath, "addons");
            return Directory.Exists(addons)
                ? Directory.GetFiles(addons, "*.pbo", SearchOption.TopDirectoryOnly)
                : new string[0];
        }

        private async Task<string> ImportSteamMod(ModChoice mod, bool extract,
            SteamImportProgressState progress = null)
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
                string pboDisplayName = Path.GetFileName(pbo);
                if (extract) ResetSteamDetailProgress(mod.Name + " — " + pboDisplayName);
                ReportSteamPboProgress(progress, 0d, mod.Name + " — preparando " + pboDisplayName);
                string destinationPbo = Path.Combine(pboOutput, Path.GetFileName(pbo));
                List<KeyValuePair<string, string>> copies = new List<KeyValuePair<string, string>>();
                if (File.Exists(destinationPbo))
                {
                    Directory.CreateDirectory(backupRoot);
                    copies.Add(new KeyValuePair<string, string>(destinationPbo,
                        Path.Combine(backupRoot, Path.GetFileName(destinationPbo))));
                    foreach (string oldSign in Directory.GetFiles(pboOutput, Path.GetFileName(pbo) + ".*.bisign"))
                        copies.Add(new KeyValuePair<string, string>(oldSign,
                            Path.Combine(backupRoot, Path.GetFileName(oldSign))));
                    createdBackup = true;
                }

                copies.Add(new KeyValuePair<string, string>(pbo, destinationPbo));
                foreach (string signature in Directory.GetFiles(addons, Path.GetFileName(pbo) + ".*.bisign"))
                    copies.Add(new KeyValuePair<string, string>(signature,
                        Path.Combine(pboOutput, Path.GetFileName(signature))));

                long totalCopyBytes = copies.Sum(item => Math.Max(1L, new FileInfo(item.Key).Length));
                long completedCopyBytes = 0;
                double copyShare = extract ? 0.22d : 0.98d;
                foreach (KeyValuePair<string, string> copyOperation in copies)
                {
                    long fileLength = Math.Max(1L, new FileInfo(copyOperation.Key).Length);
                    long completedBeforeFile = completedCopyBytes;
                    await CopyFileAsync(copyOperation.Key, copyOperation.Value, delegate(long copiedBytes)
                    {
                        double copyFraction = (completedBeforeFile + Math.Min(fileLength, copiedBytes)) /
                            (double)Math.Max(1L, totalCopyBytes);
                        ReportSteamPboProgress(progress, copyFraction * copyShare,
                            mod.Name + " — copiando " + pboDisplayName);
                    });
                    completedCopyBytes += fileLength;
                }

                if (extract)
                {
                    try
                    {
                        await ExtractImportedPbo(projectRoot, destinationPbo, progress, mod.Name);
                    }
                    catch (Exception ex)
                    {
                        string warning = pboDisplayName + ": falha isolada ao preparar a source; " +
                            "o PBO original foi preservado e a importação continuará com os próximos PBOs. " + ex.Message;
                        _steamImportWarnings.Add(warning);
                        Log(warning, Color.Gold);
                        DiagnosticLog("Falha isolada durante importação de " + pboDisplayName + ": " + ex);
                    }
                }
                CompleteSteamPboProgress(progress, mod.Name + " — " + pboDisplayName + " concluído");
            }

            PlaceImportedMetadata(mod.FullPath, projectRoot, extract);
            if (createdBackup) Log("Versão anterior preservada em: " + backupRoot, Color.Gold);
            Log("Importado para: " + projectRoot, _success);
            return projectRoot;
        }

        private static async Task CopyFileAsync(string source, string destination, Action<long> progress)
        {
            string destinationDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);
            byte[] buffer = new byte[1024 * 1024];
            long copied = 0;
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await output.WriteAsync(buffer, 0, read);
                    copied += read;
                    if (progress != null) progress(copied);
                }
            }
            if (progress != null) progress(Math.Max(1L, copied));
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
                    _settings.CfgConvertPath, LogLine, true);
                string converterPath = preparation.OdolPreserved > 0
                    ? await EnsureOdolConverterAvailable()
                    : _settings.OdolConverterPath;
                OdolReconstructionResult reconstruction = await OdolSourceReconstructor.ReconstructAsync(sourcePath,
                    _settings.PythonPath, converterPath, LogLine);
                SourcePreparationResult finalPreparation = await ReauditAfterOdolAsync(
                    preparation, reconstruction, reconstruction.Changed ? reconstruction.OutputRoot : sourcePath);
                if (reconstruction.Changed)
                    ReplaceDirectoryByMove(reconstruction.OutputRoot, sourcePath);
                List<string> warnings = new List<string>(finalPreparation.Warnings);
                warnings.AddRange(reconstruction.Warnings);
                string details = preparation.Summary + " " + reconstruction.Summary +
                    (ReferenceEquals(finalPreparation, preparation) ? string.Empty : " Auditoria final: " + finalPreparation.Summary) +
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

        private async Task<SourcePreparationResult> ReauditAfterOdolAsync(
            SourcePreparationResult initial, OdolReconstructionResult reconstruction, string preparedRoot)
        {
            bool recoveredRvmats = reconstruction != null && reconstruction.Changed &&
                reconstruction.RecoveredRvmats > 0;
            if (!recoveredRvmats && initial.BinaryFilesPreserved <= 0)
                return initial;

            if (recoveredRvmats)
            {
                if (reconstruction.SourceProofRvmats > 0 || reconstruction.SemanticOnlyRvmats > 0)
                {
                    Log("RVMAT v7 final: " + reconstruction.RecoveredRvmats + " material(is) reconstruído(s) — " +
                        reconstruction.ForensicRvmats + " por RaP forense direto, " +
                        reconstruction.SourceProofRvmats + " por RaP+EmbeddedMaterial com prova byte-a-byte e " +
                        reconstruction.SemanticOnlyRvmats + " somente semântico(s); executando auditoria final da source...", _accent);
                }
                else
                {
                    Log("RVMAT v7: " + reconstruction.RecoveredRvmats + " material(is) reconstruído(s) — " +
                        reconstruction.ForensicRvmats + " por RaP forense e " + reconstruction.EmbeddedRvmats +
                        " por EmbeddedMaterial; executando auditoria final da source...", _accent);
                }
            }
            else
            {
                Log("Auditoria final da source: verificando " + initial.BinaryFilesPreserved +
                    " arquivo(s) RaP que permaneceram pendentes após a tentativa de recuperação v7...", _accent);
            }

            SourcePreparationResult finalResult = await SourcePreparer.PrepareAsync(preparedRoot,
                _settings.CfgConvertPath, LogLine);
            Log("Auditoria final após recuperação RVMAT v7: " + finalResult.Summary,
                finalResult.IsComplete && finalResult.Warnings.Count == 0 ? _success : Color.Gold);
            return finalResult;
        }

        private async Task ExtractImportedPbo(string projectRoot, string pbo,
            SteamImportProgressState progress = null, string modName = null)
        {
            if (!File.Exists(_settings.BankRevPath)) throw new FileNotFoundException("BankRev.exe não encontrado.", _settings.BankRevPath);
            string pboName = Path.GetFileNameWithoutExtension(pbo);
            string progressName = (string.IsNullOrWhiteSpace(modName) ? string.Empty : modName + " — ") + pboName;
            ReportSteamPboProgress(progress, 0.23d, progressName + " — lendo propriedades");
            ProcessResult properties = await ProcessRunner.RunAsync(_settings.BankRevPath,
                "-p " + ProcessRunner.Quote(pbo), projectRoot, LogLine);
            string prefix = ParsePrefix(properties.Output);
            if (string.IsNullOrWhiteSpace(prefix)) prefix = pboName;
            string tempRoot = Program.CreateTemporaryPath("SteamImport");
            ReportSteamPboProgress(progress, 0.28d, progressName + " — propriedades lidas");

            if (IsProtectedPbo(properties.Output))
            {
                Directory.CreateDirectory(tempRoot);
                try
                {
                    Log(pboName + ": ofuscação detectada; iniciando recuperação pelo addon Python.", _accent);
                    ReportSteamPboProgress(progress, 0.35d, progressName + " — recuperação protegida");
                    PboSourceRecoveryResult recovery = await RecoverProtectedPbo(pbo, tempRoot, progress, pboName);
                    if (!string.IsNullOrWhiteSpace(recovery.Prefix)) prefix = recovery.Prefix;
                    string sourceRoot = Path.Combine(projectRoot, "source");
                    string destination = Path.Combine(sourceRoot, pboName);
                    ReportSteamPboProgress(progress, 0.92d, progressName + " — gravando source recuperada");
                    await Task.Run(delegate { ReplaceDirectoryByCopy(recovery.SourceRoot, destination); });
                    SaveProjectPrefix(projectRoot, pboName, prefix);
                    ReportSteamPboProgress(progress, 0.98d, progressName + " — source recuperada");
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
                    UpdateSteamDetailProgress(99, pboName + " — limpando arquivos temporários");
                    ReportSteamPboProgress(progress, 0.99d, progressName + " — limpando temporários");
                    await Task.Run(delegate { TryDeleteDirectory(tempRoot); });
                }
                return;
            }

            if (!File.Exists(_settings.CfgConvertPath)) throw new FileNotFoundException("CfgConvert.exe não encontrado.", _settings.CfgConvertPath);
            Directory.CreateDirectory(tempRoot);
            try
            {
                ReportSteamPboProgress(progress, 0.32d, progressName + " — extraindo PBO");
                ProcessResult result = await ProcessRunner.RunAsync(_settings.BankRevPath,
                    "-f " + ProcessRunner.Quote(tempRoot) + " -t " + ProcessRunner.Quote(pbo), projectRoot, LogLine);

                string extractedRoot = FindExtractedRoot(tempRoot, prefix, pboName);
                bool bankRevProtected = IsProtectedPbo(result.Output);
                bool bankRevFailed = result.ExitCode != 0;
                bool extractionEmpty = extractedRoot == null ||
                    !Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories).Any();
                if (bankRevProtected || bankRevFailed || extractionEmpty)
                {
                    string reason = bankRevProtected ? "BankRev informou PBO protegido/ofuscado" :
                        bankRevFailed ? "BankRev falhou com código " + result.ExitCode :
                        "BankRev não produziu arquivos utilizáveis";
                    Log(pboName + ": " + reason + "; tentando addon Python v7.", _accent);
                    try
                    {
                        PboSourceRecoveryResult recovery = await RecoverProtectedPbo(pbo, tempRoot, progress, pboName);
                        if (!string.IsNullOrWhiteSpace(recovery.Prefix)) prefix = recovery.Prefix;
                        string recoveredSourceRoot = Path.Combine(projectRoot, "source");
                        string recoveredDestination = Path.Combine(recoveredSourceRoot, pboName);
                        await Task.Run(delegate { ReplaceDirectoryByCopy(recovery.SourceRoot, recoveredDestination); });
                        SaveProjectPrefix(projectRoot, pboName, prefix);
                        Log("PBO recuperado pelo addon Python: " + pboName + " → " + recoveredDestination + " — " + recovery.Summary, _success);
                        foreach (string item in recovery.Warnings)
                        {
                            string warning = pboName + ": " + item;
                            _steamImportWarnings.Add(warning);
                            Log(warning, Color.Gold);
                        }
                    }
                    catch (Exception ex)
                    {
                        string warning = pboName + ": BankRev e addon Python não produziram source utilizável; " +
                            "o PBO original foi preservado. " + ex.Message;
                        _steamImportWarnings.Add(warning);
                        Log(warning, Color.Gold);
                    }
                    return;
                }
                ReportSteamPboProgress(progress, 0.44d, progressName + " — conteúdo extraído");

                bool pythonConfigRecoveryAttempted = false;
                string configRecoveryReason;
                if (SourcePreparer.NeedsPythonConfigRecovery(extractedRoot, out configRecoveryReason))
                {
                    pythonConfigRecoveryAttempted = true;
                    try
                    {
                        Log(pboName + ": " + configRecoveryReason +
                            " detectado; acionando addon Python v7 para recuperar config/scripts.", _accent);
                        ReportSteamPboProgress(progress, 0.50d, progressName + " — recuperando config e scripts");
                        PboSourceRecoveryResult recovery = await RecoverProtectedPbo(pbo, tempRoot, progress, pboName, true);
                        ShowSteamDetailProgress(0, pboName + " — verificando integridade dos arquivos");
                        PboExtractionAuditResult audit = await Task.Run(delegate
                        {
                            return PboExtractionAuditor.Validate(recovery.ManifestPath, extractedRoot,
                                delegate(int percentage, string file)
                                {
                                    UpdateSteamDetailProgress(percentage,
                                        pboName + " — verificando " + file);
                                    ReportSteamPboProgress(progress, 0.54d + percentage * 0.0008d,
                                        progressName + " — verificando integridade");
                                });
                        });
                        Log(pboName + ": " + audit.Summary + ".", _success);
                        _steamImportVerifications.Add(pboName + ": " + audit.VerifiedFiles + "/" +
                            audit.PayloadFiles + " arquivos conferidos por SHA-1");
                        _steamImportVerifications.Add(pboName + ": v7 " + recovery.VerificationStatus +
                            " — scripts " + recovery.RecoveredScripts + ", P3D " + recovery.VerifiedP3ds + "/" +
                            recovery.P3dCount + ", configs " + recovery.VerifiedConfigs + "/" + recovery.ConfigCount);
                        await Task.Run(delegate { MergeRecoveredPboSource(recovery.SourceRoot, extractedRoot); });
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
                        Log(pboName + ": addon Python não conseguiu recuperar o config nesta tentativa; " +
                            "o conversor RaP interno ainda será testado. " + ex.Message, Color.Gold);
                    }
                }

                SourcePreparationResult preparation = await SourcePreparer.PrepareAsync(extractedRoot,
                    _settings.CfgConvertPath, LogLine, true);
                ReportSteamPboProgress(progress, 0.66d, progressName + " — source pré-auditada");
                Log("Source preparada (pré-auditoria): " + pboName + " — " + preparation.Summary,
                    preparation.IsComplete && preparation.Warnings.Count == 0 ? _success : Color.Gold);

                if (!pythonConfigRecoveryAttempted && SourcePreparer.HasUnrecoverableConfig(preparation) &&
                    File.Exists(_settings.PythonPath))
                {
                    pythonConfigRecoveryAttempted = true;
                    try
                    {
                        Log(pboName + ": config.bin permaneceu não editável após o conversor interno; " +
                            "acionando fallback do addon Python v7.", _accent);
                        ReportSteamPboProgress(progress, 0.70d, progressName + " — fallback de recuperação");
                        PboSourceRecoveryResult recovery = await RecoverProtectedPbo(pbo, tempRoot, progress, pboName, true);
                        ShowSteamDetailProgress(0, pboName + " — verificando integridade dos arquivos");
                        PboExtractionAuditResult audit = await Task.Run(delegate
                        {
                            return PboExtractionAuditor.ValidatePreparedSource(recovery.ManifestPath, extractedRoot,
                                delegate(int percentage, string file)
                                {
                                    UpdateSteamDetailProgress(percentage,
                                        pboName + " — verificando " + file);
                                    ReportSteamPboProgress(progress, 0.71d + percentage * 0.0006d,
                                        progressName + " — verificando integridade");
                                });
                        });
                        Log(pboName + ": " + audit.Summary + ".", _success);
                        _steamImportVerifications.Add(pboName + ": " + audit.AccountedFiles + "/" +
                            audit.PayloadFiles + " payloads contabilizados (" + audit.VerifiedFiles +
                            " SHA-1 exatos, " + audit.TransformedFiles + " transformações legítimas)");
                        _steamImportVerifications.Add(pboName + ": v7 " + recovery.VerificationStatus +
                            " — scripts " + recovery.RecoveredScripts + ", P3D " + recovery.VerifiedP3ds + "/" +
                            recovery.P3dCount + ", configs " + recovery.VerifiedConfigs + "/" + recovery.ConfigCount);
                        await Task.Run(delegate { MergeRecoveredPboSource(recovery.SourceRoot, extractedRoot); });
                        if (!string.IsNullOrWhiteSpace(recovery.Prefix)) prefix = recovery.Prefix;
                        foreach (string item in recovery.Warnings)
                        {
                            string warning = pboName + ": " + item;
                            _steamImportWarnings.Add(warning);
                            Log(warning, Color.Gold);
                        }
                        preparation = await SourcePreparer.PrepareAsync(extractedRoot,
                            _settings.CfgConvertPath, LogLine, true);
                        Log("Source reavaliada após fallback Python: " + pboName + " — " + preparation.Summary,
                            preparation.IsComplete && preparation.Warnings.Count == 0 ? _success : Color.Gold);
                    }
                    catch (Exception ex)
                    {
                        Log(pboName + ": fallback do addon Python para config.bin falhou; " +
                            "o original continuará preservado se a auditoria final não conseguir convertê-lo. " +
                            ex.Message, Color.Gold);
                    }
                }

                string preparedRoot = extractedRoot;
                OdolReconstructionResult reconstruction = null;
                if (preparation.OdolPreserved > 0)
                {
                    ReportSteamPboProgress(progress, 0.78d, progressName + " — reconstruindo modelos");
                    if (!File.Exists(_settings.PythonPath))
                    {
                        string warning = pboName + ": Python não configurado; modelos ODOL foram preservados.";
                        _steamImportWarnings.Add(warning);
                        Log(warning, Color.Gold);
                    }
                    else
                    {
                        bool showModelProgress = progress != null && _steamImportProgressActive;
                        try
                        {
                            if (showModelProgress)
                                ShowSteamDetailProgress(5, pboName + " — preparando reconstrução Python");
                            string converterPath = await EnsureOdolConverterAvailable();
                            if (showModelProgress)
                                UpdateSteamDetailProgress(15, pboName + " — reconstruindo e verificando modelos");
                            reconstruction = await OdolSourceReconstructor.ReconstructAsync(
                                extractedRoot, _settings.PythonPath, converterPath, LogLine,
                                delegate(int percentage, string stage)
                                {
                                    UpdateSteamDetailProgress(percentage, pboName + " — " + stage);
                                    ReportSteamPboProgress(progress, 0.78d + percentage * 0.0009d,
                                        progressName + " — reconstruindo modelos");
                                });
                            if (showModelProgress)
                                UpdateSteamDetailProgress(100, pboName + " — modelos verificados");
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
                        finally
                        {
                            if (showModelProgress) HideSteamDetailProgress();
                        }
                    }
                }

                ReportSteamPboProgress(progress, 0.88d, progressName + " — auditoria final");
                SourcePreparationResult finalPreparation = await ReauditAfterOdolAsync(
                    preparation, reconstruction, preparedRoot);
                foreach (string item in finalPreparation.Warnings)
                {
                    string warning = pboName + ": " + item;
                    _steamImportWarnings.Add(warning);
                    Log(warning, Color.Gold);
                }

                string sourceRoot = Path.Combine(projectRoot, "source");
                string destination = Path.Combine(sourceRoot, pboName);
                ReportSteamPboProgress(progress, 0.95d, progressName + " — gravando source");
                await Task.Run(delegate { ReplaceDirectoryByCopy(preparedRoot, destination); });
                if (!preparedRoot.Equals(extractedRoot, StringComparison.OrdinalIgnoreCase))
                    await Task.Run(delegate { TryDeleteDirectory(preparedRoot); });
                SaveProjectPrefix(projectRoot, pboName, prefix);
                ReportSteamPboProgress(progress, 0.98d, progressName + " — source pronta");
                Log((finalPreparation.IsComplete && finalPreparation.Warnings.Count == 0 ? "Extraído: " : "Extraído com pendências: ") + pboName +
                    " → " + destination + " (prefix=" + prefix + ")",
                    finalPreparation.IsComplete && finalPreparation.Warnings.Count == 0 ? _success : Color.Gold);
            }
            finally
            {
                UpdateSteamDetailProgress(99, pboName + " — limpando arquivos temporários");
                ReportSteamPboProgress(progress, 0.99d, progressName + " — limpando temporários");
                await Task.Run(delegate { TryDeleteDirectory(tempRoot); });
            }
        }

        private static bool IsProtectedPbo(string properties)
        {
            if (string.IsNullOrWhiteSpace(properties)) return false;
            return properties.IndexOf("obfuscat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                properties.IndexOf("PBO Tools", StringComparison.OrdinalIgnoreCase) >= 0 ||
                properties.IndexOf("pbo.tools", StringComparison.OrdinalIgnoreCase) >= 0 ||
                properties.IndexOf("MPG Packer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                properties.IndexOf("protected PBO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                properties.IndexOf("encrypted PBO", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<PboSourceRecoveryResult> RecoverProtectedPbo(string pboPath, string tempRoot,
            SteamImportProgressState importProgress = null, string pboName = null,
            bool deferSourceEditabilityWarnings = false)
        {
            bool showProgress = importProgress != null && _steamImportProgressActive;
            string displayName = string.IsNullOrWhiteSpace(pboName) ? Path.GetFileNameWithoutExtension(pboPath) : pboName;
            if (showProgress) ShowSteamDetailProgress(5, displayName + " — preparando verificação Python");
            try
            {
                if (!File.Exists(_settings.PythonPath))
                    throw new FileNotFoundException("Python não configurado; confira a aba Configurações.", _settings.PythonPath);
                if (!File.Exists(_settings.CfgConvertPath))
                    throw new FileNotFoundException("CfgConvert.exe não configurado; confira a aba Configurações.", _settings.CfgConvertPath);
                string converterPath = await EnsureOdolConverterAvailable();
                if (showProgress)
                    SetSteamDetailIndeterminate(displayName + " — recuperando e verificando payloads");
                string recoveryRoot = Path.Combine(tempRoot, "PboRecovery");
                PboSourceRecoveryResult recovery = await PboSourceReconstructor.RecoverAsync(pboPath, recoveryRoot,
                    _settings.PythonPath, converterPath, _settings.CfgConvertPath, LogLine);
                if (showProgress) UpdateSteamDetailProgress(82, displayName + " — validando relatórios");
                RemoveVerifiedConfigBins(recovery.SourceRoot);
                SourcePreparationResult preparation = await SourcePreparer.PrepareAsync(recovery.SourceRoot,
                    _settings.CfgConvertPath, LogLine, deferSourceEditabilityWarnings);
                recovery.Warnings.AddRange(preparation.Warnings);
                if (showProgress) UpdateSteamDetailProgress(100, displayName + " — verificação concluída");
                return recovery;
            }
            finally
            {
                if (showProgress) HideSteamDetailProgress();
            }
        }

        internal static void MergeRecoveredPboSource(string recoveredRoot, string extractedRoot)
        {
            string recoveredScripts = Path.Combine(recoveredRoot, "scripts");
            if (Directory.Exists(recoveredScripts))
            {
                string extractedScripts = Directory.GetDirectories(extractedRoot, "scripts", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault() ?? Path.Combine(extractedRoot, "scripts");
                Directory.CreateDirectory(extractedScripts);
                // The Python addon reconstructs protected scripts, while BankRev may already have
                // extracted other valid scripts. Overlay the recovered files instead of deleting
                // the complete tree, otherwise valid payloads that need no recovery are lost.
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
            _backupText.Text = _settings.BackupPath;
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
            _settings.BackupPath = _backupText.Text.Trim();
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
                message += "\n\nBancada de mods, chaves públicas e backups não foram alterados. A chave privada é gerenciada na pasta keys do Workbench. Clique em Salvar configurações para confirmar.";

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
                UpdateProjectActionAvailability();
                SetStatus("Bancada não encontrada");
                return;
            }

            string[] projectDirectories = Directory.GetDirectories(_settings.BenchPath);
            SynchronizeProjectConfigurations(projectDirectories);
            foreach (string directory in projectDirectories.OrderBy(x => Path.GetFileName(x), StringComparer.CurrentCultureIgnoreCase))
            {
                string name = Path.GetFileName(directory);
                try
                {
                    LoadProjectConfiguration(directory);
                }
                catch (Exception ex)
                {
                    Log("Não foi possível migrar a configuração do projeto " + name + ": " + ex.Message,
                        Color.Gold);
                }
                _projectCombo.Items.Add(new PathItem { Name = name, FullPath = directory });
            }

            int selected = -1;
            for (int i = 0; i < _projectCombo.Items.Count; i++)
            {
                PathItem item = (PathItem)_projectCombo.Items[i];
                if (string.Equals(item.FullPath, previous, StringComparison.OrdinalIgnoreCase)) selected = i;
            }
            if (_projectCombo.Items.Count > 0) _projectCombo.SelectedIndex = selected >= 0 ? selected : 0;
            UpdateProjectActionAvailability();
            SetStatus(_projectCombo.Items.Count + " projeto(s) encontrado(s)");
        }

        private void SynchronizeProjectConfigurations(IEnumerable<string> projectDirectories)
        {
            string configsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs");
            if (!Directory.Exists(configsRoot)) return;

            HashSet<string> projects = new HashSet<string>(projectDirectories.Select(Path.GetFileName),
                StringComparer.OrdinalIgnoreCase);
            string safeRoot = Path.GetFullPath(configsRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string directory in Directory.GetDirectories(configsRoot, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(directory);
                if (projects.Contains(name)) continue;
                try
                {
                    string fullPath = Path.GetFullPath(directory).TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("A pasta de configuração aponta para fora de configs.");
                    Directory.Delete(directory, true);
                    Log("Configuração removida porque o projeto não existe mais: " + name, Color.Silver);
                }
                catch (Exception ex)
                {
                    Log("Não foi possível remover a configuração antiga de " + name + ": " + ex.Message,
                        Color.Gold);
                }
            }
        }

        private PathItem SelectedProject { get { return _projectCombo.SelectedItem as PathItem; } }
        private PathItem SelectedPbo { get { return _pboList.SelectedItem as PathItem; } }
        private PathItem SelectedSource { get { return _sourceCombo.SelectedItem as PathItem; } }

        private void RefreshProjectContents()
        {
            _pboList.Items.Clear();
            _projectPbos.Clear();
            _sourceCombo.Items.Clear();
            PathItem project = SelectedProject;
            if (project == null)
            {
                UpdateProjectActionAvailability();
                return;
            }

            IEnumerable<string> pbos = Directory.GetFiles(project.FullPath, "*.pbo", SearchOption.AllDirectories)
                .Where(path => !IsDerivedOrSourcePath(project.FullPath, path))
                .OrderBy(path => RelativePath(project.FullPath, path), StringComparer.CurrentCultureIgnoreCase);
            foreach (string pbo in pbos)
                _projectPbos.Add(new PathItem { Name = RelativePath(project.FullPath, pbo), FullPath = pbo });
            RefreshPboList();

            string sourceRoot = Path.Combine(project.FullPath, "source");
            if (Directory.Exists(sourceRoot))
            {
                foreach (string source in Directory.GetDirectories(sourceRoot).OrderBy(x => Path.GetFileName(x), StringComparer.CurrentCultureIgnoreCase))
                    _sourceCombo.Items.Add(new PathItem { Name = Path.GetFileName(source), FullPath = source });
            }
            if (_sourceCombo.Items.Count > 0) _sourceCombo.SelectedIndex = 0;
            UpdateProjectActionAvailability();
            SetStatus(project.Name + ": " + _projectPbos.Count + " PBO(s), " + _sourceCombo.Items.Count + " source(s)");
        }

        private void RefreshPboList()
        {
            if (_pboList == null) return;
            HashSet<string> selectedPaths = new HashSet<string>(
                _pboList.SelectedItems.Cast<PathItem>().Select(item => item.FullPath),
                StringComparer.OrdinalIgnoreCase);
            string filter = _pboSearch == null ? string.Empty : _pboSearch.Text.Trim();

            _pboList.BeginUpdate();
            try
            {
                _pboList.Items.Clear();
                foreach (PathItem item in _projectPbos.Where(candidate =>
                    string.IsNullOrWhiteSpace(filter) ||
                    candidate.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0))
                {
                    int index = _pboList.Items.Add(item);
                    if (selectedPaths.Contains(item.FullPath)) _pboList.SetSelected(index, true);
                }

                if (_pboList.SelectedItems.Count == 0 && _pboList.Items.Count > 0)
                    _pboList.SelectedIndex = 0;
                if (_pboList.Items.Count > 0) _pboList.TopIndex = 0;
            }
            finally
            {
                _pboList.EndUpdate();
            }
            UpdateProjectActionAvailability();
        }

        private void UpdateProjectActionAvailability()
        {
            bool hasProject = SelectedProject != null && Directory.Exists(SelectedProject.FullPath);
            bool hasPbo = hasProject && SelectedPbo != null && File.Exists(SelectedPbo.FullPath);
            bool hasSource = hasProject && SelectedSource != null && Directory.Exists(SelectedSource.FullPath);

            if (_openProjectButton != null) _openProjectButton.Enabled = !_busy && hasProject;
            if (_pboActionsGroup != null) _pboActionsGroup.Enabled = !_busy && hasProject;
            if (_extractPboButton != null) _extractPboButton.Enabled = !_busy && hasPbo;
            if (_buildActionsGroup != null) _buildActionsGroup.Enabled = !_busy && hasSource;
            SetAvailabilityButtonColor(_extractPboButton, hasPbo, _accent);
            SetAvailabilityButtonColor(_buildSignButton, hasSource, _success);
            SetAvailabilityButtonColor(_buildAllButton, hasSource, _accent);
        }

        private void SetAvailabilityButtonColor(Button button, bool available, Color normalColor)
        {
            if (button == null || _busy) return;
            button.UseVisualStyleBackColor = false;
            button.BackColor = available ? normalColor : _field;
        }

        private static bool IsDerivedOrSourcePath(string projectRoot, string path)
        {
            string relative = RelativePath(projectRoot, path).Replace('/', '\\').ToLowerInvariant();
            return relative.StartsWith("source\\") || relative.StartsWith("_test\\");
        }

        private async Task ExtractSelectedPbo()
        {
            PathItem project = SelectedProject;
            List<PathItem> pbos = _pboList.SelectedItems.Cast<PathItem>().ToList();
            if (project == null || pbos.Count == 0)
            {
                MessageBox.Show(this, "Selecione um projeto e pelo menos um PBO.", "Extração",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!File.Exists(_settings.BankRevPath))
            {
                MessageBox.Show(this, "BankRev.exe não foi encontrado. Confira Configurações.",
                    "Extração", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<string> selectedPboPaths = pbos.Select(item => item.FullPath).ToList();
            int selectedPboTopIndex = _pboList.TopIndex;
            int existingSources = pbos.Count(item =>
            {
                string sourceName = Path.GetFileNameWithoutExtension(item.FullPath);
                string sourcePath = Path.Combine(project.FullPath, "source", sourceName);
                return Directory.Exists(sourcePath) && Directory.EnumerateFileSystemEntries(sourcePath).Any();
            });
            if (existingSources > 0)
            {
                string question = existingSources == 1 && pbos.Count == 1
                    ? "O source já existe. Deseja substituí-lo pela nova extração?"
                    : existingSources + " source(s) da seleção já existem. Deseja substituí-los durante a fila?";
                DialogResult answer = MessageBox.Show(this, question, "Source existente",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }

            SteamImportProgressState progress = new SteamImportProgressState
            {
                TotalPbos = pbos.Count,
                Extract = true,
                IncludesCopy = false
            };
            try
            {
                SetBusy(true, "Extraindo fila de " + pbos.Count + " PBO(s)");
                BeginSteamImportProgress(true, pbos.Count,
                    pbos.Count == 1
                        ? "Progresso da extração do PBO selecionado"
                        : "Progresso da fila de extração");
                _steamImportWarnings.Clear();
                _steamImportVerifications.Clear();
                List<string> extractedSources = new List<string>();
                List<string> failedPbos = new List<string>();
                string lastSourceName = null;
                SetSteamOverallProgress(0, "Fila preparada — " + pbos.Count + " PBO(s)");

                foreach (PathItem pbo in pbos)
                {
                    string name = Path.GetFileNameWithoutExtension(pbo.FullPath);
                    string destination = Path.Combine(project.FullPath, "source", name);
                    lastSourceName = name;
                    ResetSteamDetailProgress(name);
                    ReportSteamPboProgress(progress, 0d, name + " — iniciando extração");
                    try
                    {
                        await ExtractImportedPbo(project.FullPath, pbo.FullPath, progress, project.Name);
                        if (Directory.Exists(destination) &&
                            Directory.EnumerateFileSystemEntries(destination).Any())
                            extractedSources.Add(destination);
                        else
                            failedPbos.Add(Path.GetFileName(pbo.FullPath));
                    }
                    catch (Exception ex)
                    {
                        failedPbos.Add(Path.GetFileName(pbo.FullPath));
                        string warning = Path.GetFileName(pbo.FullPath) + ": falha na extração; " + ex.Message;
                        _steamImportWarnings.Add(warning);
                        Log(warning, Color.Gold);
                        DiagnosticLog("Falha isolada na fila de extração: " + ex);
                    }
                    finally
                    {
                        CompleteSteamPboProgress(progress, name + " concluído");
                    }
                }

                if (extractedSources.Count > 0) MoveProjectMetadataToSource(project.FullPath);
                SetSteamOverallProgress(100, _steamImportWarnings.Count == 0 && failedPbos.Count == 0
                    ? "Fila de extração concluída"
                    : "Fila concluída com pendências");

                RefreshProjectContents();
                RestorePboSelection(selectedPboPaths, selectedPboTopIndex);
                if (!string.IsNullOrWhiteSpace(lastSourceName)) SelectSourceByName(lastSourceName);
                string verifications = _steamImportVerifications.Count == 0
                    ? string.Empty
                    : "\n\nVerificações de integridade:\n✓ " +
                        string.Join("\n✓ ", _steamImportVerifications.ToArray());
                string warnings = _steamImportWarnings.Count == 0
                    ? string.Empty
                    : "\n\nAvisos:\n• " + string.Join("\n• ", _steamImportWarnings.ToArray());
                string failures = failedPbos.Count == 0
                    ? string.Empty
                    : "\n\nSem source utilizável:\n• " + string.Join("\n• ", failedPbos.ToArray());
                string message = "Fila processada pelo mesmo fluxo da importação Steam.\n\n" +
                    "Sources produzidas: " + extractedSources.Count + "/" + pbos.Count;
                bool completedCleanly = failedPbos.Count == 0 && _steamImportWarnings.Count == 0;
                MessageBox.Show(this, message + verifications + warnings + failures,
                    completedCleanly ? "Sources extraídas e preparadas" : "Fila concluída com pendências",
                    MessageBoxButtons.OK,
                    completedCleanly ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ShowError("Falha ao processar a fila de extração.", ex);
            }
            finally
            {
                EndSteamImportProgress();
                SetBusy(false, "Pronto");
                RestorePboSelection(selectedPboPaths, selectedPboTopIndex);
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(delegate
                    {
                        RestorePboSelection(selectedPboPaths, selectedPboTopIndex);
                    }));
                }
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
                    _settings.CfgConvertPath, LogLine, true);

                string converterPath = preparation.OdolPreserved > 0
                    ? await EnsureOdolConverterAvailable()
                    : _settings.OdolConverterPath;
                OdolReconstructionResult reconstruction = await OdolSourceReconstructor.ReconstructAsync(source.FullPath,
                    _settings.PythonPath, converterPath, LogLine);
                SourcePreparationResult finalPreparation = await ReauditAfterOdolAsync(
                    preparation, reconstruction, reconstruction.Changed ? reconstruction.OutputRoot : source.FullPath);
                if (reconstruction.Changed)
                    ReplaceDirectoryByMove(reconstruction.OutputRoot, source.FullPath);

                Log("Source preparada: " + source.FullPath + " — " + preparation.Summary + " " + reconstruction.Summary +
                    (ReferenceEquals(finalPreparation, preparation) ? string.Empty : " Auditoria final: " + finalPreparation.Summary), _success);
                foreach (string warning in finalPreparation.Warnings) Log(warning, Color.Gold);
                foreach (string warning in reconstruction.Warnings) Log(warning, Color.Gold);
                List<string> allWarnings = new List<string>(finalPreparation.Warnings);
                allWarnings.AddRange(reconstruction.Warnings);
                string warningText = allWarnings.Count == 0
                    ? string.Empty
                    : "\n\nAvisos:\n• " + string.Join("\n• ", allWarnings.ToArray());
                MessageBox.Show(this, preparation.Summary + "\n" + reconstruction.Summary +
                    (ReferenceEquals(finalPreparation, preparation) ? string.Empty : "\nAuditoria final: " + finalPreparation.Summary) + warningText,
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
            BuildProgressState progress = new BuildProgressState { TotalSources = 1 };
            try
            {
                SetBusy(true, "Compilando " + source.Name);
                BeginBuildProgress(1, sign);
                bool ok = await BuildSource(source, sign, false, progress);
                if (!ok) return;
                RefreshProjectContents();
                MessageBox.Show(this, "Build concluído com sucesso.", "Compilação",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                EndSteamImportProgress();
                SetBusy(false, "Pronto");
            }
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
                BeginBuildProgress(sources.Count, true);
                BuildProgressState progress = new BuildProgressState { TotalSources = sources.Count };
                foreach (PathItem source in sources)
                {
                    bool ok = await BuildSource(source, true, false, progress);
                    if (!ok) return;
                }
                RefreshProjectContents();
                MessageBox.Show(this, "Todos os sources foram compilados e assinados.", "Compilação", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                EndSteamImportProgress();
                SetBusy(false, "Pronto");
            }
        }

        private async Task<bool> BuildSource(PathItem source, bool sign, bool manageBusy,
            BuildProgressState progress = null)
        {
            PathItem project = SelectedProject;
            if (project == null) return false;
            string prefix = _prefixText.Text.Trim();
            if (source != SelectedSource) prefix = LoadProjectPrefix(project.FullPath, source.Name);
            if (string.IsNullOrWhiteSpace(prefix)) prefix = source.Name;
            prefix = prefix.TrimEnd('\\', '/');
            if (!File.Exists(_settings.AddonBuilderPath))
            {
                MessageBox.Show(this, "Configure o AddonBuilder.exe para binarizar e empacotar o source.",
                    "Compilação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string privateKeyPath = _privateKeyText.Text.Trim();
            if (sign && !PrivateKeyStore.IsStoredPrivateKey(privateKeyPath))
            {
                MessageBox.Show(this, "Adicione uma chave .biprivatekey à pasta keys do Workbench.", "Assinatura", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string tempRoot = Program.CreateTemporaryPath("Build");
            string tempOut = Path.Combine(tempRoot, "out");
            Directory.CreateDirectory(tempOut);
            try
            {
                if (manageBusy) SetBusy(true, "Compilando " + source.Name);
                if (progress != null)
                {
                    progress.CurrentSource = source.Name;
                    progress.SyncedFiles = 0;
                    progress.ExpectedSyncFiles = CountAddonBuilderDirectFiles(source.FullPath);
                    ReportBuildSourceProgress(progress, 2, "preparando source");
                }
                Log("Compilando source: " + source.FullPath, _accent);
                string temporaryProject = Path.Combine(tempRoot, "project");
                ReportBuildSourceProgress(progress, 5, "montando projeto temporário");
                string stagedSource = StageSourceForAddonBuilder(source.FullPath, temporaryProject, prefix);
                ReportBuildSourceProgress(progress, 7, "binarizando materiais RVMAT");
                await BinarizeStagedRvmats(stagedSource, progress);
                string includeFile = CreateAddonBuilderIncludeFile(tempRoot, stagedSource);
                string arguments = ProcessRunner.Quote(stagedSource) + " " + ProcessRunner.Quote(tempOut) +
                    " -clear -prefix=" + ProcessRunner.Quote(prefix) +
                    " -project=" + ProcessRunner.Quote(temporaryProject) +
                    " -temp=" + ProcessRunner.Quote(Path.Combine(tempRoot, "binarized")) +
                    " -include=" + ProcessRunner.Quote(includeFile) + " -binarizeFullLogs";
                ProcessResult result = await ProcessRunner.RunAsync(_settings.AddonBuilderPath, arguments,
                    temporaryProject, delegate(string line)
                    {
                        LogLine(line);
                        ReportAddonBuilderProgress(progress, line);
                    });
                if (result.ExitCode != 0 || result.Output.IndexOf("Build Successful", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException("Addon Builder não concluiu a binarização e o empacotamento do source.");

                string expected = Path.Combine(tempOut, source.Name + ".pbo");
                string builtPbo = File.Exists(expected) ? expected : Directory.GetFiles(tempOut, "*.pbo").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (builtPbo == null) throw new InvalidOperationException("A ferramenta de compilação não criou um PBO.");
                FileInfo info = new FileInfo(builtPbo);
                if (info.Length < 128) throw new InvalidOperationException("O PBO criado parece vazio (" + info.Length + " bytes).");

                ReportBuildSourceProgress(progress, 93, "validando conteúdo do PBO");
                ProcessResult list = await ProcessRunner.RunAsync(_settings.BankRevPath, "-l " + ProcessRunner.Quote(builtPbo), tempOut, LogLine);
                if (list.ExitCode != 0 || string.IsNullOrWhiteSpace(list.Output)) throw new InvalidOperationException("Não foi possível validar o conteúdo do PBO criado.");
                ValidateBuiltPboContents(source.FullPath, list.Output);

                string signature = null;
                if (sign)
                {
                    ReportBuildSourceProgress(progress, 96, "assinando PBO");
                    ProcessResult signed = await ProcessRunner.RunAsync(_settings.SignerPath,
                        ProcessRunner.Quote(privateKeyPath) + " " + ProcessRunner.Quote(builtPbo), tempOut, LogLine);
                    if (signed.ExitCode != 0) throw new InvalidOperationException("DSSignFile terminou com código " + signed.ExitCode);
                    signature = Directory.GetFiles(tempOut, Path.GetFileName(builtPbo) + ".*.bisign").FirstOrDefault();
                    if (signature == null) throw new InvalidOperationException("A assinatura BISIGN não foi criada.");
                }

                ReportBuildSourceProgress(progress, 99, "instalando resultado no projeto");
                string outputFolder = Path.Combine(project.FullPath, "PBO");
                Directory.CreateDirectory(outputFolder);
                string destinationPboName = source.Name + ".pbo";
                string destinationPbo = Path.Combine(outputFolder, destinationPboName);
                File.Copy(builtPbo, destinationPbo, true);
                foreach (string oldSignature in Directory.GetFiles(outputFolder,
                    destinationPboName + ".*.bisign"))
                    File.Delete(oldSignature);
                if (signature != null)
                {
                    string builtPboName = Path.GetFileName(builtPbo);
                    string signatureName = Path.GetFileName(signature);
                    string signatureSuffix = signatureName.StartsWith(builtPboName, StringComparison.OrdinalIgnoreCase)
                        ? signatureName.Substring(builtPboName.Length)
                        : "." + signatureName;
                    string destinationSignature = Path.Combine(outputFolder, destinationPboName + signatureSuffix);
                    File.Copy(signature, destinationSignature, true);
                    signature = destinationSignature;
                }
                RemoveObsoleteCompilerAlias(project.FullPath, outputFolder,
                    Path.GetFileName(builtPbo), destinationPboName);

                SaveProjectPrefix(project.FullPath, source.Name, prefix);
                Log("PBO pronto: " + destinationPbo + " (" + new FileInfo(destinationPbo).Length + " bytes)", _success);
                if (signature != null) Log("BISIGN pronta: " + Path.GetFileName(signature), _success);
                CompleteBuildSourceProgress(progress);
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

        private static string StageSourceForAddonBuilder(string sourceRoot, string temporaryProject, string prefix)
        {
            string projectRoot = Path.GetFullPath(temporaryProject).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string relativePrefix = prefix.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
            string staged = Path.GetFullPath(Path.Combine(projectRoot, relativePrefix));
            if (!staged.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Prefixo do PBO aponta para fora do projeto temporário: " + prefix);

            Directory.CreateDirectory(staged);
            CopyDirectoryContents(sourceRoot, staged);
            foreach (string file in Directory.GetFiles(staged, "*", SearchOption.AllDirectories)
                .Where(IsBuildAuxiliaryFile).ToArray())
                File.Delete(file);
            return staged;
        }

        private static string CreateAddonBuilderIncludeFile(string tempRoot, string sourceRoot)
        {
            string includeFile = Path.Combine(tempRoot, "addon_builder_include.txt");
            string[] patterns = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(IsDirectBuildPayload)
                .Select(file => string.IsNullOrWhiteSpace(Path.GetExtension(file))
                    ? Path.GetFileName(file) : "*" + Path.GetExtension(file))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(pattern => pattern, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (patterns.Length == 0) patterns = new[] { "*.dayzworkbench_no_direct_payload" };
            File.WriteAllText(includeFile, string.Join(";", patterns), new UTF8Encoding(false));
            return includeFile;
        }

        private static int CountAddonBuilderDirectFiles(string sourceRoot)
        {
            return Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Count(IsDirectBuildPayload);
        }

        private static bool IsBuildAuxiliaryFile(string file)
        {
            string name = Path.GetFileName(file);
            if (BuildAuxiliaryFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            return BuildAuxiliaryFileSuffixes.Any(suffix =>
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDirectBuildPayload(string file)
        {
            if (IsBuildAuxiliaryFile(file)) return false;
            string extension = Path.GetExtension(file);
            if (string.IsNullOrWhiteSpace(extension)) return true;
            return !AddonBuilderProcessedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private async Task BinarizeStagedRvmats(string stagedSource, BuildProgressState progress)
        {
            string[] materials = Directory.GetFiles(stagedSource, "*.rvmat", SearchOption.AllDirectories);
            string[] editableMaterials = materials.Where(file => !IsBinarizedRap(file)).ToArray();
            if (editableMaterials.Length == 0) return;
            if (!File.Exists(_settings.CfgConvertPath))
                throw new FileNotFoundException("CfgConvert.exe não encontrado para binarizar os materiais RVMAT.",
                    _settings.CfgConvertPath);

            for (int index = 0; index < editableMaterials.Length; index++)
            {
                string material = editableMaterials[index];
                string temporary = material + ".dayzworkbench.bin";
                try
                {
                    ReportBuildSourceProgress(progress, 7 + (int)Math.Round((index + 1) * 3d /
                        editableMaterials.Length), "binarizando " + Path.GetFileName(material));
                    ProcessResult result = await ProcessRunner.RunAsync(_settings.CfgConvertPath,
                        "-bin -dst " + ProcessRunner.Quote(temporary) + " " + ProcessRunner.Quote(material),
                        stagedSource, LogLine);
                    if (result.ExitCode != 0 || !File.Exists(temporary) || !IsBinarizedRap(temporary))
                        throw new InvalidOperationException("CfgConvert não conseguiu binarizar o material " +
                            RelativePath(stagedSource, material) + ".");
                    File.Copy(temporary, material, true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        private static bool IsBinarizedRap(string file)
        {
            try
            {
                byte[] signature = new byte[4];
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return stream.Read(signature, 0, signature.Length) == signature.Length &&
                        signature[0] == 0 && signature[1] == (byte)'r' &&
                        signature[2] == (byte)'a' && signature[3] == (byte)'P';
            }
            catch
            {
                return false;
            }
        }

        private static void RemoveObsoleteCompilerAlias(string projectRoot, string outputFolder,
            string compilerPboName, string destinationPboName)
        {
            if (compilerPboName.Equals(destinationPboName, StringComparison.OrdinalIgnoreCase)) return;

            string aliasSourceName = Path.GetFileNameWithoutExtension(compilerPboName);
            if (Directory.Exists(Path.Combine(projectRoot, "source", aliasSourceName))) return;

            string obsoletePbo = Path.Combine(outputFolder, compilerPboName);
            if (File.Exists(obsoletePbo)) File.Delete(obsoletePbo);
            foreach (string signature in Directory.GetFiles(outputFolder, compilerPboName + ".*.bisign"))
                File.Delete(signature);
        }

        private static void ValidateBuiltPboContents(string sourceRoot, string bankRevOutput)
        {
            HashSet<string> packed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in bankRevOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string entry = NormalizePboEntry(line);
                if (!string.IsNullOrWhiteSpace(entry)) packed.Add(entry);
            }

            List<string> expected = new List<string>();
            foreach (string file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                if (IsBuildAuxiliaryFile(file)) continue;
                string relative = NormalizePboEntry(RelativePath(sourceRoot, file));
                string name = Path.GetFileName(file);
                if (name.Equals("config.cpp", StringComparison.OrdinalIgnoreCase))
                    expected.Add(NormalizePboEntry(Path.ChangeExtension(relative, ".bin")));
                else if (Path.GetExtension(file).Equals(".p3d", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetExtension(file).Equals(".rvmat", StringComparison.OrdinalIgnoreCase) ||
                         IsDirectBuildPayload(file))
                    expected.Add(relative);
            }

            string[] missing = expected.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(entry => !packed.Contains(entry)).ToArray();
            if (missing.Length > 0)
            {
                string sample = string.Join("\n• ", missing.Take(12).ToArray());
                throw new InvalidOperationException("O Addon Builder criou um PBO incompleto: " + missing.Length +
                    " arquivo(s) do source não foram empacotados.\n• " + sample);
            }
        }

        private static string NormalizePboEntry(string value)
        {
            return (value ?? string.Empty).Trim().TrimStart('\\', '/').Replace('/', '\\');
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

            CopyIfExists(FindProjectMetadata(project.FullPath, "mod.cpp"), Path.Combine(modRoot, "mod.cpp"));
            CopyIfExists(FindProjectMetadata(project.FullPath, "meta.cpp"), Path.Combine(modRoot, "meta.cpp"));
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

        private static readonly object PersistentLogSync = new object();

        private static void AppendPersistentLog(string category, string message)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]" +
                    (string.IsNullOrWhiteSpace(category) ? string.Empty : " [" + category + "]") +
                    " " + message + Environment.NewLine;
                lock (PersistentLogSync)
                {
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workbench.log"), line);
                }
            }
            catch { }
        }

        private static void DiagnosticLog(string message)
        {
            AppendPersistentLog("DIAG", message);
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
            string projectName = new DirectoryInfo(projectRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "configs", projectName, ".dayzworkbench.ini");
        }

        private static string LegacyProjectConfigPath(string projectRoot)
        {
            return Path.Combine(projectRoot, ".dayzworkbench.ini");
        }

        private static Dictionary<string, string> ReadProjectConfigurationFile(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return values;
            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                values[line.Substring(0, separator)] = line.Substring(separator + 1);
            }
            return values;
        }

        private static Dictionary<string, string> LoadProjectConfiguration(string projectRoot)
        {
            string path = ProjectConfigPath(projectRoot);
            string legacyPath = LegacyProjectConfigPath(projectRoot);
            Dictionary<string, string> values = ReadProjectConfigurationFile(legacyPath);
            foreach (KeyValuePair<string, string> item in ReadProjectConfigurationFile(path))
                values[item.Key] = item.Value;

            if (File.Exists(legacyPath))
            {
                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                File.WriteAllLines(path, values.OrderBy(x => x.Key)
                    .Select(x => x.Key + "=" + x.Value).ToArray());
                File.Delete(legacyPath);
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
            string path = ProjectConfigPath(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, values.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value).ToArray());
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

        private void RestorePboSelection(IEnumerable<string> fullPaths, int topIndex)
        {
            HashSet<string> selectedPaths = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
            _pboList.BeginUpdate();
            try
            {
                _pboList.ClearSelected();
                for (int i = 0; i < _pboList.Items.Count; i++)
                {
                    PathItem item = (PathItem)_pboList.Items[i];
                    if (selectedPaths.Contains(item.FullPath)) _pboList.SetSelected(i, true);
                }
                if (_pboList.Items.Count > 0)
                    _pboList.TopIndex = Math.Max(0, Math.Min(topIndex, _pboList.Items.Count - 1));
            }
            finally
            {
                _pboList.EndUpdate();
            }
        }

        private void SetBusy(bool busy, string message)
        {
            if (busy && !_busy) MuteButtonColors();
            _busy = busy;
            foreach (Control control in _operationControls) control.Enabled = !busy;
            if (_tabs != null) _tabs.Enabled = !busy;
            if (_projectCombo != null) _projectCombo.Enabled = !busy;
            if (!busy)
            {
                RestoreButtonColors();
                RefreshWorkDriveButton();
                UpdateProjectActionAvailability();
            }
            UseWaitCursor = busy;
            SetStatus(message);
        }

        private void MuteButtonColors()
        {
            _busyButtonColors.Clear();
            List<Button> buttons = new List<Button>();
            CollectButtons(this, buttons);
            foreach (Button button in buttons)
            {
                _busyButtonColors[button] = button.BackColor;
                button.UseVisualStyleBackColor = false;
                button.BackColor = _field;
            }
        }

        private void RestoreButtonColors()
        {
            foreach (KeyValuePair<Button, Color> item in _busyButtonColors)
            {
                if (!item.Key.IsDisposed) item.Key.BackColor = item.Value;
            }
            _busyButtonColors.Clear();
        }

        private static void CollectButtons(Control root, List<Button> buttons)
        {
            foreach (Control control in root.Controls)
            {
                Button button = control as Button;
                if (button != null) buttons.Add(button);
                if (control.HasChildren) CollectButtons(control, buttons);
            }
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
            AppendPersistentLog("OP", text);
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

        private static void PlaceImportedMetadata(string importedModRoot, string projectRoot, bool extract)
        {
            if (!extract)
            {
                CopyIfExists(Path.Combine(importedModRoot, "mod.cpp"), Path.Combine(projectRoot, "mod.cpp"));
                CopyIfExists(Path.Combine(importedModRoot, "meta.cpp"), Path.Combine(projectRoot, "meta.cpp"));
                return;
            }

            string sourceRoot = Path.Combine(projectRoot, "source");
            Directory.CreateDirectory(sourceRoot);
            foreach (string fileName in new[] { "mod.cpp", "meta.cpp" })
            {
                string imported = Path.Combine(importedModRoot, fileName);
                string legacy = Path.Combine(projectRoot, fileName);
                string destination = Path.Combine(sourceRoot, fileName);
                if (File.Exists(imported))
                    File.Copy(imported, destination, true);
                else if (File.Exists(legacy))
                    File.Copy(legacy, destination, true);
                if (File.Exists(legacy)) File.Delete(legacy);
            }
        }

        private static void MoveProjectMetadataToSource(string projectRoot)
        {
            string sourceRoot = Path.Combine(projectRoot, "source");
            Directory.CreateDirectory(sourceRoot);
            foreach (string fileName in new[] { "mod.cpp", "meta.cpp" })
            {
                string legacy = Path.Combine(projectRoot, fileName);
                string destination = Path.Combine(sourceRoot, fileName);
                if (File.Exists(legacy))
                {
                    File.Copy(legacy, destination, true);
                    File.Delete(legacy);
                }
            }
        }

        private static string FindProjectMetadata(string projectRoot, string fileName)
        {
            string sourcePath = Path.Combine(projectRoot, "source", fileName);
            return File.Exists(sourcePath) ? sourcePath : Path.Combine(projectRoot, fileName);
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
