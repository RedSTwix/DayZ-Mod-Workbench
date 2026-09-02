using System;
using System.Collections.Generic;
using System.IO;
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
                return RecoveredScripts + " script(s) recuperado(s); P3D " + VerifiedP3ds + "/" +
                    P3dCount + " e config.bin " + VerifiedConfigs + "/" + ConfigCount +
                    " semanticamente exatos; verificação v5 (scripts/P3D/configs): " +
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
                converterContents.IndexOf("PBO_EQUIVALENCE_VERIFICATION.txt", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("O addon Python local não oferece verificação semântica v5. Conecte-se à internet para atualizá-lo.");

            Directory.CreateDirectory(outputRoot);
            string launcherOption = Path.GetFileName(pythonPath).Equals("py.exe", StringComparison.OrdinalIgnoreCase) ? "-3 " : string.Empty;
            string arguments = launcherOption + ProcessRunner.Quote(converterScriptPath) + " " +
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
            if (!File.Exists(verificationPath)) throw new InvalidOperationException("O addon v5 não gerou a verificação de equivalência do PBO.");
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
            string verification = File.ReadAllText(verificationPath);
            result.VerificationStatus = ReadReportText(verification, "Overall");
            ReadReportRatio(verification, "Embedded P3Ds verified", out result.VerifiedP3ds, out result.P3dCount);
            ReadReportRatio(verification, "Configs semantic-verified", out result.VerifiedConfigs, out result.ConfigCount);
            if (!result.VerificationStatus.Equals("SEMANTIC-EXACT", StringComparison.OrdinalIgnoreCase) &&
                !result.VerificationStatus.Equals("SEMANTIC-EXACT-INCLUDE-GRAPH", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O addon v5 não comprovou equivalência integral: " +
                    result.VerificationStatus + ". Consulte " + verificationPath);
            if (result.VerifiedP3ds != result.P3dCount || result.VerifiedConfigs != result.ConfigCount)
                throw new InvalidOperationException("Verificação v5 incompleta: P3D " + result.VerifiedP3ds + "/" +
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
            string configCpp = Path.Combine(sourceRoot, "config.cpp");
            string configBin = Path.Combine(sourceRoot, "config.bin");
            bool hasTextConfig = File.Exists(configCpp) && new FileInfo(configCpp).Length > 0;
            bool hasBinaryConfig = File.Exists(configBin) && new FileInfo(configBin).Length > 0;
            if (!hasTextConfig && !hasBinaryConfig)
                throw new InvalidOperationException("A recuperação não produziu config.cpp nem config.bin utilizável.");
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
            if (!match.Success) throw new InvalidOperationException("Campo proporcional ausente no relatório v5: " + label);
            value = int.Parse(match.Groups[1].Value);
            total = int.Parse(match.Groups[2].Value);
        }
    }
}
