// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Text;
using VibeSalamander.Plugins;

namespace VibeSalamander.Core.Tests;

/// <summary>
/// The plugin contracts are frozen (docs/plugin-api/contracts.txt): a plugin built for an earlier Vibe Salamander
/// must keep loading. Everything plugins see - VibeSalamander.Plugins.Abstractions and the Core types its members
/// use - may grow, never shrink or change: no member removed or renamed, no enum value renumbered, and a member
/// added to an existing interface needs a default implementation (a plugin built before it doesn't have it).
/// </summary>
public sealed class PluginContractTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Baseline = Path.Combine(Root, "docs", "plugin-api", "contracts.txt");

    [Fact]
    public void Nothing_plugins_use_was_removed_or_changed()
    {
        HashSet<string> now = Listing().ToHashSet(StringComparer.Ordinal);
        var missing = File.ReadAllLines(Baseline).Where(l => l.Length > 0 && !l.StartsWith('#') && !now.Contains(l)).ToList();
        Assert.True(missing.Count == 0,
            "These parts of the plugin contracts are gone or changed (plugins built for them would break). Bring them back, " +
            "or, for a new major version only, update docs/plugin-api/contracts.txt:\n" + string.Join("\n", missing) +
            "\n\nThe contracts now:\n" + string.Join("\n", Listing()));
    }

    [Fact]
    public void Members_added_to_existing_interfaces_have_defaults()
    {
        var frozen = File.ReadAllLines(Baseline).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (Type type in ContractTypes().Where(t => t.IsInterface && frozen.Contains(TypeLine(t))))
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (method.IsAbstract && !frozen.Contains(MemberLine(type, method)))
                    problems.Add(MemberLine(type, method));
        Assert.True(problems.Count == 0,
            "New members of existing interfaces need a default implementation (plugins built before them don't have them):\n" +
            string.Join("\n", problems));
    }

    [Fact]
    public void The_listing_is_up_to_date()
    {
        // additions are fine, but go into the file, so the next change is checked against them too
        var frozen = File.ReadAllLines(Baseline).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal);
        var added = Listing().Where(l => !frozen.Contains(l)).ToList();
        Assert.True(added.Count == 0, "Add these lines to docs/plugin-api/contracts.txt (the contracts grew):\n" + string.Join("\n", added) +
            "\n\nThe whole listing:\n" + string.Join("\n", Listing()));
    }

    // ---- the listing ------------------------------------------------------------------------------------------

    /// <summary>The Abstractions' public types and the Core types their members mention (and those of theirs), by name.</summary>
    internal static IReadOnlyList<Type> ContractTypes()
    {
        Assembly abstractions = typeof(IVibePlugin).Assembly;
        Assembly core = typeof(Core.Operations.ConflictChoice).Assembly;
        var found = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var queue = new Queue<Type>(abstractions.GetExportedTypes());
        while (queue.Count > 0)
        {
            Type type = queue.Dequeue();
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                foreach (Type argument in type.GetGenericArguments())
                    queue.Enqueue(argument);
                type = type.GetGenericTypeDefinition();
            }
            if (type.IsArray || type.IsByRef || type.IsPointer)
            {
                queue.Enqueue(type.GetElementType()!);
                continue;
            }
            if ((type.Assembly != abstractions && type.Assembly != core) || type.IsGenericParameter || !type.IsVisible)
                continue;
            if (!found.TryAdd(type.FullName!, type))
                continue;
            foreach (MemberInfo member in Members(type))
                foreach (Type used in TypesOf(member))
                    queue.Enqueue(used);
            if (type.BaseType != null)
                queue.Enqueue(type.BaseType);
            foreach (Type i in type.GetInterfaces())
                queue.Enqueue(i);
        }
        return found.Values.ToList();
    }

    internal static IEnumerable<string> Listing()
    {
        foreach (Type type in ContractTypes())
        {
            yield return TypeLine(type);
            foreach (MemberInfo member in Members(type).Select(m => (m, Line: MemberLine(type, m))).OrderBy(x => x.Line, StringComparer.Ordinal).Select(x => x.m))
                yield return MemberLine(type, member);
        }
    }

    private static IEnumerable<MemberInfo> Members(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (type.IsEnum)
            return type.GetFields(BindingFlags.Public | BindingFlags.Static);
        return type.GetMembers(flags).Where(m => m switch
        {
            MethodInfo method => !method.IsSpecialName && method.Name != "<Clone>$", // property accessors are listed with their property
            ConstructorInfo => true,
            PropertyInfo => true,
            FieldInfo => true,
            EventInfo => true,
            _ => false, // nested types are types of their own
        });
    }

    private static IEnumerable<Type> TypesOf(MemberInfo member) => member switch
    {
        MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
        ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
        PropertyInfo p => new[] { p.PropertyType },
        FieldInfo f => new[] { f.FieldType },
        EventInfo e => new[] { e.EventHandlerType! },
        _ => Array.Empty<Type>(),
    };

    internal static string TypeLine(Type type)
    {
        string kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct"
            : typeof(Delegate).IsAssignableFrom(type) ? "delegate" : type.IsAbstract && type.IsSealed ? "static class" : "class";
        return $"{kind} {Name(type)}";
    }

    internal static string MemberLine(Type type, MemberInfo member)
    {
        string owner = Name(type);
        return member switch
        {
            FieldInfo f when type.IsEnum => $"  {owner}.{f.Name} = {Convert.ToInt64(f.GetRawConstantValue())}",
            FieldInfo f => $"  {owner}.{f.Name} : {Name(f.FieldType)}{(f.IsStatic ? " static" : string.Empty)}{(f.IsInitOnly ? " readonly" : string.Empty)}",
            PropertyInfo p => $"  {owner}.{p.Name}{Indexer(p)} : {Name(p.PropertyType)} {{{Accessors(p)}}}",
            ConstructorInfo c => $"  {owner}.new({Parameters(c)})",
            MethodInfo m => $"  {owner}.{m.Name}{Generic(m)}({Parameters(m)}) : {Name(m.ReturnType)}{(m.IsStatic ? " static" : string.Empty)}",
            EventInfo e => $"  {owner}.{e.Name} : event {Name(e.EventHandlerType!)}",
            _ => $"  {owner}.{member.Name}",
        };
    }

    private static string Indexer(PropertyInfo p)
    {
        ParameterInfo[] index = p.GetIndexParameters();
        return index.Length == 0 ? string.Empty : "[" + string.Join(", ", index.Select(i => Name(i.ParameterType))) + "]";
    }

    private static string Accessors(PropertyInfo p)
    {
        var parts = new List<string>();
        if (p.GetMethod is { IsPublic: true })
            parts.Add("get;");
        if (p.SetMethod is { IsPublic: true } set)
            parts.Add(set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit") ? "init;" : "set;");
        return " " + string.Join(" ", parts) + " ";
    }

    private static string Generic(MethodInfo m) =>
        m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : string.Empty;

    private static string Parameters(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(p =>
            (p.ParameterType.IsByRef ? (p.IsOut ? "out " : "ref ") : string.Empty) + Name(p.ParameterType) + " " + p.Name +
            (p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : string.Empty)));

    /// <summary>"IReadOnlyList&lt;RemoteItem&gt;", "Func&lt;string, bool&gt;?" isn't told apart (nullability is free to change).</summary>
    internal static string Name(Type type)
    {
        if (type.IsByRef)
            return Name(type.GetElementType()!);
        if (type.IsArray)
            return Name(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericParameter)
            return type.Name;
        string name = type.IsNested ? Name(type.DeclaringType!) + "." + type.Name : (type.Namespace is { Length: > 0 } ns ? ns + "." : string.Empty) + type.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];
        if (!type.IsGenericType)
            return name;
        Type[] arguments = type.GetGenericArguments();
        if (type.IsNested && type.DeclaringType!.IsGenericType)
            arguments = arguments.Skip(type.DeclaringType.GetGenericArguments().Length).ToArray();
        return name + "<" + string.Join(", ", arguments.Select(Name)) + ">";
    }

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "VibeSalamander.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("No VibeSalamander.sln above " + AppContext.BaseDirectory);
    }
}
