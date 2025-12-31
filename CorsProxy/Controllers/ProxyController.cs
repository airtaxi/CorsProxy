using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace CorsProxy.Controllers;

[ApiController]
[Route("[controller]")]
public class ProxyController(IHttpClientFactory httpClientFactory) : ControllerBase
{
    [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS")]
    public async Task<IActionResult> Proxy()
    {
        var target = Request.Query["_proxyTargetUrl"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
            return BadRequest("Missing required query parameter: _proxyTargetUrl");

        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri) ||
            (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("_proxyTargetUrl must be an absolute http or https URL");
        }

        // Optional flag: whether to preserve Domain attribute in Set-Cookie headers (default: false)
        // Presence of the query parameter enables preservation (any value or no value -> true)
        var preserveCookieDomain = Request.Query.ContainsKey("_preserveCookieDomain");

        using var requestMessage = new HttpRequestMessage(new HttpMethod(Request.Method), targetUri);

        // Set Host header from target URI (include port when non-default)
        var hostHeader = targetUri.IsDefaultPort ? targetUri.Host : $"{targetUri.Host}:{targetUri.Port}";
        requestMessage.Headers.Host = hostHeader;

        // Copy request headers
        foreach (var header in Request.Headers)
        {
            // Skip Host header - we set it from the target URI above
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                continue;

            // List of content headers (must only be set on content)
            var contentHeaderNames = new[]
            {
                "Content-Type", "Content-Length", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Location", "Content-MD5", "Content-Range"
            };

            if (contentHeaderNames.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
            {
                // Will be handled below if content exists
                continue;
            }

            // Try to add to request headers
            requestMessage.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
        }

        // Copy content (if any)
        if (Request.ContentLength > 0 || !HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method) && !HttpMethods.IsDelete(Request.Method))
        {
            // Create StreamContent from the incoming request body
            var streamContent = new StreamContent(Request.Body);

            // List of content headers
            var contentHeaderNames = new[]
            {
                "Content-Type", "Content-Length", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Location", "Content-MD5", "Content-Range"
            };

            // Move any content headers from the incoming request into content headers
            foreach (var header in Request.Headers)
            {
                if (contentHeaderNames.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                {
                    streamContent.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
                }
            }

            requestMessage.Content = streamContent;
        }

        var client = httpClientFactory.CreateClient();

        try
        {
            using var responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            // Copy status code
            Response.StatusCode = (int)responseMessage.StatusCode;

            // Copy response headers, optionally rewriting Set-Cookie Domain attribute so browser will accept cookies for the proxy origin
            foreach (var header in responseMessage.Headers)
            {
                if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var value in header.Value)
                    {
                        var outValue = preserveCookieDomain ? value : RemoveDomainFromSetCookie(value);
                        Response.Headers.Append("Set-Cookie", outValue);
                    }
                }
                else
                {
                    Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            if (responseMessage.Content != null)
            {
                foreach (var header in responseMessage.Content.Headers)
                {
                    if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var value in header.Value)
                        {
                            var outValue = preserveCookieDomain ? value : RemoveDomainFromSetCookie(value);
                            Response.Headers.Append("Set-Cookie", outValue);
                        }
                    }
                    else
                    {
                        Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                // Some headers are not allowed to be set on the response
                Response.Headers.Remove("transfer-encoding");

                await responseMessage.Content.CopyToAsync(Response.Body);
            }

            return new EmptyResult();
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    private static string RemoveDomainFromSetCookie(string setCookie)
    {
        if (string.IsNullOrEmpty(setCookie))
            return setCookie;

        var parts = setCookie.Split(';');
        if (parts.Length <= 1)
            return setCookie;

        var kept = parts.Where(p =>
        {
            var s = p.Trim();
            var eq = s.IndexOf('=');
            var key = eq >= 0 ? s[..eq].Trim() : s;
            return !string.Equals(key, "Domain", StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        return string.Join("; ", kept).Trim();
    }
}
