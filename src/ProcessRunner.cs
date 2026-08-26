using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace DayZModWorkbench
{
    internal sealed class ProcessResult
    {
        public int ExitCode;
        public string Output;
    }

    internal static class ProcessRunner
    {
        public static Task<ProcessResult> RunAsync(string executable, string arguments, string workingDirectory, Action<string> log)
        {
            return Task.Run(delegate
            {
                StringBuilder output = new StringBuilder();
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (output) output.AppendLine(e.Data);
                        if (log != null) log(e.Data);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (output) output.AppendLine(e.Data);
                        if (log != null) log(e.Data);
                    };

                    if (log != null) log("> " + executable + " " + arguments);
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    process.WaitForExit();
                    string text;
                    lock (output) text = output.ToString();
                    return new ProcessResult { ExitCode = process.ExitCode, Output = text };
                }
            });
        }

        public static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
