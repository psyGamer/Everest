#pragma warning disable CS0626 // Method, operator, or accessor is marked external and has no attributes on it

using Mono.Cecil;
using MonoMod;
using MonoMod.Cil;
using MonoMod.InlineRT;
using MonoMod.Utils;
using System;

namespace Microsoft.Xna.Framework {

    [GameDependencyPatch("FNA")]
    public partial class patch_Game {

        [MonoModIfFlag("Headless")]
        [MonoModConstructor]
        [MonoModIgnore]
        [PatchGameCtor]
        public extern void ctor();
    }
}

namespace MonoMod {
    /// <summary>
    /// A patch replacing the GameWindow instance
    /// </summary>
    [MonoModCustomMethodAttribute(nameof(MonoModRules.PatchGameCtor))]
    class PatchGameCtor : Attribute { }

    /// <summary>
    /// A patch removing registering the graphics adapter
    /// </summary>
    [MonoModCustomMethodAttribute(nameof(MonoModRules.PatchGameBeforeLoop))]
    class PatchGameBeforeLoop : Attribute { }

    static partial class MonoModRules {
        public static void PatchGameCtor(ILContext context, CustomAttribute attrib) {
            ILCursor cursor = new ILCursor(context);

            TypeDefinition t_HeadlessWindow = MonoModRule.Modder.FindType("Microsoft.Xna.Framework.HeadlessWindow").Resolve();
            MethodReference m_HeadlessWindow_ctor = MonoModRule.Modder.Module.ImportReference(t_HeadlessWindow.FindMethod(".ctor"));

            // Replace 'FNAPlatform.CreateWindow()' with 'new HeadlessWindow()'
            cursor.GotoNext(instr => instr.MatchLdsfld("Microsoft.Xna.Framework.FNAPlatform", "CreateWindow"));
            cursor.RemoveRange(2);
            cursor.EmitNewobj(m_HeadlessWindow_ctor);
        }

        public static void PatchGameBeforeLoop(ILContext context, CustomAttribute attrib) {
            ILCursor cursor = new ILCursor(context);

            // Remove 'this.currentAdapter = FNAPlatform.RegisterGame(this);'
            cursor.RemoveRange(5);
        }
    }
}
