using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Threading.Tasks;

namespace CorsProxy.Controllers;

[ApiController]
[Route("[controller]")]
public class ProxyController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ProxyController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS")]
    public async Task<IActionResult> Proxy()
    {
        HttpContext.Request.EnableBuffering();
        HttpContext.Request.Body.Position = 0;

        var targetUrl = HttpContext.Request.Query["_proxyTargetUrl"].FirstOrDefault();
        if (string.IsNullOrEmpty(targetUrl))
        {
            return BadRequest("Missing _proxyTargetUrl");
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
        {
            return BadRequest("Invalid _proxyTargetUrl");
        }

        using var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage
        {
            Method = new HttpMethod(HttpContext.Request.Method),
            RequestUri = uri
        };

        foreach (var header in HttpContext.Request.Headers)
        {
            if (header.Key.ToLower() != "host")
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToString());
        }

        if (HttpContext.Request.Body != null)
        {
            request.Content = new StreamContent(HttpContext.Request.Body);
            if (!string.IsNullOrEmpty(HttpContext.Request.ContentType))
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(HttpContext.Request.ContentType);
        }

        try
        {
            var response = await client.SendAsync(request, HttpContext.RequestAborted);

            HttpContext.Response.StatusCode = (int)response.StatusCode;

            foreach (var header in response.Headers)
            {
                if (!header.Key.Equals("content-length", StringComparison.CurrentCultureIgnoreCase) && !header.Key.Equals("transfer-encoding", StringComparison.CurrentCultureIgnoreCase))
                    HttpContext.Response.Headers.TryAdd(header.Key, header.Value.ToString());
            }

            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    if (header.Key.ToLower() != "content-length" && header.Key.ToLower() != "transfer-encoding")
                        HttpContext.Response.Headers.TryAdd(header.Key, header.Value.ToString());
                }
                var content = await response.Content.ReadAsByteArrayAsync();
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                return File(content, contentType);
            }
            else
            {
                return StatusCode((int)response.StatusCode);
            }
        }
        catch (Exception)
        {
            return StatusCode(502, "Bad Gateway");
        }
    }
}
