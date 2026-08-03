using System.IO;
using System.Reflection;

namespace Celeste.Mod.Patcher;

public static class Program {
    public static void Main(string[] args) {
        string patcherPath = typeof(Program).Assembly.Location;
        string gameDir = Path.GetDirectoryName(patcherPath)!;

        var gameAsm = Assembly.LoadFrom(Path.Combine(gameDir, "Celeste.dll"));

        // Hand execution over to Everest
        gameAsm.EntryPoint!.Invoke(null, [args]);
    }
}