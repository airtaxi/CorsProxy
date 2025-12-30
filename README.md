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

Example:

```http
GET /proxy?_proxyTargetUrl=https://api.example.com/data
```

This will forward the request to `https://api.example.com/data` and return the response.

## License

This project is licensed under the MIT License. See [LICENSE.txt](LICENSE.txt) for details.

## Author

airtaxi (Howon Lee)