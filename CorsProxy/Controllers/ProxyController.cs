using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;

namespace CorsProxy.Controllers;

[ApiController]
[Route("[controller]")]
public class ProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    // Hop-by-hop headers that must not be forwarded (RFC 7230)
    private static readonly HashSet<string> _hopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    public ProxyController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

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

        var preserveCookieDomain = Request.Query.ContainsKey("_preserveCookieDomain");

        using var requestMessage = new HttpRequestMessage(new HttpMethod(Request.Method), targetUri);

        // Copy request headers, skipping hop-by-hop headers and honoring Connection: header
        var connectionHeaderValues = new List<string>();
        if (Request.Headers.TryGetValue("Connection", out var connValues))
        {
            foreach (var token in connValues.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = token.Trim();
                if (!string.IsNullOrEmpty(t)) connectionHeaderValues.Add(t);
            }
        }

        foreach (var header in Request.Headers)
        {
            var headerName = header.Key;

            if (_hopByHopHeaders.Contains(headerName))
                continue;

            // Skip any header named in the Connection header
            if (connectionHeaderValues.Any(h => string.Equals(h, headerName, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Do not forward the incoming Host header; set Host explicitly from target
            if (string.Equals(headerName, "Host", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!requestMessage.Headers.TryAddWithoutValidation(headerName, header.Value.ToArray()))
            {
                // Will try to add to content headers when creating content
            }
        }

        // Explicitly set Host to target authority (host[:port]) to ensure correct virtual host
        requestMessage.Headers.Host = targetUri.IsDefaultPort ? targetUri.Host : targetUri.Authority;

        // Decide if request has a body to forward
        var hasTransferEncoding = Request.Headers.ContainsKey("Transfer-Encoding");
        var hasContentLength = Request.ContentLength.HasValue && Request.ContentLength.Value > 0;
        var shouldCopyBody = (hasContentLength || hasTransferEncoding) ||
                             (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method));

        if (shouldCopyBody && Request.Body != null && Request.Body.CanRead)
        {
            // If content-length is known, set it on the StreamContent to help HttpClient
            var streamContent = new StreamContent(Request.Body);
            if (Request.ContentLength.HasValue)
            {
                streamContent.Headers.ContentLength = Request.ContentLength.Value;
            }

            // Move any headers that weren't added to requestMessage.Headers into content headers
            foreach (var header in Request.Headers)
            {
                if (_hopByHopHeaders.Contains(header.Key))
                    continue;

                if (connectionHeaderValues.Any(h => string.Equals(h, header.Key, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!requestMessage.Headers.Contains(header.Key))
                {
                    // Do not copy content-length/transfer-encoding; HttpClient will manage them
                    if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        continue;

                    streamContent.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            requestMessage.Content = streamContent;
        }

        // Use named client configured in Program.cs which controls decompression, cookies and redirect behaviour
        var client = _httpClientFactory.CreateClient("proxy");

        try
        {
            using var responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            // Copy status code
            Response.StatusCode = (int)responseMessage.StatusCode;

            // If content-length present on content, set it explicitly
            if (responseMessage.Content?.Headers.ContentLength != null)
            {
                Response.ContentLength = responseMessage.Content.Headers.ContentLength;
            }

            // Build list of hop-by-hop/custom headers to skip from response based on Connection header
            var responseConnectionHeaderValues = new List<string>();
            if (responseMessage.Headers.TryGetValues("Connection", out var respConnValues))
            {
                foreach (var token in respConnValues.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = token.Trim();
                    if (!string.IsNullOrEmpty(t)) responseConnectionHeaderValues.Add(t);
                }
            }

            // Copy response headers, rewriting Set-Cookie Domain attribute if requested
            foreach (var header in responseMessage.Headers)
            {
                if (_hopByHopHeaders.Contains(header.Key))
                    continue;

                // Skip headers named in Connection header
                if (responseConnectionHeaderValues.Any(h => string.Equals(h, header.Key, StringComparison.OrdinalIgnoreCase)))
                    continue;

                foreach (var value in header.Value)
                {
                    if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        var outValue = preserveCookieDomain ? value : RemoveDomainFromSetCookie(value);
                        try { Response.Headers.Append("Set-Cookie", outValue); } catch { /* ignore */ }
                    }
                    else
                    {
                        try { Response.Headers.Append(header.Key, value); } catch
                        {
                            // Some headers (e.g. content-type) must be set via specific properties
                            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                            {
                                Response.ContentType = value;
                            }
                        }
                    }
                }
            }

            // Preserve content headers
            if (responseMessage.Content != null)
            {
                foreach (var header in responseMessage.Content.Headers)
                {
                    if (_hopByHopHeaders.Contains(header.Key))
                        continue;

                    if (responseConnectionHeaderValues.Any(h => string.Equals(h, header.Key, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    foreach (var value in header.Value)
                    {
                        if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                        {
                            var outValue = preserveCookieDomain ? value : RemoveDomainFromSetCookie(value);
                            try { Response.Headers.Append("Set-Cookie", outValue); } catch { /* ignore */ }
                        }
                        else if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        {
                            // Skip these; handled by ASP.NET and by Response.ContentLength above
                        }
                        else
                        {
                            try { Response.Headers.Append(header.Key, value); } catch
                            {
                                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                                {
                                    Response.ContentType = value;
                                }
                            }
                        }
                    }
                }

                // Remove transfer-encoding if present on response headers dictionary to avoid platform exceptions
                Response.Headers.Remove("transfer-encoding");

                // If trailing headers are present on the response, preserve them as regular headers prefixed with X-Trailer- to avoid losing important metadata.
                // Note: Proper HTTP trailers require declaring and appending trailers via server APIs which may not be available in all hosting scenarios.
                if (responseMessage.TrailingHeaders != null && responseMessage.TrailingHeaders.Any())
                {
                    var trailerNames = new List<string>();
                    foreach (var th in responseMessage.TrailingHeaders)
                    {
                        trailerNames.Add(th.Key);
                        foreach (var v in th.Value)
                        {
                            try { Response.Headers.Append("X-Trailer-" + th.Key, v); } catch { /* ignore */ }
                        }
                    }

                    if (trailerNames.Count > 0)
                    {
                        try { Response.Headers.Append("Trailer", string.Join(", ", trailerNames)); } catch { /* ignore */ }
                    }
                }

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

        try
        {
            // Parse cookie by splitting on semicolons, preserving the first name=value pair
            var parts = setCookie.Split(';').Select(p => p.Trim()).ToList();
            if (parts.Count == 0)
                return setCookie;

            var first = parts[0];
            var attrs = parts.Skip(1)
                             .Where(p => !p.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
                             .ToList();

            var resultParts = new List<string> { first };
            resultParts.AddRange(attrs.Where(s => !string.IsNullOrWhiteSpace(s)));

            var result = string.Join("; ", resultParts);
            return result;
        }
        catch
        {
            // If parsing fails for any reason, return original value
            return setCookie;
        }
    }
}
