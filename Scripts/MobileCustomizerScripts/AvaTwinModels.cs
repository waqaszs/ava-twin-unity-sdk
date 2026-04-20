using System.Collections.Generic;
using Newtonsoft.Json;

namespace AvaTwin
{
public class TokenMintResponse
{
    [JsonProperty("token")] public string token;
    [JsonProperty("expiresIn")] public int expiresIn;
    [JsonProperty("error")] public string error;
}

public class AvatarVariation
{
    [JsonProperty("id")] public string id;
    [JsonProperty("gender")] public string gender;
    [JsonProperty("category")] public string category;
    [JsonProperty("variation_id")] public string variationId;
    [JsonProperty("display_name")] public string displayName;
    [JsonProperty("thumbnail_url")] public string thumbnailUrl;
    [JsonProperty("model_path")] public string modelPath;
    [JsonProperty("sort_order")] public int sortOrder;
}

public class AvatarLibraryResponse
{
    // shape: { "library": { "generic": { "top": [...], "bottom": [...], "shoes": [...], "base":[...] }, ... } }
    [JsonProperty("library")]
    public Dictionary<string, Dictionary<string, List<AvatarVariation>>> library;

    [JsonProperty("error")] public string error;
}

public class VariationImageResponse
{
    [JsonProperty("url")] public string url;
    [JsonProperty("error")] public string error;
}

public class AvatarResolveResponse
{
    [JsonProperty("avatar_id")] public string avatarId;
    [JsonProperty("url")] public string url;
    [JsonProperty("expires_in")] public int expiresIn;
    [JsonProperty("error")] public string error;
}
}