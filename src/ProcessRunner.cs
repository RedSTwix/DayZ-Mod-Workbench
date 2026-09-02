using System;
using System.Diagnostics;
using System.IO;
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
            return RunAsync(executable, arguments, workingDirectory, log, -1);
        }

        public static Task<ProcessResult> RunAsync(string executable, string arguments, string workingDirectory,
            Action<string> log, int timeoutMilliseconds)
        {
            return Task.Run(delegate
            {
                if (timeoutMilliseconds == 0 || timeoutMilliseconds < -1)
                    throw new ArgumentOutOfRangeException("timeoutMilliseconds");
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
                    bool completed;
                    if (timeoutMilliseconds < 0)
                    {
                        process.WaitForExit();
                        completed = true;
                    }
                    else
                    {
                        completed = process.WaitForExit(timeoutMilliseconds);
                    }
                    if (!completed)
                    {
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(2000); } catch { }
                        throw new TimeoutException(Path.GetFileName(executable) + " excedeu o limite de " +
                            Math.Max(1, timeoutMilliseconds / 1000) + " segundo(s) e foi encerrado.");
                    }
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
