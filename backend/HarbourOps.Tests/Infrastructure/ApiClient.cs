using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarbourOps.Api.Features.Auth;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>Problem Details as returned by the API (RFC 9457 plus our "code" extension).</summary>
public sealed record ProblemResponse(
    int Status, string? Title, string? Detail, string? Code, Dictionary<string, string[]>? Errors);

/// <summary>
/// Talks to the API the way the React app does: cookie session plus an antiforgery token in the
/// X-XSRF-TOKEN header on state-changing requests, refreshed after sign-in and sign-out.
/// </summary>
public sealed class ApiClient(HttpClient http) : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public HttpClient Http => http;

    public string? AntiforgeryToken { get; private set; }

    public async Task<string> RefreshAntiforgeryTokenAsync()
    {
        var response = await http.GetFromJsonAsync<AntiforgeryTokenResponse>("/api/antiforgery/token", Json);
        AntiforgeryToken = response!.Token;
        return AntiforgeryToken;
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password = DemoAccounts.Password)
    {
        await RefreshAntiforgeryTokenAsync();
        var response = await PostAsync("/api/auth/login", new { email, password });
        if (response.IsSuccessStatusCode)
        {
            await RefreshAntiforgeryTokenAsync(); // the old token was bound to the anonymous user
        }

        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url);

    public async Task<T> GetJsonAsync<T>(string url)
    {
        var response = await http.GetAsync(url);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    /// <summary>POST with the current antiforgery token (if any).</summary>
    public Task<HttpResponseMessage> PostAsync(string url, object? body) => PostWithTokenAsync(url, body, AntiforgeryToken);

    /// <summary>POST with an explicit antiforgery token, or none at all when <paramref name="token"/> is null.</summary>
    public async Task<HttpResponseMessage> PostWithTokenAsync(string url, object? body, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body ?? new { }, options: Json),
        };
        if (token is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", token);
        }

        return await http.SendAsync(request);
    }

    public async Task<T> PostJsonAsync<T>(string url, object? body)
    {
        var response = await PostAsync(url, body);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<ProblemResponse> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return (await response.Content.ReadFromJsonAsync<ProblemResponse>(Json))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected success but got {(int)response.StatusCode}: {body}");
        }
    }

    public void Dispose() => http.Dispose();
}
