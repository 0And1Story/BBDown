using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace BBDown;

// Keep QR polling and ticket exchange in one isolated cookie session.
internal sealed class WebLoginSession : IDisposable
{
    private static readonly Uri PassportUri = new("https://passport.bilibili.com/");
    private static readonly Uri WebsiteUri = new("https://www.bilibili.com/");
    private static readonly string[] LoginCookieNames = ["SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid"];
    private readonly CookieContainer cookies = new();
    private readonly HttpClient client;

    public WebLoginSession(string userAgent, HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        });
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        client.DefaultRequestHeaders.Referrer = WebsiteUri;
    }

    public async Task<string> GetAsync(string url)
    {
        var uri = new Uri(url);
        for (int redirects = 0; redirects <= 10; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps ||
                !(uri.Host == "bilibili.com" || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host == "biligame.com" || uri.Host.EndsWith(".biligame.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("登录接口返回了不支持的跳转地址。");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            string cookieHeader = cookies.GetCookieHeader(uri);
            if (cookieHeader.Length > 0)
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            using var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                foreach (string cookie in setCookies)
                    cookies.SetCookies(uri, cookie);

            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location
                    ?? throw new InvalidDataException("登录跳转响应缺少 Location。");
                uri = new Uri(uri, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        throw new InvalidDataException("登录跳转次数过多。");
    }

    public async Task<string> GetLoginCookiesAsync(string loginUrl)
    {
        string cookie = GetLoginCookies();
        if (!HasSession(cookie))
        {
            // Older poll responses carried credentials in the query itself.
            if (Uri.TryCreate(loginUrl, UriKind.Absolute, out var legacyUri))
                foreach (string part in legacyUri.Query.TrimStart('?').Split('&'))
                {
                    int separator = part.IndexOf('=');
                    if (separator <= 0) continue;
                    string name = part[..separator];
                    string value = part[(separator + 1)..];
                    if (LoginCookieNames.Contains(name) && value.Length > 0)
                        cookies.Add(new Cookie(name, value.Replace(",", "%2C"), "/", ".bilibili.com"));
                }
            cookie = GetLoginCookies();
            if (!HasSession(cookie) && !string.IsNullOrWhiteSpace(loginUrl))
            {
                // New poll responses contain a one-time ticket. Cookies may be
                // issued at any hop, including a 302 response from crossDomain.
                await GetAsync(loginUrl);
                cookie = GetLoginCookies();
            }
        }
        if (!HasSession(cookie))
            throw new InvalidDataException("扫码已确认，但未获取到 SESSDATA，请重新登录。原凭据文件未修改。");
        return cookie;
    }

    private string GetLoginCookies() => string.Join(";", cookies.GetCookies(WebsiteUri).Cast<Cookie>()
        .Concat(cookies.GetCookies(PassportUri).Cast<Cookie>())
        .Where(cookie => LoginCookieNames.Contains(cookie.Name) && cookie.Value.Length > 0)
        .DistinctBy(cookie => cookie.Name)
        .Select(cookie => $"{cookie.Name}={cookie.Value.Replace(",", "%2C")}"));

    private static bool HasSession(string cookie) => cookie.Split(';').Any(part => part.StartsWith("SESSDATA=", StringComparison.Ordinal) && part.Length > 9);

    public void Dispose() => client.Dispose();
}
