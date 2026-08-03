using Celeste.Mod.Core;
using Celeste.Mod.Helpers;
using Monocle;
using MonoMod;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using YamlDotNet.Serialization;

namespace Celeste.Mod {
    /// <summary>
    /// RUN AWAY. TURN AROUND. GO TO CELESTE'S MAIN FUNCTION INSTEAD.
    /// </summary>
    internal static class BOOT {

        [MakeEntryPoint]
        private static void Main(string[] args) {
            try {
                // 0.1 parses into 1 in regions using ,
                // This also somehow sets the exception message language to English.
                CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
                CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
                Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

                // SELinux can cause weird game corruption-like symptoms when we lack the execheap permission
                // So probe for it before continuing to boot on Linux
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                    [DllImport("libc", SetLastError = true)]
                    static extern nuint getpagesize();

                    [DllImport("libc", SetLastError = true)]
                    static extern int mprotect(IntPtr ptr, nuint len, int prot);
                    const int PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;

                    //Figure out page size
                    nuint pageSize = getpagesize();

                    //Allocate a bit of memory on the heap
                    IntPtr heapAlloc = Marshal.AllocHGlobal(123);
                    IntPtr heapPage = heapAlloc & ~((nint)(pageSize - 1));

                    //Try to make it executable
                    if (mprotect(heapPage, pageSize, PROT_READ | PROT_WRITE | PROT_EXEC) < 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "SELinux execheap probe failed! Please ensure Everest has this permission, then try again");

                    //Cleanup
                    if (mprotect(heapPage, pageSize, PROT_READ | PROT_WRITE) < 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to revert memory permissions after SELinux execheap probe");

                    Marshal.FreeHGlobal(heapAlloc);
                }

                // Load the compatibility mode and colored logging settings
                Everest.CompatibilityMode = Everest.CompatMode.None;
                Logger.EnableColorizedLogging = true;
                try {
                    string path = patch_UserIO.GetSaveFilePath("modsettings-Everest");
                    if (File.Exists(path)) {
                        using Stream stream = File.OpenRead(path);
                        using StreamReader reader = new StreamReader(stream);
                        Dictionary<object, object> settings = new Deserializer().Deserialize<Dictionary<object, object>>(reader);

                        if (settings != null) {
                            if (settings.TryGetValue(nameof(CoreModuleSettings.CompatibilityMode), out object val)) {
                                Everest.CompatibilityMode = Enum.Parse<Everest.CompatMode>((string) val);
                                Console.WriteLine($"Loaded compatibility mode setting: {Everest.CompatibilityMode}");
                            }
                            if (settings.TryGetValue(nameof(CoreModuleSettings.ColorizedLogging), out val))
                                Logger.EnableColorizedLogging = bool.Parse((string) val);
                        }
                    }
                } catch (Exception ex) {
                    LogError("COMPAT-MODE-LOAD", ex);
                    goto Exit;
                }

                patch_Celeste.Main(args);

                if (AppDomain.CurrentDomain.GetData("EverestRestart") as bool? ?? false) {
                    // Restart the original process
                    // This is as fast as the old "fast restarts" were
                    StartCelesteProcess();
                    goto Exit;
                } else if (Everest.RestartVanilla) {
                    // Start the vanilla process
                    StartVanilla();
                    goto Exit;
                }
            } catch (Exception e) {
                LogError("BOOT-CRITICAL", e);
                goto Exit;
            }


            // Needed because certain graphics drivers and native libs like to hang around for no reason.
            // Vanilla does the same on macOS and Linux, but NVIDIA on Linux likes to waste time in DrvValidateVersion.
            Exit:
            Console.WriteLine("Exiting Celeste process");
            Environment.Exit(0);
        }

        public static void LogError(string tag, Exception e) {
            Logger.LogDetailed(e, tag);

            if (Debugger.IsAttached)
                Debugger.Break();

            try {
                ErrorLog.Write(e.ToString());
                ErrorLog.Open();
            } catch { }
        }

        [MonoModIgnore]
        private static extern bool RestartViaLauncher();

        [MonoModIfFlag("Steamworks")]
        [MonoModPatch("RestartViaLauncher")]
        [MonoModReplace]
        private static bool RestartViaSteam() {
            return SteamAPI.RestartAppIfNecessary(new AppId_t(504230));
        }

        [MonoModIfFlag("NoLauncher")]
        [MonoModPatch("RestartViaLauncher")]
        [MonoModReplace]
        private static bool RestartViaNoLauncher() {
            return false;
        }

        [SupportedOSPlatform("windows")]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        public static Process StartCelesteProcess(string gameDir = null, bool clearFNAEnv = true) {
            gameDir ??= AppContext.BaseDirectory;

            Process game = new Process();

            game.StartInfo.FileName = Path.Combine(gameDir,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Celeste.exe" :
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Celeste" :
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "Celeste" :
                throw new Exception("Unknown OS platform")
            );
            game.StartInfo.WorkingDirectory = gameDir;

            if (clearFNAEnv) {
                game.StartInfo.Environment.Clear();
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) {
                    string name = (string) entry.Key;
                    if (name.StartsWith("FNA_") || name.StartsWith("FNA3D_"))
                        continue;
                    game.StartInfo.Environment.Add(name, (string) entry.Value);
                }
            }

            Regex escapeArg = new Regex(@"(\\+)$");
            game.StartInfo.Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(s => "\"" + escapeArg.Replace(s, @"$1$1") + "\""));

            game.Start();
            return game;
        }

        public static void StartVanilla() {
            // Revert native library path to prevent accidentally messing with vanilla
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                SetDllDirectory(null);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", null);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Environment.SetEnvironmentVariable("DYLD_LIBRARY_PATH", null);

            // Don't clear FNA vars to preserve FNA compat mode
            StartCelesteProcess(Path.Combine(AppContext.BaseDirectory, "orig"), clearFNAEnv: false);
        }

    }
}
