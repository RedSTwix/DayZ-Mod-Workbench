using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DayZModWorkbench
{
    internal sealed class PboSourceRecoveryResult
    {
        public string SourceRoot;
        public string ReportPath;
        public string ManifestPath;
        public string VerificationReportPath;
        public string Prefix;
        public int HeaderEntries;
        public int RecoveredFiles;
        public int ReachableEntries;
        public int ValidCompressedBlocks;
        public int UnresolvedIncludes;
        public int RecoveredScripts;
        public int RecoveredConfigs;
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
                return RecoveredScripts + " script(s) recuperado(s);" + markerInfo + " P3D " + VerifiedP3ds + "/" +
                    P3dCount + " e config.bin " + VerifiedConfigs + "/" + ConfigCount +
                    " semanticamente exatos; verificação v7 (scripts/P3D/configs): " +
                    VerificationStatus + ".";
            }
        }
    }

    internal static class PboSourceReconstructor
    {
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
                converterContents.IndexOf("PBO_TOOLS_V18_MARKER_RECOVERY", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("O addon Python local não oferece verificação semântica v7. Conecte-se à internet para atualizá-lo.");

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
            string sourceRoot = Path.Combine(outputRoot, "recovered_source");
            if (!File.Exists(reportPath)) throw new InvalidOperationException("O addon não gerou o relatório de recuperação do PBO.");
            if (!File.Exists(manifestPath)) throw new InvalidOperationException("O addon não gerou o manifesto de recuperação do PBO.");
            if (!File.Exists(verificationPath)) throw new InvalidOperationException("O addon v7 não gerou a verificação de equivalência do PBO.");
            if (!Directory.Exists(sourceRoot)) throw new InvalidOperationException("O addon não gerou a pasta recovered_source.");

            string report = File.ReadAllText(reportPath);
            PboSourceRecoveryResult result = new PboSourceRecoveryResult
            {
                SourceRoot = sourceRoot,
                ReportPath = reportPath,
                ManifestPath = manifestPath,
                VerificationReportPath = verificationPath,
                Prefix = ReadReportText(report, "Prefix"),
                HeaderEntries = ReadReportNumber(report, "Header entries"),
                RecoveredFiles = ReadReportNumber(report, "Recovered source files"),
                ReachableEntries = ReadReportNumber(report, "Reachable payload entries"),
                ValidCompressedBlocks = ReadReportNumber(report, "Cprs blocks checksum-validated"),
                UnresolvedIncludes = ReadReportNumber(report, "Unresolved includes")
            };
            result.PboToolsMarkerRoots = ReadReportNumberOptional(report, "PBO Tools marker roots");
            result.PboToolsMarkerStyles = ReadReportText(report, "PBO Tools marker styles");
            string verification = File.ReadAllText(verificationPath);
            result.VerificationStatus = ReadReportText(verification, "Overall");
            ReadReportRatio(verification, "Embedded P3Ds verified", out result.VerifiedP3ds, out result.P3dCount);
            ReadReportRatio(verification, "Configs semantic-verified", out result.VerifiedConfigs, out result.ConfigCount);
            if (!result.VerificationStatus.Equals("SEMANTIC-EXACT", StringComparison.OrdinalIgnoreCase) &&
                !result.VerificationStatus.Equals("SEMANTIC-EXACT-INCLUDE-GRAPH", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O addon v7 não comprovou equivalência integral: " +
                    result.VerificationStatus + ". Consulte " + verificationPath);
            if (result.VerifiedP3ds != result.P3dCount || result.VerifiedConfigs != result.ConfigCount)
                throw new InvalidOperationException("Verificação v7 incompleta: P3D " + result.VerifiedP3ds + "/" +
                    result.P3dCount + ", configs " + result.VerifiedConfigs + "/" + result.ConfigCount + ".");
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
            if (recoveredFiles.Length < result.RecoveredFiles)
                throw new InvalidOperationException("A pasta recuperada contém somente " + recoveredFiles.Length +
                    " arquivo(s), mas o relatório base informa " + result.RecoveredFiles + ".");
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
