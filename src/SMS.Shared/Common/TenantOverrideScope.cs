namespace SMS.Shared.Common;

/// <summary>
/// Runs code as a given organization even inside an HTTP request of another caller (the super admin creating an organization and
/// applying its default data). While active, <see cref="ITenantContext"/> reports this organization and IsSuperAdmin = false, so
/// tenant-scoped seeders write to the new organization and never to the caller's. Flows with the async call chain; dispose to end.
/// </summary>
public static class TenantOverrideScope
{
    private static readonly AsyncLocal<Guid?> _organizationId = new();

    public static Guid? OrganizationId => _organizationId.Value;

    public static IDisposable Begin(Guid organizationId)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("An organization id is required.", nameof(organizationId));
        var previous = _organizationId.Value;
        _organizationId.Value = organizationId;
        return new Restore(previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Guid? _previous;
        private bool _done;
        public Restore(Guid? previous) => _previous = previous;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _organizationId.Value = _previous;
        }
    }
}
