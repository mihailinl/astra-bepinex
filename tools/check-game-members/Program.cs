// SPDX-License-Identifier: MIT
// Static verification that a game integration's Astra.Sdk.GameType lookups resolve in a REAL,
// installed copy of the game. It never runs the game and never executes any of its code: every
// assembly is loaded through System.Reflection.MetadataLoadContext, which reads metadata only.
//
// Usage:
//   check-game-members check <ManagedDir> <members.tsv>
//     Verifies every row of members.tsv exists, with a compatible shape, in ManagedDir. Exit code
//     0 when every row passes, 1 otherwise.
//
//   check-game-members dump <ManagedDir> <TypeName>
//     Lists every instance/static field and property of TypeName (searched across every assembly
//     in ManagedDir) with its declared type — for writing the next members.tsv row by hand.
//
// members.tsv: one row per GameType lookup an integration makes, columns separated by any run of
// whitespace (tabs or spaces — so the file can be column-aligned), '#' comments and blank lines
// ignored:
//   Type  Member  Kind  ExpectedType
// Kind is one of:
//   type      Type itself must exist (GameType.Find(...).Exists) — ExpectedType is ignored ("-")
//   static    a static field or property on Type (GameType.Static<T>)
//   instance  an instance field or property on Type, found on Type or a base (GameType.Member<T>)
//   method0   a public or non-public PARAMETERLESS instance method (not covered by GameType; an
//             integration that needs one calls System.Reflection directly against GameType.ClrType)
// ExpectedType is a short CLR type name ("Boolean", "Single", "Int32", "Vector3", "GameObject",
// "CharacterController", ...) checked by name against the member's declared type (or, for a
// method, its return type); "object" accepts any reference type (the common case for a
// game-specific type an integration only ever treats opaquely, through further GameType lookups).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CheckGameMembers;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: check-game-members check <ManagedDir> <members.tsv>");
            Console.Error.WriteLine("       check-game-members dump <ManagedDir> <TypeName> [<assembly to look in first>]");
            Console.Error.WriteLine("       check-game-members il2cpp <plugin.dll> <BepInEx/interop dir>");
            return 2;
        }

        switch (args[0])
        {
            case "check" when args.Length == 3:
                return RunCheck(args[1], args[2]) ? 0 : 1;
            case "dump" when args.Length == 3 || args.Length == 4:
                RunDump(args[1], args[2], args.Length == 4 ? args[3] : DefaultAssembly);
                return 0;
            case "il2cpp" when args.Length == 3:
                return Il2CppRefs.Run(args[1], args[2]);
            default:
                Console.Error.WriteLine("bad arguments");
                return 2;
        }
    }

    static MetadataLoadContext OpenGame(string managedDir)
    {
        var dlls = Directory.GetFiles(managedDir, "*.dll");
        if (dlls.Length == 0)
            throw new DirectoryNotFoundException($"no .dll files in '{managedDir}'");
        var resolver = new PathAssemblyResolver(dlls);
        // Old-style Unity Mono players carry mscorlib.dll as their core assembly (not
        // System.Private.CoreLib — these are .NET Framework-shaped builds).
        var coreName = dlls.Any(d => Path.GetFileNameWithoutExtension(d) == "mscorlib")
            ? "mscorlib"
            : "System.Private.CoreLib";
        var mlc = new MetadataLoadContext(resolver, coreName);
        // MetadataLoadContext.GetAssemblies() only returns assemblies LOADED so far — the resolver
        // alone does not preload anything. Force every DLL in the folder to load (metadata only;
        // nothing executes) so a type search can see all of them.
        foreach (var dll in dlls)
        {
            try { mlc.LoadFromAssemblyPath(dll); }
            catch { /* a native or non-.NET file beside the managed assemblies */ }
        }
        return mlc;
    }

    /// <summary>Where a Unity game's own code usually is; a game that names it otherwise (Valheim:
    /// assembly_valheim) says so with <c># prefer-assembly:</c> in its members file.</summary>
    const string DefaultAssembly = "Assembly-CSharp";

    /// <summary>
    /// The type named <paramref name="name"/>: in <paramref name="preferAssembly"/> first, then in the
    /// other assemblies IN NAME ORDER. <c>GetAssemblies()</c> has no stable order, so without one a name
    /// two assemblies share (Valheim's <c>Character</c>) found one type in one run and the other in the
    /// next. <paramref name="ambiguous"/> names the other assemblies that have it, when the preferred
    /// one did not decide.
    /// </summary>
    static Type? FindType(MetadataLoadContext mlc, string name, string preferAssembly, out string? ambiguous)
    {
        ambiguous = null;
        var assemblies = mlc.GetAssemblies().OrderBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase).ToList();
        var preferred = assemblies.FirstOrDefault(a => a.GetName().Name == preferAssembly);
        if (preferred != null)
        {
            var t = FindInAssembly(preferred, name);
            if (t != null) return t;
        }
        Type? found = null;
        var others = new List<string>();
        foreach (var a in assemblies)
        {
            if (a == preferred) continue;
            var t = FindInAssembly(a, name);
            if (t == null) continue;
            if (found == null) found = t;
            else others.Add(a.GetName().Name ?? "?");
        }
        if (found != null && others.Count > 0)
            ambiguous = $"'{name}' is in {found.Assembly.GetName().Name} and {string.Join(", ", others)}: {found.Assembly.GetName().Name} (first by name) was checked; name the right one with '# prefer-assembly: <name>'";
        return found;
    }

    static Type? FindInAssembly(Assembly a, string name)
    {
        try
        {
            foreach (var t in a.GetTypes())
                if (t.Name == name) return t;
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var t in ex.Types)
                if (t?.Name == name) return t;
        }
        catch
        {
            // one broken assembly in the folder must not stop the scan of every other
        }
        return null;
    }

    // Mirrors Astra.Sdk.GameType / MemberAccessor's own walk: field first, then a readable
    // non-indexer property, on the type or any base.
    static (string memberKind, Type declaredType)? FindMember(Type type, string name, bool isStatic)
    {
        var flags = (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                    | BindingFlags.Public | BindingFlags.NonPublic;
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, flags);
            if (f != null) return ("field", f.FieldType);
            var p = t.GetProperty(name, flags);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return ("property", p.PropertyType);
        }
        return null;
    }

    static MethodInfo? FindMethod0(Type type, string name, bool isStatic)
    {
        var flags = (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                    | BindingFlags.Public | BindingFlags.NonPublic;
        for (var t = type; t != null; t = t.BaseType)
        {
            var m = t.GetMethods(flags).FirstOrDefault(mi => mi.Name == name && mi.GetParameters().Length == 0);
            if (m != null) return m;
        }
        return null;
    }

    static bool TypeMatches(Type declared, string expected)
    {
        if (string.Equals(expected, "object", StringComparison.OrdinalIgnoreCase))
            return !declared.IsPrimitive || expected == declared.Name; // any reference type qualifies
        if (declared.Name == expected) return true;
        // Walk base types too (e.g. an enum's expected name is the enum itself, not its underlying int).
        for (var b = declared.BaseType; b != null; b = b.BaseType)
            if (b.Name == expected) return true;
        return false;
    }

    record Row(string Type, string Member, string Kind, string ExpectedType, int Line);

    /// <summary>The rows of a members file, and the assembly its <c># prefer-assembly: &lt;name&gt;</c> line
    /// names (<see cref="DefaultAssembly"/> without one).</summary>
    static List<Row> ReadRows(string path, out string preferAssembly)
    {
        var rows = new List<Row>();
        preferAssembly = DefaultAssembly;
        int line = 0;
        foreach (var raw in File.ReadLines(path))
        {
            line++;
            var l = raw.Trim();
            const string directive = "# prefer-assembly:";
            if (l.StartsWith(directive, StringComparison.OrdinalIgnoreCase))
            {
                preferAssembly = l.Substring(directive.Length).Trim();
                continue;
            }
            if (l.Length == 0 || l.StartsWith("#")) continue;
            // Columns may be tab- or space-separated (any run of whitespace): none of the four
            // fields (a CLR type/member name, or "-") ever contains a space itself.
            var cols = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cols.Length != 4)
                throw new FormatException($"{path}:{line}: expected 4 whitespace-separated columns, got {cols.Length}");
            rows.Add(new Row(cols[0], cols[1], cols[2], cols[3], line));
        }
        return rows;
    }

    static bool RunCheck(string managedDir, string membersPath)
    {
        var rows = ReadRows(membersPath, out var prefer);
        using var mlc = OpenGame(managedDir);
        var typeCache = new Dictionary<string, Type?>();
        int pass = 0, fail = 0;

        foreach (var row in rows)
        {
            if (!typeCache.TryGetValue(row.Type, out var type))
            {
                typeCache[row.Type] = type = FindType(mlc, row.Type, prefer, out var ambiguous);
                if (ambiguous != null) Console.WriteLine($"{membersPath}:{row.Line}: WARN  {ambiguous}");
            }

            string status;
            if (type == null)
            {
                status = $"FAIL  type '{row.Type}' not found";
            }
            else if (row.Kind == "type")
            {
                status = $"PASS  type {row.Type} exists";
            }
            else if (row.Kind == "method0")
            {
                var m = FindMethod0(type, row.Member, isStatic: false) ?? FindMethod0(type, row.Member, isStatic: true);
                status = m == null
                    ? $"FAIL  {row.Type}.{row.Member}() not found"
                    : TypeMatches(m.ReturnType, row.ExpectedType)
                        ? $"PASS  {row.Type}.{row.Member}() -> {m.ReturnType.Name}"
                        : $"FAIL  {row.Type}.{row.Member}() returns {m.ReturnType.Name}, expected {row.ExpectedType}";
            }
            else
            {
                bool isStatic = row.Kind == "static";
                var found = FindMember(type, row.Member, isStatic);
                status = found == null
                    ? $"FAIL  {row.Type}.{row.Member} ({row.Kind}) not found"
                    : TypeMatches(found.Value.declaredType, row.ExpectedType)
                        ? $"PASS  {row.Type}.{row.Member} ({row.Kind} {found.Value.memberKind}) : {found.Value.declaredType.Name}"
                        : $"FAIL  {row.Type}.{row.Member} ({row.Kind} {found.Value.memberKind}) : {found.Value.declaredType.Name}, expected {row.ExpectedType}";
            }

            if (status.StartsWith("PASS")) pass++; else fail++;
            Console.WriteLine($"{membersPath}:{row.Line}: {status}");
        }

        Console.WriteLine($"-- {pass} passed, {fail} failed, {rows.Count} total --");
        return fail == 0;
    }

    static void RunDump(string managedDir, string typeName, string preferAssembly)
    {
        using var mlc = OpenGame(managedDir);
        var type = FindType(mlc, typeName, preferAssembly, out var ambiguous);
        if (ambiguous != null) Console.WriteLine("WARN  " + ambiguous);
        if (type == null)
        {
            Console.WriteLine($"type '{typeName}' not found");
            return;
        }
        Console.WriteLine($"{type.FullName}  (base: {type.BaseType?.Name})");
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (var f in type.GetFields(all).OrderBy(f => f.IsStatic).ThenBy(f => f.Name))
            Console.WriteLine($"  {(f.IsStatic ? "static" : "instance")} field    {f.Name} : {f.FieldType.Name}");
        foreach (var p in type.GetProperties(all).OrderBy(p => p.GetMethod?.IsStatic != true).ThenBy(p => p.Name))
            if (p.CanRead && p.GetIndexParameters().Length == 0)
                Console.WriteLine($"  {(p.GetMethod?.IsStatic == true ? "static" : "instance")} property {p.Name} : {p.PropertyType.Name}");
        foreach (var m in type.GetMethods(all).Where(m => m.GetParameters().Length == 0 && !m.IsSpecialName).OrderBy(m => m.Name))
            Console.WriteLine($"  {(m.IsStatic ? "static" : "instance")} method0  {m.Name}() : {m.ReturnType.Name}");
    }
}
