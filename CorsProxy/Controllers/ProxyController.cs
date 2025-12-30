using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using System.Linq;

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
        var target = Request.Query["_proxyTargetUrl"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(target))
            return BadRequest("Missing required query parameter: _proxyTargetUrl");

        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri) ||
            (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("_proxyTargetUrl must be an absolute http or https URL");
        }

        using var requestMessage = new HttpRequestMessage(new HttpMethod(Request.Method), targetUri);

        // Copy request headers
        foreach (var header in Request.Headers)
        {
            // Skip Host header - HttpClient will set it
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                continue;

            // Try to add to request headers; if that fails, add to content headers
            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                // We'll create content below if needed
            }
        }

        // Copy content (if any)
        if (Request.ContentLength > 0 || !HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method) && !HttpMethods.IsDelete(Request.Method))
        {
            // Create StreamContent from the incoming request body
            var streamContent = new StreamContent(Request.Body);

            // Move any headers that weren't added to requestMessage.Headers into content headers
            foreach (var header in Request.Headers)
            {
                if (!requestMessage.Headers.Contains(header.Key))
                {
                    streamContent.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            requestMessage.Content = streamContent;
        }

        var client = _httpClientFactory.CreateClient();

        try
        {
            using var responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

            // Copy status code
            Response.StatusCode = (int)responseMessage.StatusCode;

            // Copy response headers
            foreach (var header in responseMessage.Headers)
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }

            if (responseMessage.Content != null)
            {
                foreach (var header in responseMessage.Content.Headers)
                {
                    Response.Headers[header.Key] = header.Value.ToArray();
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
}
