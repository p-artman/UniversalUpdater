/**************************************************************************
 * 
 *  Project Name:     Universal Updater
 *  Description:      Console based unofficial updater for Windows Phone.
 * 
 *  Author:           Fadil Fadz
 *  Created Date:     2021
 *  
 *  Contributors:     Bashar Astifan
 * 
 *  Copyright © 2021 - 2024 Fadil Fadz
 * 
 *  Permission is hereby granted, free of charge, to any person obtaining a copy 
 *  of this software and associated documentation files (the "Software"), to deal 
 *  in the Software without restriction, including without limitation the rights 
 *  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 *  copies of the Software, and to permit persons to whom the Software is 
 *  furnished to do so, subject to the following conditions:
 * 
 *  The above copyright notice and this permission notice shall be included in all 
 *  copies or substantial portions of the Software.
 * 
 *  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
 *  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
 *  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
 *  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
 *  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, 
 *  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE 
 *  SOFTWARE.
 * 
 **************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Universal_Updater
{
    internal enum FfuDumpScope
    {
        RomPartitions = 1,
        AllPartitions = 2
    }

    internal sealed class DumpedPartitionInfo
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public long SizeBytes { get; set; }
        public bool Is128KAligned { get; set; }
    }

    internal sealed class FfuDumpResult
    {
        public bool Success { get; set; }
        public List<DumpedPartitionInfo> DumpedPartitions { get; } = new List<DumpedPartitionInfo>();
        public List<string> MissingOrFailedPartitions { get; } = new List<string>();
        public string ErrorMessage { get; set; }
    }

    internal static class WPinternalsHelper
    {
        internal static readonly string[] RomPartitionNames = { "EFIESP", "MainOS", "Data" };
        internal const int FfuChunkAlignmentBytes = 128 * 1024;
        private const string SingleInstanceMutexName = @"Global\WPinternalsRunning";

        internal static string LastSelectedPath { get; set; }

        internal static string FindWPinternalsExecutable(IEnumerable<string> extraSearchPaths = null)
        {
            var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(LastSelectedPath))
            {
                candidates.Add(LastSelectedPath);
            }

            if (extraSearchPaths != null)
            {
                foreach (var entry in extraSearchPaths)
                {
                    if (string.IsNullOrWhiteSpace(entry))
                        continue;
                    var trimmed = entry.Trim().Trim('"');
                    if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(trimmed);
                    }
                    else
                    {
                        candidates.Add(Path.Combine(trimmed, "WPinternals.exe"));
                    }
                }
            }

            AddRelativeCandidates(candidates, AppDomain.CurrentDomain.BaseDirectory);
            AddRelativeCandidates(candidates, Environment.CurrentDirectory);

            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var cleanDir = dir.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(cleanDir))
                    {
                        candidates.Add(Path.Combine(cleanDir, "WPinternals.exe"));
                    }
                }
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            AddInstallCandidates(candidates, programFiles);
            AddInstallCandidates(candidates, programFilesX86);
            if (!string.IsNullOrEmpty(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Programs", "Windows Phone Internals", "WPinternals.exe"));
                candidates.Add(Path.Combine(localAppData, "WPinternals", "WPinternals.exe"));
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(candidate))
                        continue;
                    string fullPath = Path.GetFullPath(candidate);
                    if (!checkedPaths.Add(fullPath))
                        continue;
                    if (File.Exists(fullPath))
                        return fullPath;
                }
                catch
                {
                }
            }

            return null;
        }

        private static void AddRelativeCandidates(List<string> candidates, string rootDir)
        {
            if (string.IsNullOrWhiteSpace(rootDir))
                return;

            candidates.Add(Path.Combine(rootDir, "WPinternals.exe"));
            candidates.Add(Path.Combine(rootDir, Program.toolsDirectory, "WPinternals.exe"));
            candidates.Add(Path.Combine(rootDir, Program.toolsDirectory, "WPinternals", "WPinternals.exe"));
        }

        private static void AddInstallCandidates(List<string> candidates, string baseFolder)
        {
            if (string.IsNullOrWhiteSpace(baseFolder))
                return;

            candidates.Add(Path.Combine(baseFolder, "Windows Phone Internals", "WPinternals.exe"));
            candidates.Add(Path.Combine(baseFolder, "WPinternals", "WPinternals.exe"));
        }

        internal static bool IsWPinternalsRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("WPinternals");
                try
                {
                    if (processes.Length > 0)
                        return true;
                }
                finally
                {
                    foreach (var proc in processes)
                    {
                        proc.Dispose();
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (Mutex.TryOpenExisting(SingleInstanceMutexName, out var existing))
                {
                    existing.Dispose();
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        internal static string NormalizePathArgument(string path)
        {
            string full = Path.GetFullPath(path.Trim());
            if (full.Length > 3)
            {
                return full.TrimEnd('\\', '/');
            }
            // Escape trailing backslash for drive roots (e.g. D:\ -> "D:\\") so CommandLineToArgvW closes quotes properly.
            if (full.EndsWith("\\", StringComparison.Ordinal))
            {
                return full + "\\";
            }
            return full;
        }

        internal static string BuildDumpFfuArguments(string ffuPath, string destinationFolder, string partitionName = null)
        {
            string safeFfu = NormalizePathArgument(ffuPath);
            string safeDest = NormalizePathArgument(destinationFolder);
            if (string.IsNullOrWhiteSpace(partitionName))
            {
                return $"-DumpFFU \"{safeFfu}\" \"{safeDest}\"";
            }
            return $"-DumpFFU \"{safeFfu}\" \"{safeDest}\" \"{partitionName.Trim()}\"";
        }

        internal static void DrainConsoleInputBuffer()
        {
            if (Console.IsInputRedirected)
                return;

            try
            {
                while (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                }
            }
            catch
            {
            }
        }

        private static int ExecuteWPinternalsDumpCommand(string wpInternalsExe, string arguments)
        {
            Program.appendLog($"[WPINTERNALS] \"{wpInternalsExe}\" {arguments}\n");
            using (var proc = new Process())
            {
                proc.StartInfo.FileName = wpInternalsExe;
                proc.StartInfo.Arguments = arguments;
                proc.StartInfo.WorkingDirectory = Path.GetDirectoryName(wpInternalsExe) ?? Environment.CurrentDirectory;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = false;
                proc.Start();
                proc.WaitForExit();
                // WPinternals CLI sends VK_RETURN via PostMessage on exit to wake attached cmd prompts; discard it cleanly.
                Thread.Sleep(50);
                DrainConsoleInputBuffer();
                return proc.ExitCode;
            }
        }

        internal static FfuDumpResult DumpPartitionsWithWPinternals(string wpInternalsExe, string ffuPath, string destinationFolder, FfuDumpScope scope)
        {
            var result = new FfuDumpResult();

            if (string.IsNullOrWhiteSpace(wpInternalsExe) || !File.Exists(wpInternalsExe))
            {
                result.ErrorMessage = "WPinternals.exe was not found.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(ffuPath) || !File.Exists(ffuPath))
            {
                result.ErrorMessage = "Target FFU file was not found.";
                return result;
            }
            if (string.IsNullOrWhiteSpace(destinationFolder))
            {
                result.ErrorMessage = "Destination folder is invalid.";
                return result;
            }

            string destFullPath = Path.GetFullPath(destinationFolder.Trim());
            Directory.CreateDirectory(destFullPath);

            try
            {
                if (scope == FfuDumpScope.RomPartitions)
                {
                    for (int i = 0; i < RomPartitionNames.Length; i++)
                    {
                        string partition = RomPartitionNames[i];
                        string expectedBin = Path.Combine(destFullPath, partition + ".bin");
                        DateTime runStartUtc = DateTime.UtcNow.AddSeconds(-2);

                        Program.WriteLine($"[{i + 1}/{RomPartitionNames.Length}] Dumping {partition}...", ConsoleColor.DarkGray);
                        string args = BuildDumpFfuArguments(ffuPath, destFullPath, partition);
                        int exitCode = ExecuteWPinternalsDumpCommand(wpInternalsExe, args);
                        Program.appendLog($"[WPINTERNALS] {partition} ExitCode={exitCode}\n");

                        var info = new FileInfo(expectedBin);
                        if (info.Exists && info.Length > 0 && info.LastWriteTimeUtc >= runStartUtc)
                        {
                            result.DumpedPartitions.Add(new DumpedPartitionInfo
                            {
                                Name = partition,
                                FilePath = info.FullName,
                                SizeBytes = info.Length,
                                Is128KAligned = (info.Length % FfuChunkAlignmentBytes) == 0
                            });
                        }
                        else
                        {
                            result.MissingOrFailedPartitions.Add(partition);
                        }
                    }

                    result.Success = result.MissingOrFailedPartitions.Count == 0 && result.DumpedPartitions.Count == RomPartitionNames.Length;
                    if (!result.Success)
                    {
                        result.ErrorMessage = "Failed to dump partition(s): " + string.Join(", ", result.MissingOrFailedPartitions);
                    }
                }
                else
                {
                    DateTime runStartUtc = DateTime.UtcNow.AddSeconds(-2);
                    Program.WriteLine("Dumping all partitions from FFU...", ConsoleColor.DarkGray);
                    string args = BuildDumpFfuArguments(ffuPath, destFullPath, null);
                    int exitCode = ExecuteWPinternalsDumpCommand(wpInternalsExe, args);
                    Program.appendLog($"[WPINTERNALS] All partitions ExitCode={exitCode}\n");

                    var binFiles = Directory.GetFiles(destFullPath, "*.bin", SearchOption.TopDirectoryOnly)
                        .Select(p => new FileInfo(p))
                        .Where(fi => fi.Exists && fi.Length > 0 && fi.LastWriteTimeUtc >= runStartUtc)
                        .OrderBy(fi => GetPartitionDisplayOrder(Path.GetFileNameWithoutExtension(fi.Name)))
                        .ThenBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var fi in binFiles)
                    {
                        result.DumpedPartitions.Add(new DumpedPartitionInfo
                        {
                            Name = Path.GetFileNameWithoutExtension(fi.Name),
                            FilePath = fi.FullName,
                            SizeBytes = fi.Length,
                            Is128KAligned = (fi.Length % FfuChunkAlignmentBytes) == 0
                        });
                    }

                    result.Success = result.DumpedPartitions.Count > 0;
                    if (!result.Success)
                    {
                        result.ErrorMessage = "WPinternals did not produce any .bin partition files (note: WPinternals always returns exit code 0 on CLI errors; check WPInternals.log).";
                    }
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                Program.appendLog($"[WPINTERNALS] Exception: {ex}\n");
            }

            return result;
        }

        private static int GetPartitionDisplayOrder(string partitionName)
        {
            for (int i = 0; i < RomPartitionNames.Length; i++)
            {
                if (string.Equals(partitionName, RomPartitionNames[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return RomPartitionNames.Length;
        }

        private static string FormatSize(long bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            if (mb >= 1024.0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB", mb / 1024.0);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:0.00} MB", mb);
        }

        internal static async Task PromptAndDumpPartitionsAsync(string ffuPath)
        {
            if (string.IsNullOrWhiteSpace(ffuPath) || !File.Exists(ffuPath))
                return;

            DrainConsoleInputBuffer();

            Program.WriteLine("\n[EXPORT PARTITIONS VIA WPINTERNALS]", ConsoleColor.DarkGray);
            Program.WriteLine("1. Dump ROM partitions (EFIESP, MainOS, Data)", ConsoleColor.Green);
            Program.WriteLine("2. Dump all FFU partitions", ConsoleColor.Blue);
            Program.WriteLine("3. Skip", ConsoleColor.Gray);
            Program.Write("Choice: ", ConsoleColor.Magenta);

            ConsoleKeyInfo dumpChoice;
            do
            {
                dumpChoice = Console.ReadKey(true);
            }
            while (!Program.IsNumberKeyOrNumpad(dumpChoice, new char[] { '1', '2', '3' }));
            char choiceDigit = Program.NormalizeNumberKey(dumpChoice);
            Program.Write(choiceDigit + "\n");

            if (choiceDigit == '3')
                return;

            FfuDumpScope scope = choiceDigit == '1' ? FfuDumpScope.RomPartitions : FfuDumpScope.AllPartitions;

            string wpInternalsExe = FindWPinternalsExecutable();
            while (string.IsNullOrEmpty(wpInternalsExe) || !File.Exists(wpInternalsExe))
            {
                Program.WriteLine("\nWPinternals.exe was not found automatically.", ConsoleColor.DarkYellow);
                string pickedExe = await GeneralPicker.ShowOpenFileDialogAsync(
                    "exe",
                    "Please select WPinternals.exe.",
                    "WPinternals.exe|WPinternals.exe|Executable Files (*.exe)|*.exe",
                    "Select WPinternals.exe");

                if (!string.IsNullOrWhiteSpace(pickedExe) && File.Exists(pickedExe))
                {
                    wpInternalsExe = pickedExe;
                    break;
                }

                if (!PushPackages.showRetryQuestion("WPinternals.exe path is not valid or not selected, retry?", "Skip", ConsoleColor.Red))
                {
                    return;
                }
            }
            LastSelectedPath = wpInternalsExe;
            Program.Write("WPinternals: ", ConsoleColor.DarkGray);
            Program.WriteLine(wpInternalsExe, ConsoleColor.Gray);

            while (IsWPinternalsRunning())
            {
                if (!PushPackages.showRetryQuestion("WPinternals is currently running (close it so -DumpFFU can run), retry?", "Skip", ConsoleColor.DarkYellow))
                {
                    return;
                }
            }

            string destFolder = null;
            while (string.IsNullOrWhiteSpace(destFolder))
            {
                destFolder = await GeneralPicker.ShowSelectFolderDialogAsync("Please select destination folder for unpacked partition images (.bin).");
                if (string.IsNullOrWhiteSpace(destFolder))
                {
                    if (!PushPackages.showRetryQuestion("Destination folder is not valid!, retry?", "Skip", ConsoleColor.Red))
                    {
                        return;
                    }
                }
            }

            while (true)
            {
                Program.WriteLine($"\nRunning WPinternals -DumpFFU to:\n{destFolder}", ConsoleColor.Blue);
                Program.showTitleWaitMessage(true, "WPinternals.exe -DumpFFU");
                FfuDumpResult result;
                try
                {
                    result = await Task.Run(() => DumpPartitionsWithWPinternals(wpInternalsExe, ffuPath, destFolder, scope));
                }
                finally
                {
                    Program.showTitleWaitMessage(false);
                }

                if (result.DumpedPartitions.Count > 0)
                {
                    Program.WriteLine("\n[DUMPED PARTITIONS]", ConsoleColor.Green);
                    foreach (var part in result.DumpedPartitions)
                    {
                        string alignNote = part.Is128KAligned ? "128KB aligned" : "sector aligned";
                        string line = $"  {part.Name,-16} {FormatSize(part.SizeBytes),10} ({alignNote})";
                        Program.WriteLine(line, ConsoleColor.Gray);
                    }
                }

                if (result.Success)
                {
                    Program.WriteLine($"\nPartition images ({result.DumpedPartitions.Count}) saved to:\n{destFolder}", ConsoleColor.Green);
                    Program.WriteLine("In WPinternals -> Flash -> Flash separate partitions, select EFIESP.bin / MainOS.bin / Data.bin (or flash the .ffu via Flash Custom ROM).", ConsoleColor.DarkGray);
                    break;
                }

                Program.WriteLine($"\n{result.ErrorMessage ?? "Partition dump failed."}", ConsoleColor.Red);
                if (!PushPackages.showRetryQuestion("Dump partitions via WPinternals failed, retry?", "Skip", ConsoleColor.DarkYellow))
                {
                    break;
                }

                while (IsWPinternalsRunning())
                {
                    if (!PushPackages.showRetryQuestion("WPinternals is currently running (close it so -DumpFFU can run), retry?", "Skip", ConsoleColor.DarkYellow))
                    {
                        return;
                    }
                }
            }
        }
    }
}
