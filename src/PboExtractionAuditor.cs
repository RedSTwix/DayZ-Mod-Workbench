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

        public string Summary
        {
            get
            {
                return "auditoria integral aprovada: " + VerifiedFiles + "/" + PayloadFiles +
                    " payload(s) extraído(s) com SHA-1 idêntico ao PBO";
            }
        }
    }

    internal static class PboExtractionAuditor
    {
        internal static PboExtractionAuditResult Validate(string manifestPath, string extractedRoot)
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
            List<string> failures = new List<string>();
            int payloads = 0;
            int verified = 0;

            foreach (object entryValue in entries)
            {
                Dictionary<string, object> entry = entryValue as Dictionary<string, object>;
                if (entry == null || ReadString(entry, "kind") != "file") continue;
                payloads++;
                string name = ReadString(entry, "decoded_name");
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
                    failures.Add(name + " (não extraído)");
                    continue;
                }

                string actualSha1 = ComputeSha1(target);
                if (!actualSha1.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(name + " (SHA-1 diferente)");
                    continue;
                }
                verified++;
            }

            string[] extractedFiles = Directory.GetFiles(extractedRoot, "*", SearchOption.AllDirectories);
            foreach (string file in extractedFiles)
                if (!expectedPaths.Contains(Path.GetFullPath(file))) failures.Add(RelativePath(root, file) + " (arquivo extra)");

            if (payloads == 0) throw new InvalidDataException("O manifesto não contém payloads de arquivo.");
            if (verified != payloads || failures.Count > 0 || extractedFiles.Length != expectedPaths.Count)
            {
                string details = failures.Count == 0 ? "contagem de arquivos diferente" :
                    string.Join("; ", failures.GetRange(0, Math.Min(8, failures.Count)).ToArray());
                throw new InvalidDataException("Auditoria integral da extração falhou: " + verified + "/" +
                    payloads + " payload(s) verificado(s). " + details);
            }

            return new PboExtractionAuditResult { PayloadFiles = payloads, VerifiedFiles = verified };
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
