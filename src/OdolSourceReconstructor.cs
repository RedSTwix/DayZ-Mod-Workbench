using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DayZModWorkbench
{
    internal sealed class OdolReconstructionResult
    {
        public string OutputRoot;
        public string ReportPath;
        public int OdolConverted;
        public int MlodKept;
        public int AnimatedModels;
        public readonly List<string> Warnings = new List<string>();

        public bool Changed
        {
            get { return OdolConverted > 0 && !string.IsNullOrWhiteSpace(OutputRoot); }
        }

        public string Summary
        {
            get
            {
                if (!Changed) return "Nenhum modelo ODOL compatível precisava ser reconstruído.";
                return OdolConverted + " modelo(s) ODOL reconstruído(s) como MLOD; " +
                    MlodKept + " MLOD já existente(s); " + AnimatedModels + " modelo(s) animado(s) com model.cfg recuperado.";
            }
        }
    }

    internal static class OdolSourceReconstructor
    {
        public static async Task<OdolReconstructionResult> ReconstructAsync(string sourceRoot,
            string pythonPath, string converterScriptPath, Action<string> log)
        {
            if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("Source não encontrado: " + sourceRoot);
            string[] sourceModels = Directory.GetFiles(sourceRoot, "*.p3d", SearchOption.AllDirectories);
            int odolCount = sourceModels.Count(path => ReadSignature(path) == "ODOL");
            if (odolCount == 0) return new OdolReconstructionResult { OutputRoot = sourceRoot };
            if (!File.Exists(pythonPath)) throw new FileNotFoundException("Python não encontrado.", pythonPath);
            if (!File.Exists(converterScriptPath)) throw new FileNotFoundException("Conversor ODOL não encontrado.", converterScriptPath);

            string parent = Directory.GetParent(sourceRoot).FullName;
            string outputRoot = Path.Combine(parent,
                Path.GetFileName(sourceRoot) + "_mlod_" + Guid.NewGuid().ToString("N"));
            try
            {
                string launcherOption = Path.GetFileName(pythonPath).Equals("py.exe", StringComparison.OrdinalIgnoreCase) ? "-3 " : string.Empty;
                string arguments = launcherOption + ProcessRunner.Quote(converterScriptPath) + " " +
                    ProcessRunner.Quote(sourceRoot) + " " + ProcessRunner.Quote(outputRoot);
                ProcessResult process = await ProcessRunner.RunAsync(pythonPath, arguments, sourceRoot, log);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Conversor ODOL terminou com código " + process.ExitCode + ".");

                string reportPath = Path.Combine(outputRoot, "DEBINARIZE_REPORT.txt");
                if (!File.Exists(reportPath)) throw new InvalidOperationException("O conversor ODOL não gerou relatório.");
                string report = File.ReadAllText(reportPath);
                int converted = ReadReportNumber(report, "ODOL converted to MLOD");
                int kept = ReadReportNumber(report, "Already-MLOD kept");
                int animated = ReadReportNumber(report, "Animated ODOL models detected");
                int errors = ReadReportNumber(report, "Errors");
                if (errors != 0 || converted != odolCount)
                    throw new InvalidOperationException("Reconstrução ODOL incompleta: " + converted + "/" + odolCount + " convertido(s), " + errors + " erro(s).");

                string[] outputModels = Directory.GetFiles(outputRoot, "*.p3d", SearchOption.AllDirectories);
                if (outputModels.Length != sourceModels.Length)
                    throw new InvalidOperationException("A source reconstruída possui " + outputModels.Length +
                        " P3D(s), mas a original possui " + sourceModels.Length + ".");
                string invalid = outputModels.FirstOrDefault(path => ReadSignature(path) != "MLOD");
                if (invalid != null) throw new InvalidOperationException("P3D reconstruído não é MLOD: " + invalid);
                foreach (string sourceModel in sourceModels)
                {
                    string relative = RelativePath(sourceRoot, sourceModel);
                    string verificationPath = Path.Combine(outputRoot, relative + ".verification.txt");
                    if (!File.Exists(verificationPath))
                        throw new InvalidOperationException("O v5 não gerou a verificação do modelo: " + relative);
                    string expected = ReadSignature(sourceModel) == "ODOL" ? "SEMANTIC-EXACT" : "STRUCTURAL-EXACT";
                    string actual = ReadReportText(File.ReadAllText(verificationPath), "Result");
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Verificação v5 de " + relative + ": esperado " +
                            expected + ", recebido " + actual + ".");
                    File.Delete(verificationPath);
                }
                if (animated > 0 && Directory.GetFiles(outputRoot, "model.cfg", SearchOption.AllDirectories).Length == 0 &&
                    Directory.GetFiles(outputRoot, "model_recovered.cfg", SearchOption.AllDirectories).Length == 0)
                    throw new InvalidOperationException("Foram detectados modelos animados, mas nenhum model.cfg foi recuperado.");

                OdolReconstructionResult result = new OdolReconstructionResult
                {
                    OutputRoot = outputRoot,
                    ReportPath = reportPath,
                    OdolConverted = converted,
                    MlodKept = kept,
                    AnimatedModels = animated
                };
                ReadReportWarnings(report, result.Warnings);
                File.Delete(reportPath);
                result.ReportPath = string.Empty;
                return result;
            }
            catch
            {
                TryDeleteDirectory(outputRoot);
                throw;
            }
        }

        private static int ReadReportNumber(string report, string label)
        {
            Match match = Regex.Match(report, "^" + Regex.Escape(label) + @"\s*:\s*(\d+)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!match.Success) throw new InvalidOperationException("Campo ausente no relatório ODOL: " + label);
            return int.Parse(match.Groups[1].Value);
        }

        private static string ReadReportText(string report, string label)
        {
            Match match = Regex.Match(report, "^" + Regex.Escape(label) + @"\s*:\s*(.*?)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!match.Success) throw new InvalidOperationException("Campo ausente no relatório ODOL v5: " + label);
            return match.Groups[1].Value.Trim();
        }

        private static string RelativePath(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(fullRoot.Length)
                : Path.GetFileName(fullPath);
        }

        private static void ReadReportWarnings(string report, List<string> warnings)
        {
            bool reading = false;
            foreach (string raw in report.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = raw.Trim();
                if (line.Equals("MODEL.CFG WARNINGS:", StringComparison.OrdinalIgnoreCase))
                {
                    reading = true;
                    continue;
                }
                if (!reading) continue;
                if (line.Length == 0) break;
                if (line.StartsWith("- ")) warnings.Add(line.Substring(2));
            }
        }

        private static string ReadSignature(string path)
        {
            try
            {
                byte[] bytes = new byte[4];
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Read(bytes, 0, bytes.Length) != bytes.Length) return string.Empty;
                }
                return Encoding.ASCII.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
