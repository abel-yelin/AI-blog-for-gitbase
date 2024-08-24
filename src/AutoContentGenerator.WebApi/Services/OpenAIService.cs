using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;

namespace AutoContentGenerator.WebApi.Services;

public class OpenAIService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public OpenAIService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _httpClient.BaseAddress = new Uri(_configuration["OpenAI:BaseUrl"]);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _configuration["OpenAI:OpenAIApiKey"]);
    }

    public async Task<string> SendChatRequest(string content)
    {
        var model = _configuration["OpenAI:Model"] ?? "gpt-3.5-turbo";

        var requestBody = new
        {
            model = model,
            messages = new[]
            {
                new { role = "user", content = content }
            }
        };

        var response = await _httpClient.PostAsJsonAsync("chat/completions", requestBody);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Request failed with status code {response.StatusCode}. Error: {errorContent}");
        }

        var result = await response.Content.ReadFromJsonAsync<OpenAIResponse>();
        return result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    public async Task<string> GenerateImage(string prompt, string imageModel = "dall-e-3")
    {
        var requestBody = new
        {
            model = imageModel,
            prompt = prompt,
            n = 1,
            size = "1024x1024"
        };

        var response = await _httpClient.PostAsJsonAsync("images/generations", requestBody);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Request failed with status code {response.StatusCode}. Error: {errorContent}");
        }

        var result = await response.Content.ReadFromJsonAsync<ImageGenerationResponse>();
        return result?.Data?.FirstOrDefault()?.Url ?? string.Empty;
    }

    public async Task<bool> TestConnection()
    {
        try
        {
            var response = await SendChatRequest("Hello, this is a test message.");
            return !string.IsNullOrEmpty(response);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenAI API connection test failed: {ex.Message}");
            return false;
        }
    }

    private class ImageGenerationResponse
    {
        public List<ImageData>? Data { get; set; }
    }

    private class ImageData
    {
        public string? Url { get; set; }
    }
}

public class OpenAIResponse
{
    public Choice[]? Choices { get; set; }
}

public class Choice
{
    public Message? Message { get; set; }
}

public class Message
{
    public string? Content { get; set; }
}