# CorsProxy

A simple CORS proxy server built with ASP.NET Core (.NET 10) that forwards HTTP requests to a target URL while preserving headers, body, and method. This allows clients to bypass CORS restrictions when making requests to external APIs.

## Features

- Supports all HTTP methods (GET, POST, PUT, DELETE, PATCH, HEAD, OPTIONS)
- Forwards request headers and body intact
- Returns response headers and body as-is
- Includes CORS configuration to allow cross-origin requests

## Usage

1. Clone the repository and build the project.
2. Run the application.
3. Make requests to the proxy endpoint with the `_proxyTargetUrl` query parameter.

Options:

- `_proxyTargetUrl` (required): the absolute http or https URL to forward the request to.
- `_preserveCookieDomain` (optional, default: `false`): presence of this parameter enables preservation of the `Domain` attribute in `Set-Cookie` response headers from the target. If the parameter is omitted (default), the proxy removes the `Domain` attribute so the browser will accept the cookie for the proxy origin.

Example:

```http
GET /proxy?_proxyTargetUrl=https://api.example.com/data
```

This will forward the request to `https://api.example.com/data` and return the response.

To preserve cookie Domain attributes:

```http
GET /proxy?_proxyTargetUrl=https://api.example.com/data&_preserveCookieDomain
```

You may also pass a value (e.g. `_preserveCookieDomain=true`); the proxy treats any presence of the parameter as `true`.

## License

This project is licensed under the MIT License. See [LICENSE.txt](LICENSE.txt) for details.

## Author

airtaxi (Howon Lee)