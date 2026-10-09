// SPDX-License-Identifier: MIT
// `check-game-members il2cpp <plugin.dll> <interopDir>`: which of an IL2CPP plugin's references into a
// game's interop assemblies (the game's BepInEx/interop, generated on its first run) do not exist in
// THAT game. An IL2CPP build strips every engine type and member the game never uses, and the
// interop is generated from what is left: a plugin method that names a stripped one fails to
// compile the first time it runs (TypeLoadException / MissingMethodException) — and if that method
// is on the plugin's start path, the plugin stops. A member Il2CppInterop put back but could not
// restore the body of ("Method unstripping failed") throws when called, and counts as missing too.
// Each missing reference is listed with the plugin's methods that use it — directly, through a
// generic instantiation, a catch clause, a local, or by calling a plugin method or touching a
// plugin field whose own signature names a missing type — so each can be fenced. Metadata only:
// nothing is loaded or run.
using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace CheckGameMembers;

static class Il2CppRefs
{
    public static int Run(string pluginPath, string interopDir)
    {
        // What the game HAS: "assembly|Namespace.Type" (nested: Outer/Inner) -> its members, a method
        // as its full signature (Names.Method), a field as its name.
        var have = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var interop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unrestored = 0;
        foreach (var dll in Directory.GetFiles(interopDir, "*.dll"))
        {
            using var pe = new PEReader(File.OpenRead(dll));
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) continue;
            string asm = md.GetString(md.GetAssemblyDefinition().Name);
            interop.Add(asm);
            var names = new Names();
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    if (m.RelativeVirtualAddress != 0 && Unrestored(pe, md, m))
                    {
                        unrestored++;
                        continue;
                    }
                    members.Add(Names.Method(md.GetString(m.Name), m.DecodeSignature(names, null)));
                }
                foreach (var fh in td.GetFields()) members.Add(md.GetString(md.GetFieldDefinition(fh).Name));
                have[asm + "|" + DefName(md, td)] = members;
            }
        }

        using var plugin = new PEReader(File.OpenRead(pluginPath));
        var p = plugin.GetMetadataReader();
        var pnames = new Names();
        // Every reference into the interop that the game lacks, by its metadata token.
        var missing = new Dictionary<int, string>();
        foreach (var h in p.TypeReferences)
        {
            var key = RefKey(p, h);
            if (key != null && interop.Contains(key.Split('|')[0]) && !have.ContainsKey(key))
                missing[MetadataTokens.GetToken(h)] = "type " + key;
        }
        foreach (var h in p.MemberReferences)
        {
            var mr = p.GetMemberReference(h);
            var parent = ParentKey(p, mr.Parent);
            if (parent == null || !interop.Contains(parent.Split('|')[0])) continue;
            string name = p.GetString(mr.Name);
            string member = mr.GetKind() == MemberReferenceKind.Method ? Names.Method(name, mr.DecodeMethodSignature(pnames, null)) : name;
            if (!have.TryGetValue(parent, out var members))
                missing[MetadataTokens.GetToken(h)] = $"{parent}::{member} (its type is missing)";
            else if (!members.Contains(member))
                missing[MetadataTokens.GetToken(h)] = $"{parent}::{member}";
        }
        // The plugin's OWN methods and fields whose signatures name a missing type: whatever calls or
        // touches them fails to compile too (how the old HdrpHook.TryLoad stopped the plugin in MiSide).
        foreach (var th in p.TypeDefinitions)
        {
            var td = p.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                var m = p.GetMethodDefinition(mh);
                var why = BlobTypes(p, m.Signature, missing).FirstOrDefault();
                if (why != null)
                    missing[MetadataTokens.GetToken(mh)] = $"plugin method {DefName(p, td)}.{p.GetString(m.Name)} (its signature names: {why})";
            }
            foreach (var fh in td.GetFields())
            {
                var f = p.GetFieldDefinition(fh);
                var why = BlobTypes(p, f.Signature, missing).FirstOrDefault();
                if (why != null)
                    missing[MetadataTokens.GetToken(fh)] = $"plugin field {DefName(p, td)}.{p.GetString(f.Name)} (its type names: {why})";
            }
        }

        // Which plugin methods name them.
        var users = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Use(string why, string where)
        {
            if (!users.TryGetValue(why, out var set)) users[why] = set = new SortedSet<string>(StringComparer.Ordinal);
            set.Add(where);
        }
        foreach (var th in p.TypeDefinitions)
        {
            var td = p.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                var m = p.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                var body = plugin.GetMethodBody(m.RelativeVirtualAddress);
                string where = DefName(p, td) + "." + p.GetString(m.Name);
                foreach (var token in Tokens(body.GetILBytes()!, strings: false))
                    foreach (var why in Explain(p, token, missing)) Use(why, where);
                if (!body.LocalSignature.IsNil)
                    foreach (var why in BlobTypes(p, p.GetStandaloneSignature(body.LocalSignature).Signature, missing))
                        Use(why, where + " (a local)");
                foreach (var region in body.ExceptionRegions)
                    if (region.Kind == ExceptionRegionKind.Catch && !region.CatchType.IsNil)
                        foreach (var why in Explain(p, MetadataTokens.GetToken(region.CatchType), missing))
                            Use(why, where + " (a catch clause)");
            }
        }
        foreach (var why in missing.Values.Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            Console.WriteLine("MISSING " + why);
            if (users.TryGetValue(why, out var set))
                foreach (var u in set) Console.WriteLine("    used by " + u);
        }
        int count = missing.Values.Distinct().Count();
        Console.WriteLine($"{count} missing reference(s) into {interop.Count} interop assemblies " +
            $"({unrestored} interop member(s) whose body could not be restored count as missing)");
        return count == 0 ? 0 : 1;
    }

    /// <summary>A member Il2CppInterop put back whose body it could not restore: its IL throws
    /// "Method unstripping failed".</summary>
    static bool Unrestored(PEReader pe, MetadataReader md, MethodDefinition m)
    {
        var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
        if (il == null || il.Length > 64) return false; // the stand-in is a few instructions
        foreach (var token in Tokens(il, strings: true))
            if ((token >> 24) == 0x70 && md.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF)).Contains("unstripping failed"))
                return true;
        return false;
    }

    /// <summary>The missing references a token names, directly or through a spec built on them.</summary>
    static IEnumerable<string> Explain(MetadataReader p, int token, Dictionary<int, string> missing)
    {
        if (missing.TryGetValue(token, out var direct)) yield return direct;
        var handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.TypeSpecification:
                foreach (var w in BlobTypes(p, p.GetTypeSpecification((TypeSpecificationHandle)handle).Signature, missing)) yield return w;
                break;
            case HandleKind.MethodSpecification:
                var ms = p.GetMethodSpecification((MethodSpecificationHandle)handle);
                foreach (var w in Explain(p, MetadataTokens.GetToken(ms.Method), missing)) yield return w;
                foreach (var w in BlobTypes(p, ms.Signature, missing)) yield return w;
                break;
            case HandleKind.MemberReference:
                var mr = p.GetMemberReference((MemberReferenceHandle)handle);
                if (mr.Parent.Kind == HandleKind.TypeSpecification || mr.Parent.Kind == HandleKind.TypeReference)
                    foreach (var w in Explain(p, MetadataTokens.GetToken(mr.Parent), missing)) yield return w;
                foreach (var w in BlobTypes(p, mr.Signature, missing)) yield return w;
                break;
        }
    }

    /// <summary>The missing types a signature blob mentions (any TypeDefOrRef coded index in it —
    /// found by scanning for the element types that are followed by one).</summary>
    static IEnumerable<string> BlobTypes(MetadataReader p, BlobHandle blob, Dictionary<int, string> missing)
    {
        var r = p.GetBlobReader(blob);
        var found = new List<string>();
        while (r.RemainingBytes > 0)
        {
            byte b = r.ReadByte();
            // CLASS (0x12) and VALUETYPE (0x11) are followed by a TypeDefOrRefOrSpec coded index.
            if ((b == 0x11 || b == 0x12) && r.RemainingBytes > 0)
            {
                // A byte scan, not a decoder: a 0x11/0x12 inside another item reads a junk handle,
                // which at worst fails to match.
                try
                {
                    var h = r.ReadTypeHandle();
                    if (!h.IsNil && missing.TryGetValue(MetadataTokens.GetToken(h), out var why)) found.Add(why);
                }
                catch (BadImageFormatException) { break; }
            }
        }
        return found;
    }

    static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .GroupBy(o => o.Value).ToDictionary(g => g.Key, g => g.First().OperandType);

    /// <summary>Every metadata token an IL stream's instructions carry (with
    /// <paramref name="strings"/>, ldstr's user-string tokens too).</summary>
    static IEnumerable<int> Tokens(byte[] il, bool strings)
    {
        int i = 0;
        while (i < il.Length)
        {
            short code = il[i++];
            if (code == 0xFE && i < il.Length) code = (short)(0xFE00 | il[i++]);
            if (!Operands.TryGetValue(code, out var operand)) yield break; // not IL we can walk
            switch (operand)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch:
                    int n = BitConverter.ToInt32(il, i);
                    i += 4 + 4 * n;
                    break;
                case OperandType.InlineField: case OperandType.InlineMethod: case OperandType.InlineTok:
                case OperandType.InlineType: case OperandType.InlineSig:
                    yield return BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                case OperandType.InlineString:
                    if (strings) yield return BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                default: i += 4; break; // InlineBrTarget, InlineI, ShortInlineR
            }
        }
    }

    /// <summary>Type names as both sides spell them — no assembly, nested as Outer/Inner, generic
    /// parameters by position — so an overload is matched by its whole signature, never by its
    /// parameter count alone.</summary>
    sealed class Names : ISignatureTypeProvider<string, object?>
    {
        public static string Method(string name, MethodSignature<string> sig) =>
            $"{sig.ReturnType} {name}{(sig.GenericParameterCount > 0 ? "`" + sig.GenericParameterCount : "")}({string.Join(",", sig.ParameterTypes)})";

        public string GetArrayType(string element, ArrayShape shape) => element + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string element) => element + "&";
        public string GetFunctionPointerType(MethodSignature<string> sig) => "fnptr";
        public string GetGenericInstantiation(string generic, ImmutableArray<string> args) => generic + "<" + string.Join(",", args) + ">";
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodified, bool isRequired) => unmodified;
        public string GetPinnedType(string element) => element;
        public string GetPointerType(string element) => element + "*";
        public string GetPrimitiveType(PrimitiveTypeCode code) => code.ToString();
        public string GetSZArrayType(string element) => element + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle h, byte kind) => DefName(reader, reader.GetTypeDefinition(h));
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle h, byte kind)
        {
            var key = RefKey(reader, h);
            return key == null ? reader.GetString(reader.GetTypeReference(h).Name) : key.Substring(key.IndexOf('|') + 1);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle h, byte kind) =>
            reader.GetTypeSpecification(h).DecodeSignature(this, context);
    }

    static string DefName(MetadataReader md, TypeDefinition td)
    {
        string name = md.GetString(td.Name);
        var outer = td.GetDeclaringType();
        if (!outer.IsNil) return DefName(md, md.GetTypeDefinition(outer)) + "/" + name;
        string ns = md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>"assembly|Namespace.Type" of a type reference, or null when it is not in another assembly.</summary>
    static string? RefKey(MetadataReader md, TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        string name = md.GetString(tr.Name);
        switch (tr.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                string ns = md.GetString(tr.Namespace);
                string asm = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
                return asm + "|" + (ns.Length == 0 ? name : ns + "." + name);
            case HandleKind.TypeReference:
                var outer = RefKey(md, (TypeReferenceHandle)tr.ResolutionScope);
                return outer == null ? null : outer + "/" + name;
            default:
                return null;
        }
    }

    /// <summary>The key of a member reference's declaring type — through a generic instantiation to
    /// its generic type.</summary>
    static string? ParentKey(MetadataReader md, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeReference) return RefKey(md, (TypeReferenceHandle)parent);
        if (parent.Kind != HandleKind.TypeSpecification) return null;
        var r = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (r.ReadByte() != 0x15) return null; // GENERICINST
        r.ReadByte();                          // CLASS or VALUETYPE
        var generic = r.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeReference ? RefKey(md, (TypeReferenceHandle)generic) : null;
    }
}
