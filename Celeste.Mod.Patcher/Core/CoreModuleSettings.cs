using System.Runtime.InteropServices;

namespace Celeste.Mod.Patcher;

// Stripped down version of the actual one from Celeste.Mod.mm
// It can't be included directly, due to references to both Celeste and FNA
internal sealed class CoreModuleSettings {
    public bool? WhitelistFullOverride { get; set; } = null;

    public CompatMode CompatibilityMode { get; set; } = CompatMode.None;
    public bool D3D11UseExclusiveFullscreen { get; set; }

    private bool _ColorizedLogging = true;
    public bool ColorizedLogging {
        get => _ColorizedLogging;
        set {
            if (value && RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Logger.TryEnableWindowsVTSupport()) {
                Logger.Error("core", "Failed to enable Windows VT support!");
            }
            _ColorizedLogging = value;
            Logger.EnableColorizedLogging = value;
        }
    }

    public enum CompatMode {
        None,
        LegacyXNA, // 61 FPS jank
        LegacyFNA  // Artificial input latency
    }
}