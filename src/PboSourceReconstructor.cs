using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Threading.Tasks;

namespace DayZModWorkbench
{
    internal sealed class PboSourceRecoveryResult
    {
        public string SourceRoot;
        public string ReportPath;
        public string ManifestPath;
        public string VerificationReportPath;
        public string TechniqueSelectionPath;
        public string Prefix;
        public int HeaderEntries;
        public int RecoveredFiles;
        public int ReachableEntries;
        public int ValidCompressedBlocks;
        public int UnresolvedIncludes;
        public int RecoveredScripts;
        public int RecoveredConfigs;
        public int FilteredDecoyPayloads;
        public int RecoveredDecoyLeakage;
        public int RecoveredTypeMismatchLeakage;
        public string TechniqueName;
        public double TechniqueConfidence;
        public double TechniqueThreshold;
        public bool TechniqueFallback;
        public int PboToolsMarkerRoots;
        public string PboToolsMarkerStyles;
        public int VerifiedP3ds;
        public int P3dCount;
        public int VerifiedConfigs;
        public int ConfigCount;
        public string VerificationStatus;
        public readonly List<string> Warnings = new List<string>();

        public string Summary
        {
            get
            {
                string markerInfo = PboToolsMarkerRoots > 0
                    ? " PBO Tools: " + PboToolsMarkerRoots + " raiz(es) confiável(is) [" + PboToolsMarkerStyles + "];"
                    : string.Empty;
                string techniqueInfo = string.IsNullOrWhiteSpace(TechniqueName)
                    ? string.Empty
                    : " técnica " + TechniqueName + " (confidence " + TechniqueConfidence.ToString("0.000") + ");";
                return RecoveredScripts + " script(s) recuperado(s);" + techniqueInfo + markerInfo +
                    " P3D " + VerifiedP3ds + "/" + P3dCount + " e config.bin " +
                    VerifiedConfigs + "/" + ConfigCount + " semanticamente exatos; " +
                    FilteredDecoyPayloads + " decoy(s) filtrado(s); verificação do addon: " +
                    VerificationStatus + ".";
            }
        }
    }

    internal sealed class PboTechniqueSelectionAudit
    {
        public string selected { get; set; }
        public double selected_confidence { get; set; }
        public double automatic_threshold { get; set; }
        public bool fallback { get; set; }
        public PboTechniqueEvaluation[] evaluated { get; set; }
    }

    internal sealed class PboTechniqueEvaluation
    {
        public string technique { get; set; }
        public bool matched { get; set; }
        public double confidence { get; set; }
        public string[] evidence { get; set; }
    }

    internal static class PboSourceReconstructor
    {
        private const double MinimumSpecializedTechniqueConfidence = 0.95d;
        internal static async Task<PboSourceRecoveryResult> RecoverAsync(string pboPath, string outputRoot,
            string pythonPath, string converterScriptPath, string cfgConvertPath, Action<string> log)
        {
            if (!File.Exists(pboPath)) throw new FileNotFoundException("PBO não encontrado.", pboPath);
            if (!File.Exists(pythonPath)) throw new FileNotFoundException("Python não encontrado.", pythonPath);
            if (!File.Exists(converterScriptPath)) throw new FileNotFoundException("Addon ODOL/PBO não encontrado.", converterScriptPath);
            if (!File.Exists(cfgConvertPath)) throw new FileNotFoundException("CfgConvert.exe não encontrado.", cfgConvertPath);
            string converterContents = File.ReadAllText(converterScriptPath);
            if (converterContents.IndexOf("verify_pbo_archive", StringComparison.Ordinal) < 0 ||
                converterContents.IndexOf("PBO_EQUIVALENCE_VERIFICATION.txt", StringComparison.Ordinal) < 0 ||
                converterContents.IndexOf("forensic_recover_rvmat_bytes", StringComparison.Ordinal) < 0 ||
                converterContents.IndexOf("PBO_FULL_PAYLOAD_RECOVERY", StringComparison.Ordinal) < 0 ||
                converterContents.IndexOf("PBO_TOOLS_MARKER_RECOVERY", StringComparison.Ordinal) < 0 ||
                converterContents.IndexOf("PBO_TOOLS_V18_MARKER_RECOVERY", StringComparison.Ordinal) < 0 ||
                converterContents.IndexOf("GENERIC_DECOY_FILTER", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("O addon Python local não oferece as garantias de recuperação exigidas pelo Workbench 1.7.7. Atualize para o addon modular v8.0.1 ou superior.");

            Directory.CreateDirectory(outputRoot);
            string launcherOption = Path.GetFileName(pythonPath).Equals("py.exe", StringComparison.OrdinalIgnoreCase) ? "-3 " : string.Empty;
            string arguments = launcherOption + "-u " + ProcessRunner.Quote(converterScriptPath) + " " +
                ProcessRunner.Quote(pboPath) + " " + ProcessRunner.Quote(outputRoot) +
                " --cfgconvert " + ProcessRunner.Quote(cfgConvertPath);
            ProcessResult process = await ProcessRunner.RunAsync(pythonPath, arguments,
                Path.GetDirectoryName(pboPath), log);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("O addon Python terminou com código " + process.ExitCode + " ao recuperar o PBO.");

            string reportPath = Path.Combine(outputRoot, "PBO_RECOVERY_REPORT.txt");
            string manifestPath = Path.Combine(outputRoot, "PBO_MANIFEST.json");
            string verificationPath = Path.Combine(outputRoot, "PBO_EQUIVALENCE_VERIFICATION.txt");
            string techniqueSelectionPath = Path.Combine(outputRoot, "PBO_TECHNIQUE_SELECTION.json");
            string sourceRoot = Path.Combine(outputRoot, "recovered_source");
            if (!File.Exists(reportPath)) throw new InvalidOperationException("O addon não gerou o relatório de recuperação do PBO.");
            if (!File.Exists(manifestPath)) throw new InvalidOperationException("O addon não gerou o manifesto de recuperação do PBO.");
            if (!File.Exists(verificationPath)) throw new InvalidOperationException("O addon não gerou a verificação de equivalência do PBO.");
            if (!File.Exists(techniqueSelectionPath)) throw new InvalidOperationException("O addon v8 não gerou PBO_TECHNIQUE_SELECTION.json; a técnica utilizada não pode ser auditada.");
            if (!Directory.Exists(sourceRoot)) throw new InvalidOperationException("O addon não gerou a pasta recovered_source.");

            string report = File.ReadAllText(reportPath);
            PboSourceRecoveryResult result = new PboSourceRecoveryResult
            {
                SourceRoot = sourceRoot,
                ReportPath = reportPath,
                ManifestPath = manifestPath,
                VerificationReportPath = verificationPath,
                TechniqueSelectionPath = techniqueSelectionPath,
                Prefix = ReadReportText(report, "Prefix"),
                HeaderEntries = ReadReportNumber(report, "Header entries"),
                RecoveredFiles = ReadReportNumber(report, "Recovered source files"),
                ReachableEntries = ReadReportNumber(report, "Reachable payload entries"),
                ValidCompressedBlocks = ReadReportNumber(report, "Cprs blocks checksum-validated"),
                UnresolvedIncludes = ReadReportNumber(report, "Unresolved includes"),
                FilteredDecoyPayloads = ReadReportNumberOptional(report, "Filtered decoy payload files")
            };
            result.PboToolsMarkerRoots = ReadReportNumberOptional(report, "PBO Tools marker roots");
            result.PboToolsMarkerStyles = ReadReportText(report, "PBO Tools marker styles");
            string verification = File.ReadAllText(verificationPath);
            result.VerificationStatus = ReadReportText(verification, "Overall");
            result.RecoveredDecoyLeakage = ReadReportNumber(verification, "Recovered decoy leakage");
            result.RecoveredTypeMismatchLeakage = ReadReportNumber(verification, "Recovered type-mismatch leakage");
            ReadReportRatio(verification, "Embedded P3Ds verified", out result.VerifiedP3ds, out result.P3dCount);
            ReadReportRatio(verification, "Configs semantic-verified", out result.VerifiedConfigs, out result.ConfigCount);

            PboTechniqueSelectionAudit selection = ReadTechniqueSelection(techniqueSelectionPath);
            ValidateTechniqueSelection(selection, techniqueSelectionPath);
            result.TechniqueName = selection.selected;
            result.TechniqueConfidence = selection.selected_confidence;
            result.TechniqueThreshold = selection.automatic_threshold;
            result.TechniqueFallback = selection.fallback;
            if (!result.VerificationStatus.Equals("SEMANTIC-EXACT", StringComparison.OrdinalIgnoreCase) &&
                !result.VerificationStatus.Equals("SEMANTIC-EXACT-INCLUDE-GRAPH", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O addon não comprovou equivalência integral: " +
                    result.VerificationStatus + ". Consulte " + verificationPath);
            if (result.VerifiedP3ds != result.P3dCount || result.VerifiedConfigs != result.ConfigCount)
                throw new InvalidOperationException("Verificação incompleta: P3D " + result.VerifiedP3ds + "/" +
                    result.P3dCount + ", configs " + result.VerifiedConfigs + "/" + result.ConfigCount + ".");
            if (result.RecoveredDecoyLeakage != 0 || result.RecoveredTypeMismatchLeakage != 0)
                throw new InvalidOperationException("A auditoria independente detectou resíduos na recovered_source: " +
                    result.RecoveredDecoyLeakage + " decoy(s) e " + result.RecoveredTypeMismatchLeakage +
                    " arquivo(s) com extensão/conteúdo incompatíveis. Consulte " + verificationPath);
            bool archiveSha1Valid;
            if (!bool.TryParse(ReadReportText(report, "Archive SHA1 trailer valid"), out archiveSha1Valid) ||
                !archiveSha1Valid)
                throw new InvalidOperationException("O trailer SHA-1 do PBO não passou na validação.");
            int payloadErrors = ReadReportNumber(report, "Payload errors");
            if (result.HeaderEntries <= 0)
                throw new InvalidOperationException("O relatório não contém entradas válidas do PBO.");
            if (payloadErrors != 0)
                throw new InvalidOperationException("A recuperação do PBO teve " + payloadErrors + " erro(s) de payload.");
            if (result.RecoveredFiles <= 0)
                throw new InvalidOperationException("O addon não encontrou arquivos-fonte recuperáveis neste PBO.");

            string[] recoveredFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
            result.RecoveredScripts = Array.FindAll(recoveredFiles,
                path => Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase)).Length;
            result.RecoveredConfigs = result.VerifiedConfigs;
            if (recoveredFiles.Length != result.RecoveredFiles)
                throw new InvalidOperationException("A contagem física de recovered_source não coincide com o relatório: " +
                    recoveredFiles.Length + " arquivo(s) no disco contra " + result.RecoveredFiles +
                    " declarado(s). A recuperação foi rejeitada para evitar arquivos extras ou ausentes.");
            bool hasTextConfig = Directory.GetFiles(sourceRoot, "*.cpp", SearchOption.AllDirectories).Any(path =>
                Path.GetFileName(path).IndexOf("config", StringComparison.OrdinalIgnoreCase) >= 0 &&
                new FileInfo(path).Length > 0);
            bool hasBinaryConfig = Directory.GetFiles(sourceRoot, "*.bin", SearchOption.AllDirectories).Any(path =>
                Path.GetFileName(path).Equals("config.bin", StringComparison.OrdinalIgnoreCase) &&
                new FileInfo(path).Length > 0);
            if (result.ConfigCount > 0 && !hasTextConfig && !hasBinaryConfig)
                throw new InvalidOperationException("O PBO contém config verificado, mas a recuperação não produziu config.cpp/config.bin utilizável.");
            // PBOs protegidos de dados podem ser válidos sem config raiz.
            if (result.UnresolvedIncludes > 0)
                result.Warnings.Add(result.UnresolvedIncludes + " include(s) não puderam ser resolvidos; revise o source antes de compilar.");

            return result;
        }

        private static PboTechniqueSelectionAudit ReadTechniqueSelection(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                PboTechniqueSelectionAudit selection = serializer.Deserialize<PboTechniqueSelectionAudit>(json);
                if (selection == null)
                    throw new InvalidOperationException("JSON vazio ou incompatível.");
                return selection;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("PBO_TECHNIQUE_SELECTION.json inválido; a técnica de recuperação não pode ser auditada. " + ex.Message, ex);
            }
        }

        private static void ValidateTechniqueSelection(PboTechniqueSelectionAudit selection, string path)
        {
            if (string.IsNullOrWhiteSpace(selection.selected))
                throw new InvalidOperationException("PBO_TECHNIQUE_SELECTION.json não informa a técnica selecionada. Consulte " + path);
            if (double.IsNaN(selection.selected_confidence) || double.IsInfinity(selection.selected_confidence) ||
                selection.selected_confidence < 0d || selection.selected_confidence > 1d)
                throw new InvalidOperationException("Confidence inválida para a técnica " + selection.selected + ". Consulte " + path);
            if (double.IsNaN(selection.automatic_threshold) || double.IsInfinity(selection.automatic_threshold) ||
                selection.automatic_threshold < 0d || selection.automatic_threshold > 1d)
                throw new InvalidOperationException("Threshold inválido para a técnica " + selection.selected + ". Consulte " + path);

            PboTechniqueEvaluation selectedEvaluation = null;
            if (selection.evaluated != null)
            {
                selectedEvaluation = Array.Find(selection.evaluated, item => item != null &&
                    string.Equals(item.technique, selection.selected, StringComparison.OrdinalIgnoreCase));
            }
            if (selectedEvaluation == null || !selectedEvaluation.matched)
                throw new InvalidOperationException("A técnica selecionada não aparece como matched na auditoria de detecção: " +
                    selection.selected + ". Consulte " + path);
            if (Math.Abs(selectedEvaluation.confidence - selection.selected_confidence) > 0.000001d)
                throw new InvalidOperationException("Confidence inconsistente entre selected e evaluated para " +
                    selection.selected + ". Consulte " + path);

            if (!selection.fallback)
            {
                double required = Math.Max(selection.automatic_threshold, MinimumSpecializedTechniqueConfidence);
                if (selection.selected_confidence + 0.000001d < required)
                    throw new InvalidOperationException("A técnica especializada " + selection.selected +
                        " foi selecionada com confidence " + selection.selected_confidence.ToString("0.000") +
                        ", abaixo do mínimo independente " + required.ToString("0.000") +
                        ". A recuperação foi rejeitada para evitar falso positivo.");
            }
        }

        private static int ReadReportNumber(string report, string label)
        {
            Match match = Regex.Match(report, "^" + Regex.Escape(label) + @"\s*:\s*(\d+)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!match.Success) throw new InvalidOperationException("Campo ausente no relatório de recuperação: " + label);
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
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static void ReadReportRatio(string report, string label, out int value, out int total)
        {
            Match match = Regex.Match(report, "^" + Regex.Escape(label) + @"\s*:\s*(\d+)\s*/\s*(\d+)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (!match.Success) throw new InvalidOperationException("Campo proporcional ausente no relatório v7: " + label);
            value = int.Parse(match.Groups[1].Value);
            total = int.Parse(match.Groups[2].Value);
        }
    }
}
