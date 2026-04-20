using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace AvaTwin
{
public class AvaTwinApiClient
{
    private readonly AvaTwinConfig _config;
    private readonly Credentials _credentials;
    public string SessionToken { get; private set; }

    public AvaTwinApiClient(AvaTwinConfig config, Credentials credentials)
    {
        _config = config;
        _credentials = credentials;
    }

    public async Task<string> MintTokenAsync()
    {
        if (_credentials == null)
            throw new Exception(
                "No credentials available. Assign a Credentials asset via Ava-Twin > Setup.");

        var url = $"{_config.baseApiUrl}/api/token-mint";
        var body = JsonConvert.SerializeObject(new { appId = _credentials.AppId, apiKey = _credentials.ApiKey });
        var resp = await PostJsonAsync<TokenMintResponse>(url, body);
        if (string.IsNullOrWhiteSpace(resp.token))
            throw new Exception($"Mint token failed: {resp.error ?? "No token"}");

        SessionToken = resp.token;
        return SessionToken;
    }

    public async Task<AvatarLibraryResponse> GetLibraryAsync()
    {
        EnsureToken();
        var url = $"{_config.baseApiUrl}/api/avatar-library";
        var response = await GetJsonAsync<AvatarLibraryResponse>(url, SessionToken);
        if (!string.IsNullOrWhiteSpace(response?.error))
            throw new Exception($"Avatar library failed: {response.error}");
        return response;
    }

    public async Task<Texture2D> GetVariationThumbnailAsync(string variationId)
    {
        EnsureToken();

        // API expects short ID (g1_t2 -> t2)
        if (string.IsNullOrWhiteSpace(variationId))
            throw new Exception("Variation id is required.");

        // Strip generation prefix (e.g. "g1_t2" -> "t2", "g2_h3" -> "h3")
        int prefixEnd = variationId.IndexOf('_');
        string shortId = prefixEnd >= 0 ? variationId.Substring(prefixEnd + 1) : variationId;
        string metaUrl = $"{_config.baseApiUrl}/api/variation-image?id={shortId}";
        var meta = await GetJsonAsync<VariationImageResponse>(metaUrl, SessionToken);

        if (string.IsNullOrWhiteSpace(meta.url))
            throw new Exception($"Thumbnail URL missing for {variationId}. {meta.error}");

        return await DownloadTextureAsync(meta.url);
    }

    public async Task<AvatarResolveResponse> ResolveAvatarAsync(string avatarId)
    {
        EnsureToken();
        if (string.IsNullOrWhiteSpace(avatarId))
            throw new Exception("Avatar id is required.");

        string url = $"{_config.baseApiUrl}/api/avatar-resolve";
        string body = JsonConvert.SerializeObject(new { avatar_id = avatarId });
        var response = await PostJsonAsync<AvatarResolveResponse>(url, body, SessionToken);
        if (!string.IsNullOrWhiteSpace(response?.error))
            throw new Exception($"Avatar resolve failed: {response.error}");
        return response;
    }

    private void EnsureToken()
    {
        if (string.IsNullOrWhiteSpace(SessionToken))
            throw new Exception("Session token missing. Call MintTokenAsync() first.");
    }

    private static async Task<T> GetJsonAsync<T>(string url, string bearer = null)
    {
        using (var req = UnityWebRequest.Get(url))
        {
            if (!string.IsNullOrWhiteSpace(bearer))
                req.SetRequestHeader("Authorization", $"Bearer {bearer}");

            await SendAsync(req);

            if (req.responseCode < 200 || req.responseCode >= 300)
                throw new Exception($"GET {url} failed ({req.responseCode}): {req.downloadHandler.text}");

            return JsonConvert.DeserializeObject<T>(req.downloadHandler.text);
        }
    }

    private static async Task<T> PostJsonAsync<T>(string url, string json, string bearer = null)
    {
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrWhiteSpace(bearer))
                req.SetRequestHeader("Authorization", $"Bearer {bearer}");

            await SendAsync(req);

            if (req.responseCode < 200 || req.responseCode >= 300)
                throw new Exception($"POST {url} failed ({req.responseCode}): {req.downloadHandler.text}");

            return JsonConvert.DeserializeObject<T>(req.downloadHandler.text);
        }
    }

    private static async Task<Texture2D> DownloadTextureAsync(string url)
    {
        using (var req = UnityWebRequestTexture.GetTexture(url))
        {
            await SendAsync(req);

            if (req.responseCode < 200 || req.responseCode >= 300)
                throw new Exception($"Image download failed ({req.responseCode})");

            return DownloadHandlerTexture.GetContent(req);
        }
    }

    private static Task SendAsync(UnityWebRequest req)
    {
        var tcs = new TaskCompletionSource<bool>();
        var op = req.SendWebRequest();
        op.completed += _ => tcs.TrySetResult(true);
        return tcs.Task;
    }
}
}
