// Reads which types and members of this build (Assembly-CSharp, Memoria.Prime) the mod's compiled scripts
// (Memoria.Scripts.HadesWorkshopMod.dll) use, and checks a build still has each of them with the same signature.
// Loaded by Update-ModReferences.ps1 and Test-ModReferences.ps1 (Add-Type: C# 5, .NET Framework).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

public static class ModReferences
{
    // The build's own assemblies: references into these are recorded and checked
    public static readonly string[] Ours = { "Assembly-CSharp", "Memoria.Prime" };

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static string[] s_dirs;
    private static readonly Dictionary<string, Assembly> s_loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assemblies are found in these folders, in order (the build's Output first, then References).</summary>
    public static void Setup(string[] dirs)
    {
        s_dirs = dirs;
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            return Load(name);
        };
    }

    private static Assembly Load(string name)
    {
        Assembly a;
        if (s_loaded.TryGetValue(name, out a))
            return a;
        foreach (string d in s_dirs)
        {
            string p = Path.Combine(d, name + ".dll");
            if (File.Exists(p))
            {
                a = Assembly.LoadFrom(p);
                s_loaded[name] = a;
                return a;
            }
        }
        return null;
    }

    private static bool IsOurs(Type t)
    {
        return t != null && Array.IndexOf(Ours, t.Assembly.GetName().Name) >= 0;
    }

    // ---- Names ------------------------------------------------------------------------------------------------

    /// <summary>A type's name as written in the list: Ns.Type, Outer+Nested, Ns.Gen`1&lt;Arg&gt;, T[], T&amp;; generic
    /// parameters by position (!0 a type's, !!0 a method's), so renaming T changes nothing.</summary>
    public static string Name(Type t)
    {
        if (t.IsGenericParameter)
            return (t.DeclaringMethod != null ? "!!" : "!") + t.GenericParameterPosition;
        if (t.IsByRef)
            return Name(t.GetElementType()) + "&";
        if (t.IsPointer)
            return Name(t.GetElementType()) + "*";
        if (t.IsArray)
            return Name(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            Type[] args = t.GetGenericArguments();
            string[] names = new string[args.Length];
            for (int i = 0; i < args.Length; i++)
                names[i] = Name(args[i]);
            return Name(t.GetGenericTypeDefinition()) + "<" + string.Join(",", names) + ">";
        }
        return t.FullName ?? t.Name;
    }

    private static string Params(MethodBase m)
    {
        ParameterInfo[] ps = m.GetParameters();
        string[] names = new string[ps.Length];
        for (int i = 0; i < ps.Length; i++)
            names[i] = Name(ps[i].ParameterType);
        return "(" + string.Join(",", names) + ")";
    }

    private static string Scope(bool isStatic) { return isStatic ? "static" : "instance"; }
    private static string Access(bool isPublic) { return isPublic ? "public" : "nonpublic"; }

    /// <summary>A member's line in the list (tab-separated):
    ///   type    asm  Type
    ///   field   asm  Type  name  static|instance  public|nonpublic  FieldType
    ///   method  asm  Type  name  static|instance  public|nonpublic  (Params)  ReturnType  [generic arity]
    ///   ctor    asm  Type  .ctor static|instance  public|nonpublic  (Params)</summary>
    public static string Row(MemberInfo mi)
    {
        Type dt = mi.DeclaringType;
        string asm = dt.Assembly.GetName().Name;
        FieldInfo f = mi as FieldInfo;
        if (f != null)
            return string.Join("\t", new[] { "field", asm, Name(dt), f.Name, Scope(f.IsStatic), Access(f.IsPublic), Name(f.FieldType) });
        ConstructorInfo c = mi as ConstructorInfo;
        if (c != null)
            return string.Join("\t", new[] { "ctor", asm, Name(dt), c.Name, Scope(c.IsStatic), Access(c.IsPublic), Params(c) });
        MethodInfo m = mi as MethodInfo;
        if (m != null)
        {
            string row = string.Join("\t", new[] { "method", asm, Name(dt), m.Name, Scope(m.IsStatic), Access(m.IsPublic), Params(m), Name(m.ReturnType) });
            return m.IsGenericMethodDefinition ? row + "\t`" + m.GetGenericArguments().Length : row;
        }
        return null;
    }

    // ---- Extract ----------------------------------------------------------------------------------------------

    private static readonly OpCode[] s_one = new OpCode[256], s_two = new OpCode[256];

    static ModReferences()
    {
        foreach (FieldInfo fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode op = (OpCode)fi.GetValue(null);
            ushort v = (ushort)op.Value;
            if (op.Size == 1)
                s_one[v] = op;
            else
                s_two[v & 0xFF] = op;
        }
    }

    public sealed class Result
    {
        public readonly SortedDictionary<string, SortedSet<string>> Rows = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        public readonly List<string> Problems = new List<string>();
    }

    /// <summary>Every type and member of the build the mod's DLL uses (in its code, signatures, base types,
    /// interfaces and overrides), each with the mod types that use it. Problems: references the given build can't
    /// resolve (already missing from it).</summary>
    public static Result Extract(string modDll)
    {
        Result r = new Result();
        Assembly mod = Assembly.LoadFrom(modDll);
        Type[] types;
        try { types = mod.GetTypes(); }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types;
            foreach (Exception le in e.LoaderExceptions)
                if (le != null)
                    r.Problems.Add("loading the mod's types: " + le.Message);
        }
        foreach (Type t in types)
        {
            if (t == null)
                continue;
            string user = t.FullName;
            AddType(r, t.BaseType, user);
            foreach (Type i in t.GetInterfaces())
            {
                if (!IsOurs(i))
                    continue;
                AddType(r, i, user);
                // An interface of ours the mod implements: every method of it must stay as it is
                Type def = i.IsGenericType ? i.GetGenericTypeDefinition() : i;
                foreach (MethodInfo im in def.GetMethods(All))
                    AddMember(r, im, user);
            }
            foreach (FieldInfo f in t.GetFields(All))
                AddType(r, f.FieldType, user);
            foreach (PropertyInfo p in t.GetProperties(All))
                AddType(r, p.PropertyType, user);
            List<MethodBase> methods = new List<MethodBase>();
            methods.AddRange(t.GetMethods(All));
            methods.AddRange(t.GetConstructors(All));
            foreach (MethodBase m in methods)
            {
                string where = user + "." + m.Name;
                foreach (ParameterInfo p in m.GetParameters())
                    AddType(r, p.ParameterType, user);
                MethodInfo mi = m as MethodInfo;
                if (mi != null)
                {
                    AddType(r, mi.ReturnType, user);
                    MethodInfo baseDef = mi.GetBaseDefinition();
                    if (baseDef != null && baseDef != mi && IsOurs(baseDef.DeclaringType))
                        AddMember(r, baseDef, user); // an override of one of ours
                }
                MethodBody body;
                try { body = m.GetMethodBody(); }
                catch (Exception e) { r.Problems.Add(where + ": " + e.Message); continue; }
                if (body == null)
                    continue;
                foreach (LocalVariableInfo lv in body.LocalVariables)
                    AddType(r, lv.LocalType, user);
                ScanIl(r, m, body.GetILAsByteArray(), where, user);
            }
        }
        return r;
    }

    private static void ScanIl(Result r, MethodBase m, byte[] il, string where, string user)
    {
        Module module = m.Module;
        Type[] typeArgs = m.DeclaringType != null && m.DeclaringType.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
        Type[] methodArgs = m.IsGenericMethod ? m.GetGenericArguments() : null;
        int i = 0;
        while (i < il.Length)
        {
            OpCode op = il[i] == 0xFE ? s_two[il[i + 1]] : s_one[il[i]];
            i += il[i] == 0xFE ? 2 : 1;
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    i += 1;
                    break;
                case OperandType.InlineVar:
                    i += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    i += 8;
                    break;
                case OperandType.InlineSwitch:
                    i += 4 + 4 * BitConverter.ToInt32(il, i);
                    break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    int token = BitConverter.ToInt32(il, i);
                    i += 4;
                    MemberInfo target;
                    try { target = module.ResolveMember(token, typeArgs, methodArgs); }
                    catch (Exception e)
                    {
                        r.Problems.Add(where + ": " + e.GetType().Name + ": " + e.Message);
                        break;
                    }
                    Type asType = target as Type;
                    if (asType != null)
                        AddType(r, asType, user);
                    else
                        AddMember(r, target, user);
                    break;
                default: // InlineI, InlineBrTarget, InlineSig, InlineString, ShortInlineR
                    i += 4;
                    break;
            }
        }
    }

    private static void Add(Result r, string row, string user)
    {
        SortedSet<string> users;
        if (!r.Rows.TryGetValue(row, out users))
            r.Rows[row] = users = new SortedSet<string>(StringComparer.Ordinal);
        users.Add(user);
    }

    private static void AddType(Result r, Type t, string user)
    {
        if (t == null)
            return;
        if (t.HasElementType)
        {
            AddType(r, t.GetElementType(), user);
            return;
        }
        if (t.IsGenericParameter)
            return;
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            foreach (Type a in t.GetGenericArguments())
                AddType(r, a, user);
            t = t.GetGenericTypeDefinition();
        }
        if (!IsOurs(t))
            return;
        Add(r, "type\t" + t.Assembly.GetName().Name + "\t" + Name(t), user);
        if (t.DeclaringType != null)
            AddType(r, t.DeclaringType, user);
    }

    private static void AddMember(Result r, MemberInfo mi, string user)
    {
        Type dt = mi.DeclaringType;
        if (dt == null)
            return;
        AddType(r, dt, user); // also the generic arguments: List<BattleUnit> uses BattleUnit
        // Recorded on the generic definitions: List`1.Add(!0), not List<BattleUnit>.Add(BattleUnit)
        if (dt.IsGenericType && !dt.IsGenericTypeDefinition)
        {
            Type def = dt.GetGenericTypeDefinition();
            MemberInfo found = null;
            foreach (MemberInfo d in def.GetMembers(All))
                if (d.MetadataToken == mi.MetadataToken && d.Module == mi.Module)
                {
                    found = d;
                    break;
                }
            if (found == null)
                return;
            mi = found;
            dt = def;
        }
        MethodInfo m = mi as MethodInfo;
        if (m != null && m.IsGenericMethod && !m.IsGenericMethodDefinition)
        {
            foreach (Type a in m.GetGenericArguments())
                AddType(r, a, user);
            mi = m = m.GetGenericMethodDefinition();
        }
        // The signature's types, wherever they're from
        FieldInfo f = mi as FieldInfo;
        if (f != null)
            AddType(r, f.FieldType, user);
        MethodBase mb = mi as MethodBase;
        if (mb != null)
            foreach (ParameterInfo p in mb.GetParameters())
                AddType(r, p.ParameterType, user);
        if (m != null)
            AddType(r, m.ReturnType, user);
        if (!IsOurs(dt))
            return;
        string row = Row(mi);
        if (row != null)
            Add(r, row, user);
    }

    // ---- Check ------------------------------------------------------------------------------------------------

    /// <summary>Each row (as written by Extract) that the build doesn't have, with why.</summary>
    public static List<string> Check(IList<string> rows)
    {
        List<string> fails = new List<string>();
        foreach (string row in rows)
        {
            string[] p = row.Split('\t');
            string why = CheckRow(p);
            if (why != null)
                fails.Add(why);
        }
        return fails;
    }

    /// <summary>Why the build lacks this row (as written by Extract), or null if it has it.</summary>
    public static string CheckLine(string row)
    {
        return CheckRow(row.Split('\t'));
    }

    private static Type FindType(string asmName, string typeName)
    {
        Assembly a = Load(asmName);
        return a == null ? null : a.GetType(typeName, false);
    }

    private static string CheckRow(string[] p)
    {
        string kind = p[0];
        Type t = FindType(p[1], p[2]);
        if (t == null)
            return "type " + p[2] + " (" + p[1] + ") not found";
        if (kind == "type")
            return null;
        string name = p[3], scope = p[4], access = p[5];
        string what = kind + " " + p[2] + "." + name;
        // Searched up the base types too: a reference resolves there at run time as well
        for (Type cur = t; cur != null; cur = cur.BaseType)
        {
            if (kind == "field")
            {
                FieldInfo f = cur.GetField(name, All);
                if (f == null)
                    continue;
                if (Scope(f.IsStatic) != scope)
                    return what + ": now " + Scope(f.IsStatic) + " (was " + scope + ")";
                if (Name(f.FieldType) != p[6])
                    return what + ": its type is now " + Name(f.FieldType) + " (was " + p[6] + ")";
                if (access == "public" && !f.IsPublic)
                    return what + ": no longer public";
                return null;
            }
            List<MethodBase> candidates = new List<MethodBase>();
            if (kind == "ctor")
                candidates.AddRange(cur.GetConstructors(All));
            else
                candidates.AddRange(cur.GetMethods(All));
            string sameName = null;
            foreach (MethodBase m in candidates)
            {
                if (m.Name != name)
                    continue;
                MethodInfo mi = m as MethodInfo;
                string arity = mi != null && mi.IsGenericMethodDefinition ? "`" + mi.GetGenericArguments().Length : null;
                string wantArity = p.Length > 8 ? p[8] : null;
                if (Params(m) != p[6] || arity != wantArity)
                {
                    sameName = sameName ?? Params(m);
                    continue;
                }
                if (mi != null && Name(mi.ReturnType) != p[7])
                    return what + p[6] + ": now returns " + Name(mi.ReturnType) + " (was " + p[7] + ")";
                if (Scope(m.IsStatic) != scope)
                    return what + p[6] + ": now " + Scope(m.IsStatic) + " (was " + scope + ")";
                if (access == "public" && !m.IsPublic)
                    return what + p[6] + ": no longer public";
                return null;
            }
            if (sameName != null)
                return what + p[6] + " not found; it now takes " + sameName;
        }
        return what + (kind == "field" ? "" : p[6]) + " not found";
    }
}
