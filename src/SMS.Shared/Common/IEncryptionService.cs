namespace SMS.Shared.Common;

/// <summary>
/// Encrypts values that must not be readable in the database.
/// <para>
/// Promoted here from <c>SMS.Modules.Suppliers</c>, where it was internal (finding <b>F29</b>):
/// supplier bank details were the first thing that needed it, carrier API credentials are the
/// second, and a second implementation would have meant two ways to read one column type. Follows
/// the <c>IStockReservationService</c> precedent — contract in Shared, resolved through DI.
/// </para>
/// </summary>
public interface IEncryptionService
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}
