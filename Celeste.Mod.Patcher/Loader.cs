using MonoMod.Utils.Cil;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using YamlDotNet.Serialization;

namespace Celeste.Mod.Patcher;

internal static class Loader {
    /// <summary>
    /// The path to the directory holding Celeste.exe
    /// </summary>
    public static string PathGame = null!;

    /// <summary>
    /// Path to Everest base location. Defaults to the game directory.
    /// </summary>
    public static string PathEverest = null!;

    /// <summary>
    /// The path to the Everest /Mods directory.
    /// </summary>
    private static string PathMods = null!;

    /// <summary>
    /// The path to the Everest /Mods/Cache directory.
    /// </summary>
    private static string PathCache = null!;

    /// <summary>
    /// The path to the Everest /Mods/blacklist.txt file.
    /// </summary>
    private static string PathBlacklist = null!;

    /// <summary>
    /// The path to the Everest /Mods/temporaryblacklist.txt file.
    /// </summary>
    public static string PathTemporaryBlacklist = null!;
    internal static string? NameTemporaryBlacklist;

    /// <summary>
    /// The path to the Everest /Mods/whitelist.txt file.
    /// </summary>
    public static string PathWhitelist = null!;
    internal static string? NameWhitelist;

    /// <summary>
    /// The currently loaded mod temporary blacklist.
    /// </summary>
    public static ReadOnlyCollection<string> TemporaryBlacklist => _TemporaryBlacklist?.AsReadOnly();
    internal static List<string> _TemporaryBlacklist;

    /// <summary>
    /// The currently loaded mod blacklist.
    /// </summary>
    public static IReadOnlyCollection<string> Blacklist => _Blacklist.ToImmutableHashSet();
    internal static HashSet<string> _Blacklist = new HashSet<string>();


    /// <summary>
    /// The currently loaded mod whitelist.
    /// </summary>
    public static ReadOnlyCollection<string> Whitelist => _Whitelist?.AsReadOnly();
    internal static List<string> _Whitelist;

    public static CoreModuleSettings CoreModuleSettings = new();

    public static void Load() {
        // Setup directory paths
        string asmPath = typeof(Program).Assembly.Location;

        PathGame = Path.GetDirectoryName(asmPath)!;
        PathEverest = PathGame;

        if (File.Exists(Path.Combine(PathGame, "EverestXDGFlag"))) {
            string dataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Directory.CreateDirectory(PathEverest = Path.Combine(dataDir, "Everest"));
            Directory.CreateDirectory(Path.Combine(dataDir, "Everest", "Mods")); // Make sure it exists before content gets initialized
        }

        Directory.CreateDirectory(PathMods = Path.Combine(PathEverest, "Mods"));
        Directory.CreateDirectory(PathCache = Path.Combine(PathMods, "Cache"));

        PathBlacklist = Path.Combine(PathMods, "blacklist.txt");
        if (File.Exists(PathBlacklist)) {
            _Blacklist = File.ReadAllLines(PathBlacklist).Select(l => (l.StartsWith("#") ? "" : l).Trim()).ToHashSet<string>();
        } else {
            using (StreamWriter writer = File.CreateText(PathBlacklist)) {
                writer.WriteLine("# This is the blacklist. Lines starting with # are ignored.");
                writer.WriteLine("# Mod folders and archives listed in this file will be disabled.");
                writer.WriteLine("ExampleFolder");
                writer.WriteLine("SomeMod.zip");
            }
        }

        if (!string.IsNullOrEmpty(NameTemporaryBlacklist)) {
            PathTemporaryBlacklist = Path.Combine(PathMods, NameTemporaryBlacklist);
            if (File.Exists(PathTemporaryBlacklist)) {
                _TemporaryBlacklist = File.ReadAllLines(PathTemporaryBlacklist).Select(l => (l.StartsWith("#") ? "" : l).Trim()).ToList();
            }
        }
    }

    // public static bool ShouldLoadFile(string file) {
    //     if (CoreModule.Settings.WhitelistFullOverride ?? false) {
    //         return Whitelist != null ? Whitelist.Contains(file) : (!Blacklist.Contains(file) && (TemporaryBlacklist == null || !TemporaryBlacklist.Contains(file)));
    //     } else {
    //         return (Whitelist != null && Whitelist.Contains(file)) || (!Blacklist.Contains(file) && (TemporaryBlacklist == null || !TemporaryBlacklist.Contains(file)));
    //     }
    // }
}