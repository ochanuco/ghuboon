namespace Ghuboon.App.ViewModels.Settings;

/// <summary>
/// Security section of the Settings screen. Read-only descriptive text only:
/// it explains where the PAT and the cache encryption key live (ADR-007).
/// </summary>
public sealed class SecuritySettingsViewModel : ViewModelBase
{
    public string CredentialStorageDescription =>
        "Personal Access Tokens are stored in the macOS Keychain and are never written to logs or local files.";

    public string EncryptedCacheDescription =>
        "The local notification cache is encrypted using a SQLCipher-compatible scheme. The encryption key lives in the macOS Keychain.";
}
