using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrennorsCall.Launcher;

public sealed record CharacterChoice(Guid Id, string Name, int Level);
public sealed record AccountSession(string Name, string ApiUrl, string Token, DateTimeOffset Expires);
public sealed record SavedAccount(string Name, string ApiUrl, string ProtectedToken, DateTimeOffset Expires);
public sealed class AuthenticationApi(HttpClient http, DeploymentConfig config)
{
    private async Task<T> Send<T>(string api, HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        config.ValidateUrl(api);
        using var request = new HttpRequestMessage(method, api.TrimEnd('/') + path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Manifests.Json);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Sign in again. The password or session is invalid or expired.",
                HttpStatusCode.Conflict => "That name is taken, the character is already online, or all character slots are occupied.",
                HttpStatusCode.TooManyRequests => "Too many requests. Wait one minute and retry.",
                HttpStatusCode.BadRequest => "Check the name and password. Accounts require 3–24 letters/digits and passwords require 10–128 characters.",
                _ => "The account service is unavailable or rejected the request. Retry shortly."
            }, null, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Manifests.Json, ct)) ?? throw new InvalidDataException("The account service returned an empty response.");
    }
    public async Task<AccountSession> Login(string api, string name, string password, bool register = false, CancellationToken ct = default)
    {
        if (register) await Send<JsonElement>(api, HttpMethod.Post, "/v1/auth/register", new { name, password }, null, ct);
        var response = await Send<JsonElement>(api, HttpMethod.Post, "/v1/auth/login", new { name, password }, null, ct);
        return new(name.ToLowerInvariant(), api.TrimEnd('/'), response.GetProperty("token").GetString()!, DateTimeOffset.UtcNow.AddSeconds(response.GetProperty("expiresIn").GetInt32()));
    }
    public Task<CharacterChoice[]> Characters(AccountSession account, CancellationToken ct = default) => Send<CharacterChoice[]>(account.ApiUrl, HttpMethod.Get, "/v1/characters", null, account.Token, ct);
    public Task<CharacterChoice> CreateCharacter(AccountSession account, string name, CancellationToken ct = default) => Send<CharacterChoice>(account.ApiUrl, HttpMethod.Post, "/v1/characters", new { name }, account.Token, ct);
    public async Task<string> Ticket(AccountSession account, Guid character, CancellationToken ct = default)
    {
        var response = await Send<JsonElement>(account.ApiUrl, HttpMethod.Post, $"/v1/characters/{character}/ticket", new { }, account.Token, ct);
        return response.GetProperty("ticket").GetString() ?? throw new InvalidDataException("Missing launch ticket.");
    }
    public Task<string[]> Channels(AccountSession account, CancellationToken ct = default) => Send<string[]>(account.ApiUrl, HttpMethod.Get, "/v1/launcher/channels", null, account.Token, ct);
    public async Task Logout(AccountSession account, CancellationToken ct = default) => await Send<JsonElement>(account.ApiUrl, HttpMethod.Post, "/v1/auth/logout", new { }, account.Token, ct);
    public async Task<bool> Healthy(string api, CancellationToken ct = default)
    {
        try { return (await Send<JsonElement>(api, HttpMethod.Get, "/health", null, null, ct)).GetProperty("status").GetString() == "ready"; }
        catch (HttpRequestException) { return false; }
    }
}

public sealed class AccountVault(string directory)
{
    private string PathName => Path.Combine(directory, "accounts.dpapi.json");
    public SavedAccount[] List() => AtomicFile.Load<SavedAccount[]>(PathName) ?? [];
    public void Save(AccountSession account)
    {
        var saved = new SavedAccount(account.Name, account.ApiUrl, Convert.ToBase64String(WindowsSecrets.Protect(Encoding.UTF8.GetBytes(account.Token))), account.Expires);
        AtomicFile.Save(PathName, List().Where(a => !Same(a, account.Name, account.ApiUrl)).Append(saved).ToArray());
    }
    private static bool Same(SavedAccount a, string name, string api) => a.Name == name && a.ApiUrl == api;
    public AccountSession Restore(SavedAccount saved)
    {
        if (saved.Expires <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Saved session expired. Sign in again; passwords are never saved.");
        var token = WindowsSecrets.Unprotect(Convert.FromBase64String(saved.ProtectedToken));
        try { return new(saved.Name, saved.ApiUrl, Encoding.UTF8.GetString(token), saved.Expires); }
        finally { CryptographicOperations.ZeroMemory(token); }
    }
    public void Remove(string name, string api) => AtomicFile.Save(PathName, List().Where(a => !Same(a, name, api)).ToArray());
}

public static class WindowsSecrets
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Protect(byte[] data) => Transform(data, true);
    public static byte[] Unprotect(byte[] data) => Transform(data, false);
    private static byte[] Transform(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved sessions require Windows DPAPI.");
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) }; Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var success = protect ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException("Windows could not unlock this saved session. Remove it and sign in again.");
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            unsafe { CryptographicOperations.ZeroMemory(new Span<byte>((void*)input.Data, input.Length)); if (output.Data != IntPtr.Zero) CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Length)); }
            Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
