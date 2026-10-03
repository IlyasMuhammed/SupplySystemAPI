using System.Reflection;
using SMS.Shared.Common;

namespace SMS.WorkflowEngine.Tests.Helpers;

/// <summary>
/// The built-in roles' permission grants as <c>AuthDataSeeder</c> seeds them. Read from the seeder's private
/// data by reflection — the same approach as SMS.Modules.Auth.Tests' seed tests — because running the
/// seeder needs a real database, and a copy of the lists here would only drift from the real ones.
/// </summary>
internal static class SeededRoles
{
    private static readonly Lazy<Dictionary<int, string[]>> Grants = new(() =>
    {
        var seeder = typeof(SMS.Modules.Auth.Authorization.PermissionPolicyProvider).Assembly
            .GetType("SMS.Modules.Auth.Data.AuthDataSeeder", throwOnError: true)!;

        return (Dictionary<int, string[]>)seeder
            .GetField("RolePermissionSeed", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
    });

    /// <summary>Every seeded role, with what it is granted.</summary>
    public static IEnumerable<(EnumRole Role, string[] Permissions)> All =>
        Grants.Value.Select(g => ((EnumRole)g.Key, g.Value));

    public static string[] Permissions(EnumRole role) => Grants.Value[(int)role];
}
