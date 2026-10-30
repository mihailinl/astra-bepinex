// SPDX-License-Identifier: MIT
namespace Doorstop
{
    /// <summary>Doorstop 4's own invocation ABI (design §2.4): it loads whatever assembly
    /// `DOORSTOP_INVOKE_DLL_PATH` names and calls exactly this method — `static void
    /// Doorstop.Entrypoint.Start()`, zero parameters, found by a Mono method-desc search for the
    /// full name "Doorstop.Entrypoint:Start" (not by this method's accessibility, which Doorstop's
    /// native embedding call bypasses; it is public here only so Astra.Chain.ChainCore's own
    /// reflection lookup over a THIRD PARTY's assembly — same method, same name — needs no special
    /// case for "is it ours or theirs").</summary>
    public static class Entrypoint
    {
        public static void Start()
        {
            global::Astra.Chain.ChainCore.Run();
        }
    }
}
