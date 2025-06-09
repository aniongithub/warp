using System.Net;

internal static class ForwarderHttpClientFactory
{
    public static readonly HttpMessageInvoker Instance = new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false
    });
}