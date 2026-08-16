/*
Copyright (c) FufuLauncher Dev Team. All rights reserved.
Licensed under the MIT License.
*/
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FufuLauncher.Models.MiHoYo.Fingerprint;

namespace FufuLauncher.Services;

public partial class AccountManager
{
    #region Cookie 文件读写

    private const string ProtectedCookieFormat = "dpapi-current-user-v1";
    private static readonly byte[] CookieEntropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("FufuLauncher.AccountCookie.v2"));

    private async Task WriteCookieFileAsync(string path, Dictionary<string, string> cookies)
    {
        var file = new AccountCookieFile(cookies, await ReadFingerprintCoreAsync(path));
        await WriteAccountCookieFileAsync(path, file);
    }

    private static async Task WriteAccountCookieFileAsync(string path, AccountCookieFile file)
    {
        var payloadJson = JsonSerializer.Serialize(file, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(payloadJson), CookieEntropy, DataProtectionScope.CurrentUser);
        var envelope = new ProtectedAccountCookieEnvelope(
            ProtectedCookieFormat, Convert.ToBase64String(protectedBytes));
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });

        var tempPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { }
        }
    }

    private static AccountCookieFile? ParseAccountCookieFile(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(root, "format", out var formatProp) &&
            string.Equals(formatProp.GetString(), ProtectedCookieFormat, StringComparison.Ordinal) &&
            TryGetPropertyIgnoreCase(root, "protected_data", out var dataProp))
        {
            var encoded = dataProp.GetString();
            if (string.IsNullOrWhiteSpace(encoded))
                throw new CryptographicException("Protected cookie payload is empty.");

            var clearBytes = ProtectedData.Unprotect(
                Convert.FromBase64String(encoded), CookieEntropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AccountCookieFile>(Encoding.UTF8.GetString(clearBytes));
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetPropertyIgnoreCase(root, "cookies", out var cookiesProp) &&
            cookiesProp.ValueKind == JsonValueKind.Object)
        {
            var cookies = ReadStringDictionary(cookiesProp);
            DeviceFpRequest? fingerprint = null;
            if (TryGetPropertyIgnoreCase(root, "fingerprint", out var fpProp) &&
                fpProp.ValueKind == JsonValueKind.Object)
            {
                fingerprint = fpProp.Deserialize<DeviceFpRequest>();
            }
            return new AccountCookieFile(cookies, fingerprint);
        }

        if (TryGetPropertyIgnoreCase(root, "values", out var valuesProp) &&
            valuesProp.ValueKind == JsonValueKind.Object)
        {
            return new AccountCookieFile(ReadStringDictionary(valuesProp));
        }

        var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return legacy == null ? null : new AccountCookieFile(legacy);
    }

    private static async Task<AccountCookieFile?> ReadAccountCookieFileAsync(string path)
    {
        if (!File.Exists(path)) return null;
        return ParseAccountCookieFile(await File.ReadAllTextAsync(path));
    }

    private static AccountCookieFile? ReadAccountCookieFile(string path)
    {
        if (!File.Exists(path)) return null;
        return ParseAccountCookieFile(File.ReadAllText(path));
    }

    private async Task<Dictionary<string, string>?> ReadCookieValuesAsync(string path)
    {
        try
        {
            return (await ReadAccountCookieFileAsync(path))?.Cookies;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AccountManager] Cookie 文件解析失败: {ex.Message}");
            return null;
        }
    }

    public async Task<Dictionary<string, string>> LoadCookiesAsync(string accountId)
    {
        var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
        if (entry == null) return null;

        string path = Path.Combine(CookiesDir, entry.CookieFilePath);
        if (!File.Exists(path)) return null;

        return await ReadCookieValuesAsync(path);
    }

    public async Task UpdateCookiesAsync(string accountId, Dictionary<string, string> newCookies)
    {
        await _lock.WaitAsync();
        try
        {
            var entry = _accountList.Accounts.FirstOrDefault(a => a.Id == accountId);
            if (entry == null) return;

            string cookiePath = Path.Combine(CookiesDir, entry.CookieFilePath);
            await WriteCookieFileAsync(cookiePath, newCookies);
            entry.CookieVersion = CookieFileVersion;
            entry.UpdatedAt = DateTime.Now;
            await SaveAccountListAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    #endregion
}
