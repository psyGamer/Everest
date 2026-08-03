// Keep the types between runtime and patcher distinct, without copy-pasting
#if EVEREST_MONOMOD
namespace Celeste.Mod.Helpers;
#elif EVEREST_PATCHER
namespace Celeste.Mod.Patcher;
#else
#error "Unsupported project"
#endif

/// <summary>
/// Represents a constant value, to be used for generic methods by structs implementing this interface.
/// </summary>
public interface IConst<out T>
{
    /// <summary>
    /// The value of this constant.
    /// </summary>
    public static abstract T Value { get; }
}