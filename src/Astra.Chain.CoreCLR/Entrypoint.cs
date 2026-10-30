// SPDX-License-Identifier: MIT
namespace Doorstop
{
    /// <summary>Doorstop's CoreCLR host ABI (design §4.3; confirmed against UnityDoorstop master's
    /// own <c>il2cpp_doorstop_bootstrap</c>, gi-bugs/src/doorstop/src_bootstrap.c, and against the
    /// real be.788 <c>BepInEx.Unity.IL2CPP.dll</c>'s own <c>Doorstop.Entrypoint.Start()</c>,
    /// disassembled from gi-bugs/packs/x/…/BepInExPack_IL2CPP-6.0.788): Doorstop's
    /// <c>coreclr_create_delegate</c> loads whatever assembly <c>DOORSTOP_INVOKE_DLL_PATH</c> names
    /// (by its file name, resolved through the APP_PATHS it just initialized CoreCLR with) and
    /// creates a delegate for exactly this method — <c>static void Doorstop.Entrypoint.Start()</c>,
    /// zero parameters, public (the native hosting API only finds a PUBLIC static method; the
    /// enclosing class's own accessibility does not matter — BepInEx's own Doorstop.Entrypoint
    /// classes, Mono and CoreCLR alike, are `internal`, matching this one). Public here only so
    /// Astra.Chain.ChainCore's own reflection lookup over a THIRD PARTY's assembly — same method,
    /// same name — needs no special case for "is it ours or theirs" (mirrors
    /// Astra.Chain/Entrypoint.cs, the Mono half).</summary>
    internal static class Entrypoint
    {
        public static void Start()
        {
            global::Astra.Chain.ChainCore.Run();
        }
    }
}
