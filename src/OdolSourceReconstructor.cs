using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
        public int RecoveredRvmats;
        public int ForensicRvmats;
        public int EmbeddedRvmats;
        public int SourceProofRvmats;
        public int SemanticOnlyRvmats;
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
                string rvmatDetail;
                if (SourceProofRvmats > 0 || SemanticOnlyRvmats > 0)
                    rvmatDetail = ForensicRvmats + " por RaP forense direto, " + SourceProofRvmats +
                        " por RaP+EmbeddedMaterial com prova byte-a-byte e " + SemanticOnlyRvmats +
                        " somente por equivalência semântica";
                else
                    rvmatDetail = ForensicRvmats + " por recuperação forense RaP com prova byte-a-byte e " +
                        EmbeddedRvmats + " a partir de EmbeddedMaterial ODOL";
                return OdolConverted + " modelo(s) ODOL reconstruído(s) como MLOD; " +
                    MlodKept + " MLOD já existente(s); " + AnimatedModels + " modelo(s) animado(s) com model.cfg recuperado; " +
                    RecoveredRvmats + " RVMAT(s) reconstruído(s): " + rvmatDetail + ".";
            }
        }
    }

    internal static class OdolSourceReconstructor
    {
        public static async Task<OdolReconstructionResult> ReconstructAsync(string sourceRoot,
            string pythonPath, string converterScriptPath, Action<string> log,
            Action<int, string> progress = null)
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
                if (progress != null) progress(5, "preparando reconstrução de modelos");
                string launcherOption = Path.GetFileName(pythonPath).Equals("py.exe", StringComparison.OrdinalIgnoreCase) ? "-3 " : string.Empty;
                string arguments = launcherOption + "-u " + ProcessRunner.Quote(converterScriptPath) + " " +
                    ProcessRunner.Quote(sourceRoot) + " " + ProcessRunner.Quote(outputRoot);
                int processedModels = 0;
                Action<string> processLog = delegate(string line)
                {
                    if (log != null) log(line);
                    if (progress == null || string.IsNullOrWhiteSpace(line)) return;
                    if (!line.StartsWith("[P3D]", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase)) return;
                    int completed = Math.Min(sourceModels.Length, Interlocked.Increment(ref processedModels));
                    int percentage = 10 + (int)Math.Floor(completed * 72d / Math.Max(1, sourceModels.Length));
                    progress(percentage, "modelo " + completed + "/" + sourceModels.Length);
                };
                ProcessResult process = await ProcessRunner.RunAsync(pythonPath, arguments, sourceRoot, processLog);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Conversor ODOL terminou com código " + process.ExitCode + ".");

                if (progress != null) progress(84, "validando relatório da reconstrução");
                string reportPath = Path.Combine(outputRoot, "DEBINARIZE_REPORT.txt");
                if (!File.Exists(reportPath)) throw new InvalidOperationException("O conversor ODOL não gerou relatório.");
                string report = File.ReadAllText(reportPath);
                int converted = ReadReportNumber(report, "ODOL converted to MLOD");
                int kept = ReadReportNumber(report, "Already-MLOD kept");
                int animated = ReadReportNumber(report, "Animated ODOL models detected");
                int errors = ReadReportNumber(report, "Errors");
                int forensicRvmats = ReadReportNumberOptional(report, "Forensic RaP RVMATs recovered");
                int embeddedRvmats = ReadReportNumberOptional(report, "Embedded RVMATs recovered");
                int sourceProofRvmats = ReadReportNumberOptional(report, "Source-proof Embedded RVMATs recovered");
                int semanticOnlyRvmats = ReadReportNumberOptional(report, "Semantic-only Embedded RVMATs recovered");
                int recoveredRvmats = ReadReportNumberOptional(report, "RVMATs recovered total");
                if (recoveredRvmats == 0) recoveredRvmats = forensicRvmats + embeddedRvmats;
                if (errors != 0 || converted != odolCount)
                    throw new InvalidOperationException("Reconstrução ODOL incompleta: " + converted + "/" + odolCount + " convertido(s), " + errors + " erro(s).");

                string[] outputModels = Directory.GetFiles(outputRoot, "*.p3d", SearchOption.AllDirectories);
                if (outputModels.Length != sourceModels.Length)
                    throw new InvalidOperationException("A source reconstruída possui " + outputModels.Length +
                        " P3D(s), mas a original possui " + sourceModels.Length + ".");
                string invalid = outputModels.FirstOrDefault(path => ReadSignature(path) != "MLOD");
                if (invalid != null) throw new InvalidOperationException("P3D reconstruído não é MLOD: " + invalid);
                int verificationIndex = 0;
                foreach (string sourceModel in sourceModels)
                {
                    string relative = RelativePath(sourceRoot, sourceModel);
                    if (progress != null)
                        progress(88 + (int)Math.Floor(verificationIndex * 11d / Math.Max(1, sourceModels.Length)),
                            "verificando " + relative);
                    string verificationPath = Path.Combine(outputRoot, relative + ".verification.txt");
                    if (!File.Exists(verificationPath))
                        throw new InvalidOperationException("O v7 não gerou a verificação do modelo: " + relative);
                    string expected = ReadSignature(sourceModel) == "ODOL" ? "SEMANTIC-EXACT" : "STRUCTURAL-EXACT";
                    string actual = ReadReportText(File.ReadAllText(verificationPath), "Result");
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Verificação v7 de " + relative + ": esperado " +
                            expected + ", recebido " + actual + ".");
                    File.Delete(verificationPath);
                    verificationIndex++;
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
                    AnimatedModels = animated,
                    RecoveredRvmats = recoveredRvmats,
                    ForensicRvmats = forensicRvmats,
                    EmbeddedRvmats = embeddedRvmats,
                    SourceProofRvmats = sourceProofRvmats,
                    SemanticOnlyRvmats = semanticOnlyRvmats
                };
                ReadReportWarnings(report, result.Warnings);
                foreach (string modelCfgReport in Directory.GetFiles(outputRoot,
                    "MODEL_CFG_EQUIVALENCE_VERIFICATION.txt", SearchOption.AllDirectories))
                    File.Delete(modelCfgReport);
                File.Delete(reportPath);
                result.ReportPath = string.Empty;
                if (progress != null) progress(100, "modelos verificados");
                return result;
            }
            catch
            {
                await Task.Run(delegate { TryDeleteDirectory(outputRoot); });
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

        private static int ReadReportNumberOptional(string report, string label)
        {
            Match match = Regex.Match(report, "^" + Regex.Escape(label) + @"\s*:\s*(\d+)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            return match.Success ? int.Parse(match.Groups[1].Value) : 0;
        }

        private static string ReadReportText(string report, string label)
        {
            Match match = Regex.Match(report, "^" + Regex.Escape(label) + @"\s*:\s*(.*?)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!match.Success) throw new InvalidOperationException("Campo ausente no relatório ODOL v7: " + label);
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
                if (line.Equals("MODEL.CFG WARNINGS:", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("RECOVERY WARNINGS:", StringComparison.OrdinalIgnoreCase))
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
