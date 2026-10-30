// SPDX-License-Identifier: MIT
namespace Astra.Chain
{
    /// <summary>Doorstop 3's own invocation ABI (design §2.4, verified against UnityDoorstop
    /// v3.4.1.0 `Proxy/main.c`): it loads whatever assembly `DOORSTOP_INVOKE_DLL_PATH` names and
    /// finds the FIRST method literally called `Main` anywhere in the assembly's metadata (a Mono
    /// method-desc search for `"*:Main"`, type wildcarded, method name exact) — never the PE
    /// entry-point token, so this stays an ordinary class library. It must be static (Doorstop 3
    /// invokes it with a null instance) and take either zero parameters or one `string[]` (Doorstop
    /// 3 only special-cases a 1-parameter signature, passing an empty array). This is the ONLY
    /// method named `Main` in this assembly, so Doorstop 3's wildcard search cannot find another.</summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            ChainCore.Run();
        }
    }
}
