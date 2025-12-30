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
    public async Task Proxy()
    {
        var targetUrl = HttpContext.Request.Query["_proxyTargetUrl"].FirstOrDefault();
        if (string.IsNullOrEmpty(targetUrl))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsync("Missing _proxyTargetUrl");
            return;
        }

        using var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage
        {
            Method = new HttpMethod(HttpContext.Request.Method),
            RequestUri = new Uri(targetUrl)
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

        var response = await client.SendAsync(request, HttpContext.RequestAborted);

        HttpContext.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers)
        {
            HttpContext.Response.Headers.TryAdd(header.Key, header.Value.ToString());
        }

        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
            {
                HttpContext.Response.Headers.TryAdd(header.Key, header.Value.ToString());
            }
            await response.Content.CopyToAsync(HttpContext.Response.Body);
        }
    }
}
