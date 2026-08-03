namespace Celeste.Mod {
    /// <summary>
    /// Mod configuration, mirroring the data in your config.yaml
    /// </summary>
    public sealed class EverestModuleConfig {

        public sealed class MethodIdentifier {
            /// <summary>
            /// The fully qualified type which contains the target methods.
            /// </summary>
            /// <example>Celeste.Player</example>
            public string Type { get; set; }
            /// <summary>
            /// List of methods inside the type which should be targeted.
            /// </summary>
            /// <example>System.Boolean CanUnDuckAt(Microsoft.Xna.Framework.Vector2)</example>
            public string[] Methods { get; set; }
        }

        /// <summary>
        /// List of methods which should be prevented from being inlining.
        /// This should be specified for all methods which are being hooked by this mod.
        /// </summary>
        public MethodIdentifier[] PreventInlining { get; set; }
    }
}