using System;
using System.Windows.Forms;

namespace DayZModWorkbench
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args != null && Array.IndexOf(args, "--test-selector") >= 0)
            {
                ToolSettings settings = ToolSettings.Load();
                ModSelectionForm selector = ModSelectionForm.CreateFromPaths(settings.WorkshopPath, settings.BenchPath,
                    settings.EditorDependencies + ";" + settings.EditorPath, string.Empty);
                Application.Run(selector);
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
