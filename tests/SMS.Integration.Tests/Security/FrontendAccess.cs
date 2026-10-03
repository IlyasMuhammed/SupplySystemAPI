using System.Reflection;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Shared.Authorization;

namespace SMS.Integration.Tests.Security;

/// <summary>
/// One way the frontend leads a user to a server action: a route (its <c>permissionGuard(...)</c>), a menu
/// entry (<c>permRequired</c>) or a button inside a page (<c>hasPermission(...)</c>), and the endpoints it then
/// calls (<see cref="Actions"/>). <see cref="Requires"/> is ALL of its groups, each group ANY of its codes — a
/// route guard is one group, a button on that page adds another.
/// </summary>
internal sealed record FrontendEntry(string Where, string[][] Requires, (Type Controller, string Action)[] Actions)
{
    /// <summary>A route or menu entry, opened by any one of <paramref name="anyOf"/> — or, with none, by every signed-in user.</summary>
    public static FrontendEntry Page(string where, params string[] anyOf) => new(where, anyOf.Length == 0 ? [] : [anyOf], []);

    /// <summary>A button or tab inside the page, offered only to holders of any one of <paramref name="anyOf"/> as well.</summary>
    public FrontendEntry And(string what, params string[] anyOf) => this with { Where = $"{Where} → {what}", Requires = [.. Requires, anyOf] };

    /// <summary>The actions this page or button calls.</summary>
    public FrontendEntry Calls<TController>(params string[] actions) =>
        this with { Actions = [.. Actions, .. actions.Select(a => (typeof(TController), a))] };

    public override string ToString() => Where;
}

/// <summary>
/// Proves the server admits whoever the frontend lets reach an action. Gating an endpoint more narrowly than
/// the page that calls it locks out a user who uses that page today — the rule of the 2026-10-02 permission
/// sweep is that the server requires exactly what the frontend already requires to reach the page or button.
/// </summary>
internal static class FrontendAccess
{
    /// <summary>
    /// The server's gates on an action: one any-of group per [RequirePermission] on the controller or the action
    /// (all of them must hold). Empty when the action checks no permission. Throws for an action that does not
    /// exist or is not an HTTP action, so a renamed action cannot quietly drop out of the walk.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Gates(Type controller, string action)
    {
        var methods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == action && m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();
        if (methods.Count == 0)
            throw new InvalidOperationException($"{controller.Name}.{action} is not an HTTP action — renamed or removed?");

        // Overloads (rare) must agree, or one of them would be walked under the other's gates.
        var gates = methods.Select(m => controller.GetCustomAttributes<RequirePermissionAttribute>(inherit: true)
                .Concat(m.GetCustomAttributes<RequirePermissionAttribute>())
                .Select(a => (IReadOnlyList<string>)a.AnyOf.ToArray())
                .ToList())
            .ToList();
        if (gates.Select(g => string.Join(";", g.Select(any => string.Join("|", any)))).Distinct().Count() > 1)
            throw new InvalidOperationException($"{controller.Name}.{action} has overloads with different gates — name the one meant");
        return gates[0];
    }

    private static bool Admits(IEnumerable<IReadOnlyList<string>> gates, IReadOnlyCollection<string> held) =>
        gates.All(group => group.Any(held.Contains));

    /// <summary>
    /// For every seeded role and every entry it can reach, the actions that entry calls but the server refuses
    /// the role. Empty when nobody is locked out.
    /// </summary>
    public static IEnumerable<string> SeededRoleLockOuts(IEnumerable<FrontendEntry> entries)
    {
        var list = entries.ToList();
        foreach (var (role, granted) in SeededRoles.All)
        foreach (var entry in list.Where(e => e.Requires.All(group => group.Any(granted.Contains))))
        foreach (var (controller, action) in entry.Actions)
        {
            var gates = Gates(controller, action);
            if (!Admits(gates, granted))
                yield return $"{role} reaches '{entry.Where}' but {controller.Name}.{action} refuses it " +
                             $"(needs {Describe(gates)})";
        }
    }

    /// <summary>
    /// Stronger than <see cref="SeededRoleLockOuts"/>: for every entry, every user holding only the codes the
    /// frontend checks — one from each group, in every combination — must be admitted by every action it calls.
    /// A custom role built in the role editor is covered by this, not by the seeded walk.
    /// </summary>
    public static IEnumerable<string> FrontendParityGaps(IEnumerable<FrontendEntry> entries)
    {
        foreach (var entry in entries)
        foreach (var held in Combinations(entry.Requires))
        foreach (var (controller, action) in entry.Actions)
        {
            var gates = Gates(controller, action);
            if (!Admits(gates, held))
                yield return $"a user holding only [{string.Join(", ", held)}] reaches '{entry.Where}' but " +
                             $"{controller.Name}.{action} refuses them (needs {Describe(gates)})";
        }
    }

    private static IEnumerable<string[]> Combinations(string[][] groups) =>
        groups.Aggregate(
            (IEnumerable<string[]>)[[]],
            (acc, group) => acc.SelectMany(held => group.Select(code => held.Append(code).Distinct().ToArray())));

    private static string Describe(IEnumerable<IReadOnlyList<string>> gates) =>
        string.Join(" and ", gates.Select(g => g.Count == 1 ? g[0] : $"any of {string.Join("/", g)}"));
}
