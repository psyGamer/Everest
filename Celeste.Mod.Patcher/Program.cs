using Mono.Cecil;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Celeste.Mod.Patcher;

internal static class Program {
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    public static void Main(string[] args) {
        string everestPath = typeof(Program).Assembly.Location;
        Loader.PathGame = Path.GetDirectoryName(everestPath)!;

        // Determine if we are using a Steam install
        string origExe = Path.Combine(Loader.PathGame, "orig", "Celeste.exe");
        var origModule = ModuleDefinition.ReadModule(origExe);
        bool isSteamworks = origModule.AssemblyReferences.Any(a => a.Name.Contains("Steamworks"));

        // Launching Celeste from a shortcut can sometimes set cwd to System32 on Windows.
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            Environment.CurrentDirectory = Loader.PathGame;

        // Required for native libs to be properly picked up
        SetupNativeLibPaths();

        if (isSteamworks) {
            try {
                if (SteamAPI.RestartAppIfNecessary(new AppId_t(504230)))
                    return;
            } catch {
                // ignore
            }
        }

        if (File.Exists("everest-launch.txt")) {
            args =
                File.ReadAllLines("everest-launch.txt")
                    .Select(l => l.Trim())
                    .Where(l => !l.StartsWith("#"))
                    .SelectMany(l => l.Split(' '))
                    .Concat(args)
                    .ToArray();
        } else {
            using StreamWriter writer = File.CreateText("everest-launch.txt");

            writer.WriteLine("# Add any Everest launch flags here.");
            writer.WriteLine("# Lines starting with # are ignored.");
            writer.WriteLine("# All options here are disabled by default.");
            writer.WriteLine("# Full list: https://github.com/EverestAPI/Resources/wiki/Command-Line-Arguments");
            writer.WriteLine();
            writer.WriteLine("# Windows only: open a separate log console window.");
            writer.WriteLine("#--console");
            writer.WriteLine();
            writer.WriteLine("# FNA only: force OpenGL (might be necessary to bypass a load crash on some PCs).");
            writer.WriteLine("#--graphics OpenGL");
            writer.WriteLine();
            writer.WriteLine("# Change default log level (verbose will print all logs).");
            writer.WriteLine("#--loglevel verbose");

            if (File.Exists("launch.txt")) {
                using (StreamReader reader = File.OpenText("launch.txt")) {
                    writer.WriteLine();
                    writer.WriteLine();
                    writer.WriteLine("# The following options are migrated from the old launch.txt and force-disabled.");
                    writer.WriteLine("# Some of them might not work anymore or cause unwanted effects.");
                    writer.WriteLine();
                    writer.WriteLine();
                    for (string? line; (line = reader.ReadLine()) != null;) {
                        writer.Write("#");
                        writer.WriteLine(line);
                    }
                }

                File.Delete("launch.txt");
            }
        }

        if (File.Exists("everest-env.txt")) {
            foreach (string line in File.ReadAllLines("everest-env.txt")) {
                if (line.StartsWith("#"))
                    continue;

                int index = line.IndexOf('=');
                if (index == -1)
                    continue;

                string key = line.Substring(0, index).Trim();
                if (key.StartsWith("'") && key.EndsWith("'"))
                    key = key.Substring(1, key.Length - 2);

                string value = line.Substring(index + 1).Trim();
                if (value.StartsWith("'") && value.EndsWith("'"))
                    value = value.Substring(1, value.Length - 2);

                if (key.EndsWith("!")) {
                    key = key.Substring(0, key.Length - 1);
                } else {
                    value = value
                        .Replace("\\r", "\r")
                        .Replace("\\n", "\n")
                        .Replace($"${{{key}}}", Environment.GetEnvironmentVariable(key) ?? "");
                }

                Environment.SetEnvironmentVariable(key, value);
            }
        }

        // Parse relevant arguments
        Queue<string> queue = new(args);
        while (queue.Count > 0) {
            string arg = queue.Dequeue();

            if (arg == "--whitelist" && queue.Count >= 1)
                Loader.NameWhitelist = queue.Dequeue();

            else if (arg == "--blacklist" && queue.Count >= 1)
                Loader.NameTemporaryBlacklist = queue.Dequeue();

            else if (arg == "--loglevel" && queue.Count >= 1) {
                if (Enum.TryParse(queue.Dequeue(), ignoreCase: true, out LogLevel level))
                    Logger.SetLogLevelFromSettings("", level);
            }
        }

        // Load the compatibility mode and colored logging settings
        string savePath = GetSavePath("Saves");
        string coreModuleSettings = Path.Combine(savePath, "modsettings-Everest.celeste");
        if (File.Exists(coreModuleSettings)) {
            using Stream stream = File.OpenRead(coreModuleSettings);
            using StreamReader reader = new(stream);

            Loader.CoreModuleSettings = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<CoreModuleSettings>(reader);
        }

        // Handle the compatibility modes here, so that vanilla is also affected
        if (Loader.CoreModuleSettings.CompatibilityMode == CoreModuleSettings.CompatMode.LegacyFNA) {
            Environment.SetEnvironmentVariable("FNA3D_D3D11_FORCE_BITBLT", "1");
            Environment.SetEnvironmentVariable("FNA3D_D3D11_NO_EXCLUSIVE_FULLSCREEN", "1");
        } else if (!Loader.CoreModuleSettings.D3D11UseExclusiveFullscreen) {
            Environment.SetEnvironmentVariable("FNA3D_D3D11_NO_EXCLUSIVE_FULLSCREEN", "1");
        }

        if (Loader.CoreModuleSettings.D3D11UseExclusiveFullscreen)
            Console.WriteLine("Enabling D3D11 exclusive fullscreen support");

        // Start vanilla if instructed to
        string vanillaDummy = Path.Combine(Loader.PathGame, "nextLaunchIsVanilla.txt");
        if (File.Exists(vanillaDummy) || args.Contains("--vanilla")) {
            File.Delete(vanillaDummy);
            StartVanilla();
            goto Exit;
        }

        // Setup logging
        string logfile = Environment.GetEnvironmentVariable("EVEREST_LOG_FILENAME") ?? "log.txt";

        // Only applying log rotation on default name, feel free to improve LogRotationHelper to deal with custom log file names...
        if (logfile == "log.txt" && File.Exists("log.txt")) {
            if (new FileInfo("log.txt").Length > 0) {
                // move the old log.txt to the LogHistory folder.
                // note that the cleanup will only be done when the core module is loaded: the settings aren't even loaded right now,
                // so we don't know how many files we should keep.
                if (!Directory.Exists("LogHistory")) {
                    Directory.CreateDirectory("LogHistory");
                }

                File.Move("log.txt", Path.Combine("LogHistory", LogRotationHelper.GetFileNameByDate(File.GetLastWriteTime("log.txt"))));
            } else {
                // log is empty! (this actually happens more often than you'd think, because of Steam re-opening Celeste)
                // just delete it.
                File.Delete("log.txt");
            }
        } else {
            // check if log filename is allowed
            Regex regexBadCharacter = new Regex("[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]");
            Match match = regexBadCharacter.Match(logfile);

            if (match.Success) {
                StringBuilder errorText = new StringBuilder($"Custom log filename set in EVEREST_LOG_FILENAME=\"{logfile}\" contains invalid character(s): ", 100);

                while (match.Success) {
                    foreach (Capture c in match.Groups[0].Captures)
                        errorText.Append(c);

                    match = match.NextMatch();
                    if (match.Success)
                        errorText.Append(" ");
                }

                throw new ArgumentException(errorText.ToString());
            }

            if (!logfile.EndsWith(".txt"))
                logfile += ".txt";
        }

        bool allocatedConsole = false;
        if (args.Contains("--console") && RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            AllocConsole();

            // Invalidate console streams
            typeof(Console).GetField("s_in", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
            typeof(Console).GetField("s_out", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
            typeof(Console).GetField("s_error", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);

            allocatedConsole = true;
        }

        if (args.Contains("--nolog")) {
            Process();
        } else {
            using FileStream fileStream = new(logfile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using StreamWriter fileWriter = new(fileStream, Console.OutputEncoding);
            using LogWriter logWriter = new(Console.Out, Console.Error, fileWriter);

            Logger.outWriter = logWriter.STDOUT.Stream;
            Logger.logWriter = logWriter.File;

            // Setup Windows VT support as early as possible, to avoid escape codes being printed
            if (allocatedConsole && Logger.EnableColorizedLogging && !Logger.TryEnableWindowsVTSupport()) {
                Logger.Error("core", "Failed to enable Windows VT support!");
            }

            Process();
        }

        // Hand execution over to Everest
        var gameAsm = Assembly.LoadFrom(Path.Combine(Loader.PathGame, "Celeste.dll"));
        gameAsm.EntryPoint!.Invoke(null, [args]);

        // Needed because certain graphics drivers and native libs like to hang around for no reason.
        // Vanilla does the same on macOS and Linux, but NVIDIA on Linux likes to waste time in DrvValidateVersion.
        Exit:
        Console.WriteLine("Exiting Celeste process");
        Environment.Exit(0);
    }

    private static void SetupNativeLibPaths() {
        // macOS SIP Steam overlay hack (taken from the Linux launcher script - is this required?)
        bool didApplySteamSIPHack = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAM_DYLD_INSERT_LIBRARIES")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DYLD_INSERT_LIBRARIES"))
           ) {
            Console.WriteLine("Applying Steam DYLD_INSERT_LIBRARIES...");
            Environment.SetEnvironmentVariable("DYLD_INSERT_LIBRARIES", Environment.GetEnvironmentVariable("STEAM_DYLD_INSERT_LIBRARIES"));
            didApplySteamSIPHack = true;
        }

        // This is a bit hacky, but I'm not getting MiniInstaller to set an rpath ._.
        static void EnsureLibPathEnvVarSet(string envVar, string libPath) {
            libPath = Path.GetFullPath(libPath);

            string[] ldPath = Environment.GetEnvironmentVariable(envVar)?.Split(":") ?? Array.Empty<string>();
            if (!ldPath.Any(path => !string.IsNullOrWhiteSpace(path) && Path.GetFullPath(path) == libPath)) {
                Environment.SetEnvironmentVariable(envVar, $"{libPath}:{Environment.GetEnvironmentVariable(envVar)}");
                Console.WriteLine($"Restarting with {envVar}=\"{Environment.GetEnvironmentVariable(envVar)}\"...");

                Process proc = StartCelesteProcess();
                proc.WaitForExit();
                Environment.Exit(proc.ExitCode);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            SetDllDirectory(Path.Combine(AppContext.BaseDirectory, $"lib64-win-{(Environment.Is64BitProcess ? "x64" : "x86")}")); // Windows is the only platform with an API like this

            // Register an unmanaged DLL resolver so that we can take redirect fmod.dll to fmod64.dll
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += static (_, name) => {
                if (!name.Equals("fmod", StringComparison.OrdinalIgnoreCase) && !name.Equals("fmod.dll", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;

                return NativeLibrary.Load("fmod64.dll");
            };
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            EnsureLibPathEnvVarSet("LD_LIBRARY_PATH", Path.Combine(AppContext.BaseDirectory, "lib64-linux"));
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            EnsureLibPathEnvVarSet("DYLD_LIBRARY_PATH", Path.Combine(AppContext.BaseDirectory, "lib64-osx"));

        // If we got here without restarting the process, restart it now if required
        if (didApplySteamSIPHack) {
            Process proc = StartCelesteProcess();
            proc.WaitForExit();
            Environment.Exit(proc.ExitCode);
        }
    }

    private static Process StartCelesteProcess(string? gameDir = null) {
        gameDir ??= AppContext.BaseDirectory;

        Process game = new Process();

        game.StartInfo.FileName = Path.Combine(gameDir,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Celeste.exe" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Celeste" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Celeste" :
            throw new Exception("Unknown OS platform")
        );
        game.StartInfo.WorkingDirectory = gameDir;

        Regex escapeArg = new(@"(\\+)$");
        game.StartInfo.Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(s => "\"" + escapeArg.Replace(s, @"$1$1") + "\""));

        game.Start();
        return game;
    }

    private static void StartVanilla() {
        // Revert native library path to prevent accidentally messing with vanilla
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            SetDllDirectory(null);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", null);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Environment.SetEnvironmentVariable("DYLD_LIBRARY_PATH", null);

        StartCelesteProcess(Path.Combine(AppContext.BaseDirectory, "orig"));
    }

    private static string GetSavePath(string dir) {
        string? env = Environment.GetEnvironmentVariable("EVEREST_SAVEPATH");
        if (!string.IsNullOrEmpty(env))
            return Path.Combine(env, dir);

        try {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                string? home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (!string.IsNullOrEmpty(home))
                    return Path.Combine(home, "Celeste/" + dir);

                home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home))
                    return Path.Combine(home, ".local/share/Celeste/" + dir);
            } else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
                string? home = Environment.GetEnvironmentVariable("HOME");
                if (!string.IsNullOrEmpty(home))
                    return Path.Combine(home, "Library/Application Support/Celeste/" + dir);
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dir);
        } catch (NotSupportedException) {
            return Path.Combine(Loader.PathGame, dir);
        }
    }

    private static void Process() {
        Logger.Info("hihi", ":3");
        Loader.Load();
        Logger.Error("haha", ":3");
    }
}