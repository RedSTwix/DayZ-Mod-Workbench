using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace DayZModWorkbench
{
    internal sealed class PboExtractionAuditResult
    {
        public int PayloadFiles;
        public int VerifiedFiles;
        public int TransformedFiles;
        public bool PreparedSourceAudit;

        public int AccountedFiles
        {
            get { return VerifiedFiles + TransformedFiles; }
        }

        public string Summary
        {
            get
            {
                if (!PreparedSourceAudit)
                    return "auditoria integral aprovada: " + VerifiedFiles + "/" + PayloadFiles +
                        " payload(s) extraído(s) com SHA-1 idêntico ao PBO";

                return "auditoria da source preparada aprovada: " + AccountedFiles + "/" + PayloadFiles +
                    " payload(s) contabilizado(s) — " + VerifiedFiles + " SHA-1 idêntico(s) e " +
                    TransformedFiles + " transformação(ões) legítima(s) da preparação";
            }
        }
    }

    internal static class PboExtractionAuditor
    {
        internal static PboExtractionAuditResult Validate(string manifestPath, string extractedRoot,
            Action<int, string> progress = null)
        {
            return ValidateCore(manifestPath, extractedRoot, progress, false);
        }

        // Use only after SourcePreparer has already transformed the BankRev tree.
        // config.bin -> config.cpp and removal of texHeaders.bin are legitimate source
        // preparation steps and must not be mistaken for lost PBO payloads. Every other
        // original payload remains subject to the same strict SHA-1 comparison.
        internal static PboExtractionAuditResult ValidatePreparedSource(string manifestPath, string extractedRoot,
            Action<int, string> progress = null)
        {
            return ValidateCore(manifestPath, extractedRoot, progress, true);
        }

        private static PboExtractionAuditResult ValidateCore(string manifestPath, string extractedRoot,
            Action<int, string> progress, bool preparedSourceAudit)
        {
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("Manifesto do PBO não encontrado.", manifestPath);
            if (!Directory.Exists(extractedRoot)) throw new DirectoryNotFoundException("Extração do PBO não encontrada: " + extractedRoot);

            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> manifest = serializer.DeserializeObject(File.ReadAllText(manifestPath))
                as Dictionary<string, object>;
            if (manifest == null) throw new InvalidDataException("Manifesto do PBO inválido.");

            object archiveValue;
            if (!manifest.TryGetValue("archive_sha1_ok", out archiveValue) || !(archiveValue is bool) || !(bool)archiveValue)
                throw new InvalidDataException("O trailer SHA-1 do PBO não passou na validação.");

            object entriesValue;
            object[] entries;
            if (!manifest.TryGetValue("entries", out entriesValue) || (entries = entriesValue as object[]) == null)
                throw new InvalidDataException("O manifesto não contém a lista de entradas do PBO.");

            string root = Path.GetFullPath(extractedRoot).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            HashSet<string> expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> allowedPreparedExtras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> failures = new List<string>();
            int payloads = 0;
            int verified = 0;
            int transformed = 0;
            int totalPayloads = 0;
            int lastReportedPercentage = -1;

            foreach (object entryValue in entries)
            {
                Dictionary<string, object> entry = entryValue as Dictionary<string, object>;
                if (entry != null && ReadString(entry, "kind") == "file") totalPayloads++;
            }

            foreach (object entryValue in entries)
            {
                Dictionary<string, object> entry = entryValue as Dictionary<string, object>;
                if (entry == null || ReadString(entry, "kind") != "file") continue;
                payloads++;
                string name = ReadString(entry, "decoded_name");
                int auditPercentage = (int)Math.Floor((payloads - 1) * 95d / Math.Max(1, totalPayloads));
                if (progress != null && auditPercentage != lastReportedPercentage)
                {
                    lastReportedPercentage = auditPercentage;
                    progress(auditPercentage, string.IsNullOrWhiteSpace(name) ? "entrada " + payloads : name);
                }
                string expectedSha1 = ReadString(entry, "decompressed_sha1");
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(expectedSha1))
                {
                    failures.Add("entrada " + payloads + " sem nome ou SHA-1");
                    continue;
                }

                string target;
                try
                {
                    target = Path.GetFullPath(Path.Combine(extractedRoot,
                        name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
                }
                catch (Exception ex)
                {
                    failures.Add(name + " (caminho inválido: " + ex.Message + ")");
                    continue;
                }
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(name + " (caminho fora da extração)");
                    continue;
                }
                if (!expectedPaths.Add(target))
                {
                    failures.Add(name + " (nome duplicado no PBO)");
                    continue;
                }
                if (!File.Exists(target))
                {
                    string preparedReplacement;
                    if (preparedSourceAudit && TryAcceptPreparedTransformation(target, out preparedReplacement))
                    {
                        transformed++;
                        if (!string.IsNullOrWhiteSpace(preparedReplacement))
                            allowedPreparedExtras.Add(Path.GetFullPath(preparedReplacement));
                        continue;
                    }

                    failures.Add(name + " (não extraído)");
                    continue;
                }

                // A config.cpp generated from a still-present config.bin is also an
                // expected artifact of SourcePreparer and must not be reported as extra.
                if (preparedSourceAudit && Path.GetFileName(target).Equals("config.bin", StringComparison.OrdinalIgnoreCase))
                {
                    string cpp = Path.Combine(Path.GetDirectoryName(target), "config.cpp");
                    if (IsUsablePreparedConfig(cpp)) allowedPreparedExtras.Add(Path.GetFullPath(cpp));
                }

                string actualSha1 = ComputeSha1(target);
                if (!actualSha1.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(name + " (SHA-1 diferente)");
                    continue;
                }
                verified++;
            }

            if (progress != null) progress(95, "conferindo arquivos extras");
            string[] extractedFiles = Directory.GetFiles(extractedRoot, "*", SearchOption.AllDirectories);
            foreach (string file in extractedFiles)
            {
                string full = Path.GetFullPath(file);
                if (!expectedPaths.Contains(full) && !allowedPreparedExtras.Contains(full))
                    failures.Add(RelativePath(root, file) + " (arquivo extra)");
            }

            if (payloads == 0) throw new InvalidDataException("O manifesto não contém payloads de arquivo.");
            if (verified + transformed != payloads || failures.Count > 0)
            {
                string details = failures.Count == 0 ? "contagem de payloads contabilizados diferente" :
                    string.Join("; ", failures.GetRange(0, Math.Min(8, failures.Count)).ToArray());
                string mode = preparedSourceAudit ? "Auditoria da source preparada falhou: " :
                    "Auditoria integral da extração falhou: ";
                throw new InvalidDataException(mode + verified + "/" + payloads +
                    " payload(s) com SHA-1 idêntico(s), " + transformed + " transformação(ões) legítima(s). " + details);
            }

            if (progress != null) progress(100, "integridade aprovada");
            return new PboExtractionAuditResult
            {
                PayloadFiles = payloads,
                VerifiedFiles = verified,
                TransformedFiles = transformed,
                PreparedSourceAudit = preparedSourceAudit
            };
        }

        private static bool TryAcceptPreparedTransformation(string originalPath, out string replacementPath)
        {
            replacementPath = string.Empty;
            string fileName = Path.GetFileName(originalPath);

            // texHeaders.bin is a generated texture index/cache. SourcePreparer
            // intentionally removes it; Addon Builder regenerates it on build.
            if (fileName.Equals("texHeaders.bin", StringComparison.OrdinalIgnoreCase)) return true;

            // SourcePreparer converts config.bin into an editable sibling config.cpp
            // and deletes the binary only after the text passes CfgConvert validation.
            if (!fileName.Equals("config.bin", StringComparison.OrdinalIgnoreCase)) return false;
            string cpp = Path.Combine(Path.GetDirectoryName(originalPath), "config.cpp");
            if (!IsUsablePreparedConfig(cpp)) return false;
            replacementPath = cpp;
            return true;
        }

        private static bool IsUsablePreparedConfig(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length >= 4)
                    {
                        int a = stream.ReadByte();
                        int b = stream.ReadByte();
                        int c = stream.ReadByte();
                        int d = stream.ReadByte();
                        if (a == 0 && b == 0x72 && c == 0x61 && d == 0x50) return false;
                    }
                }
                using (StreamReader reader = new StreamReader(path, true))
                {
                    char[] buffer = new char[4096];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    for (int i = 0; i < read; i++)
                        if (!char.IsWhiteSpace(buffer[i])) return true;
                }
            }
            catch { }
            return false;
        }

        private static string ReadString(Dictionary<string, object> value, string key)
        {
            object item;
            return value.TryGetValue(key, out item) && item != null ? Convert.ToString(item) : string.Empty;
        }

        private static string ComputeSha1(string path)
        {
            using (SHA1 sha1 = SHA1.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha1.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string RelativePath(string rootWithSeparator, string path)
        {
            string full = Path.GetFullPath(path);
            return full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(rootWithSeparator.Length)
                : Path.GetFileName(full);
        }
    }
}
