using System;
using System.IO;
using System.Windows.Forms;

namespace DayZModWorkbench
{
    internal static class Program
    {
        internal static string CreateTemporaryPath(string operation)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DayZ Mod Workbench", operation + "_" + Guid.NewGuid().ToString("N"));
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args != null && Array.IndexOf(args, "--test-key-store") >= 0)
            {
                try
                {
                    ToolSettings settings = ToolSettings.Load();
                    settings.PrivateKeyPath = PrivateKeyStore.EnsureLocal(settings.PrivateKeyPath);
                    settings.Save();
                    Environment.ExitCode = PrivateKeyStore.IsStoredPrivateKey(settings.PrivateKeyPath) ? 0 : 1;
                }
                catch
                {
                    Environment.ExitCode = 1;
                }
                return;
            }
            if (args != null && Array.IndexOf(args, "--test-odol-addon") >= 0)
            {
                try
                {
                    ToolSettings settings = ToolSettings.Load();
                    string diagnosticDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DayZ Mod Workbench");

                    Directory.CreateDirectory(diagnosticDir);

                    string diagnosticPath = Path.Combine(
                        diagnosticDir, "odol-addon-test-error.txt");

                    File.WriteAllText(diagnosticPath,
                        "=== TESTE DO UPDATER ODOL ===" + Environment.NewLine);

                    Action<string> testLog = delegate(string message)
                    {
                        File.AppendAllText(
                            diagnosticPath,
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                            "  " + message + Environment.NewLine);
                    };

                    string path = OdolConverterProvisioner.EnsureAvailableAsync(
                        settings.OdolConverterPath, testLog).GetAwaiter().GetResult();

                    bool testSucceeded =
                        File.Exists(path) &&
                        Path.GetFullPath(path).Equals(
                            Path.GetFullPath(OdolConverterProvisioner.ManagedAddonPath),
                            StringComparison.OrdinalIgnoreCase);

                    Environment.ExitCode = testSucceeded ? 0 : 1;

                    if (testSucceeded)
                    {
                        try
                        {
                            if (File.Exists(diagnosticPath))
                                File.Delete(diagnosticPath);
                        }
                        catch { }

                        try
                        {
                            string updateRoot = Path.Combine(
                                diagnosticDir, "updates");

                            if (Directory.Exists(updateRoot) &&
                                Directory.GetFileSystemEntries(updateRoot).Length == 0)
                            {
                                Directory.Delete(updateRoot, false);
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    string diagnosticDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DayZ Mod Workbench");

                    Directory.CreateDirectory(diagnosticDir);

                    File.AppendAllText(
                        Path.Combine(diagnosticDir, "odol-addon-test-error.txt"),
                        Environment.NewLine +
                        "=== EXCEÇÃO ===" + Environment.NewLine +
                        ex.ToString() + Environment.NewLine);

                    Environment.ExitCode = 1;
                }
                return;
            }
            string pboRecoveryTest = args == null ? null : Array.Find(args,
                value => value.StartsWith("--test-pbo-recovery=", StringComparison.OrdinalIgnoreCase));
            if (pboRecoveryTest != null)
            {
                string pboPath = pboRecoveryTest.Substring("--test-pbo-recovery=".Length).Trim('"');
                string outputRoot = CreateTemporaryPath("PboRecoveryTest");
                try
                {
                    ToolSettings settings = ToolSettings.Load();
                    string converter = OdolConverterProvisioner.EnsureAvailableAsync(
                        settings.OdolConverterPath, null).GetAwaiter().GetResult();
                    PboSourceRecoveryResult recovery = PboSourceReconstructor.RecoverAsync(pboPath, outputRoot,
                        settings.PythonPath, converter, settings.CfgConvertPath, null).GetAwaiter().GetResult();
                    SourcePreparer.PrepareAsync(recovery.SourceRoot, settings.CfgConvertPath, null)
                        .GetAwaiter().GetResult();
                    Environment.ExitCode = recovery.RecoveredFiles > 0 &&
                        (File.Exists(Path.Combine(recovery.SourceRoot, "config.cpp")) ||
                         File.Exists(Path.Combine(recovery.SourceRoot, "config.bin"))) ? 0 : 1;
                }
                catch
                {
                    Environment.ExitCode = 1;
                }
                finally
                {
                    try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); } catch { }
                }
                return;
            }
            if (args != null && Array.IndexOf(args, "--test-workdrive-state") >= 0)
            {
                ToolSettings settings = ToolSettings.Load();
                WorkDriveState state = WorkDriveManager.GetState(settings.ProjectDrivePath);
                string executable = WorkDriveManager.FindExecutable(settings.AddonBuilderPath,
                    settings.BankRevPath, settings.CfgConvertPath);
                Environment.ExitCode = state.IsAvailable && WorkDriveManager.IsManagedMapping(executable, state) ? 0 : 1;
                return;
            }
            string pboAuditTest = args == null ? null : Array.Find(args,
                value => value.StartsWith("--test-pbo-audit=", StringComparison.OrdinalIgnoreCase));
            if (pboAuditTest != null)
            {
                string value = pboAuditTest.Substring("--test-pbo-audit=".Length).Trim('"');
                int separator = value.IndexOf('|');
                try
                {
                    if (separator <= 0 || separator == value.Length - 1)
                        throw new ArgumentException("Informe manifesto|pasta_extraída.");
                    PboExtractionAuditor.Validate(value.Substring(0, separator), value.Substring(separator + 1));
                    Environment.ExitCode = 0;
                }
                catch
                {
                    Environment.ExitCode = 1;
                }
                return;
            }
            string mergeRecoveryTest = args == null ? null : Array.Find(args,
                value => value.StartsWith("--test-merge-recovery=", StringComparison.OrdinalIgnoreCase));
            if (mergeRecoveryTest != null)
            {
                string value = mergeRecoveryTest.Substring("--test-merge-recovery=".Length).Trim('"');
                int separator = value.IndexOf('|');
                try
                {
                    if (separator <= 0 || separator == value.Length - 1)
                        throw new ArgumentException("Informe source_recuperada|extração_completa.");
                    MainForm.MergeRecoveredPboSource(value.Substring(0, separator), value.Substring(separator + 1));
                    Environment.ExitCode = 0;
                }
                catch
                {
                    Environment.ExitCode = 1;
                }
                return;
            }
            if (args != null && Array.IndexOf(args, "--test-selector") >= 0)
            {
                ToolSettings settings = ToolSettings.Load();
                ModSelectionForm selector = ModSelectionForm.CreateFromPaths(settings.WorkshopPath, settings.BenchPath,
                    settings.EditorDependencies + ";" + settings.EditorPath, string.Empty);
                Application.Run(selector);
                return;
            }
            if (args != null && Array.IndexOf(args, "--test-steam-progress-ui") >= 0)
            {
                MainForm testForm = new MainForm();
                bool succeeded = false;
                testForm.Shown += delegate
                {
                    succeeded = testForm.RunSteamProgressLayoutTest();
                    testForm.Close();
                };
                Application.Run(testForm);
                Environment.ExitCode = succeeded ? 0 : 1;
                return;
            }
            string prepareTest = args == null ? null : Array.Find(args, value => value.StartsWith("--test-prepare=", StringComparison.OrdinalIgnoreCase));
            if (prepareTest != null)
            {
                string sourcePath = prepareTest.Substring("--test-prepare=".Length).Trim('"');
                MainForm testForm = new MainForm();
                bool succeeded = false;
                testForm.Shown += async delegate
                {
                    succeeded = await testForm.RunAutomatedSourcePreparation(sourcePath);
                    testForm.Close();
                };
                Application.Run(testForm);
                Environment.ExitCode = succeeded ? 0 : 1;
                return;
            }
            string importTest = args == null ? null : Array.Find(args, value => value.StartsWith("--test-import=", StringComparison.OrdinalIgnoreCase));
            if (importTest != null)
            {
                string modName = importTest.Substring("--test-import=".Length).Trim('"');
                MainForm testForm = new MainForm();
                bool succeeded = false;
                testForm.Shown += async delegate
                {
                    succeeded = await testForm.RunAutomatedSteamImport(modName);
                    testForm.Close();
                };
                Application.Run(testForm);
                Environment.ExitCode = succeeded ? 0 : 1;
                return;
            }
            Application.Run(new MainForm());
        }
    }
}
