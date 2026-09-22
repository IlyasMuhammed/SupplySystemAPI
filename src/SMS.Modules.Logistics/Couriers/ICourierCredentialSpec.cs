namespace SMS.Modules.Logistics.Couriers;

/// <summary>One credential an adapter reads from its carrier account.</summary>
/// <param name="Key">The name it is stored under in the vault, and read back under.</param>
/// <param name="IsSecret">Shown to the person setting it as a password field, and never shown again.</param>
public sealed record CourierCredentialSpec(string Key, string Description, bool Required, bool IsSecret);

/// <summary>
/// Implemented by an <see cref="ICourierProvider"/> that wants to say what it needs in the vault.
/// <para>
/// Optional, and separate from the provider contract, so an adapter that needs nothing — the manual
/// path — has nothing to declare. Its only job is to let a screen tell whoever is configuring an
/// account which keys to enter, instead of them finding out from a refused booking.
/// </para>
/// </summary>
public interface ICourierCredentialSpec
{
    IReadOnlyList<CourierCredentialSpec> Credentials { get; }
}
