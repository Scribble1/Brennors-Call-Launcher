namespace BrennorsCall.Launcher;

public static class PrivateDownloadUrls
{
    // The API token and account session never accompany this direct CDN request.
    public static bool IsCdn(Uri url) => url.IsAbsoluteUri && url.Scheme == "https" && url.Port == 443 &&
        url.Host == "release-assets.githubusercontent.com" && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Fragment);
}
