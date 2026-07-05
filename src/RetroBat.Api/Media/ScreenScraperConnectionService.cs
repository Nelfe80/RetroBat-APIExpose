using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public sealed class ScreenScraperConnectionService
{
    private const string DefaultBaseUrl = "https://api.screenscraper.fr/api2";
    private const string BundledSoftName = "RetroBat-APIExpose-V1";

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly EmulationStationSettingsService _settingsService;

    public ScreenScraperConnectionService(
        IOptionsMonitor<ApiExposeOptions> options,
        EmulationStationSettingsService settingsService)
    {
        _options = options;
        _settingsService = settingsService;
    }

    public ScreenScraperConnectionInfo Resolve()
    {
        var scrapingOptions = _options.CurrentValue.Scraping;
        var esSettings = _settingsService.GetScrapingSettings();

        var devId = FirstNonEmpty(
            Environment.GetEnvironmentVariable("APIEXPOSE_SCREENSCRAPER_DEV_ID"),
            scrapingOptions.ScreenScraperDevId,
            scrapingOptions.UseBundledScreenScraperDeveloperCredentials ? EmbeddedSecretDefaults.ScreenScraperDevId : string.Empty);
        var devPassword = FirstNonEmpty(
            Environment.GetEnvironmentVariable("APIEXPOSE_SCREENSCRAPER_DEV_PASSWORD"),
            scrapingOptions.ScreenScraperDevPassword,
            scrapingOptions.UseBundledScreenScraperDeveloperCredentials ? EmbeddedSecretDefaults.ScreenScraperDevPassword : string.Empty);
        var softName = FirstNonEmpty(
            Environment.GetEnvironmentVariable("APIEXPOSE_SCREENSCRAPER_SOFTNAME"),
            scrapingOptions.UseBundledScreenScraperDeveloperCredentials ? BundledSoftName : string.Empty,
            BundledSoftName);
        var baseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("APIEXPOSE_SCREENSCRAPER_BASE_URL"),
            scrapingOptions.ScreenScraperBaseUrl,
            DefaultBaseUrl);

        return new ScreenScraperConnectionInfo
        {
            BaseUrl = NormalizeBaseUrl(baseUrl),
            User = esSettings.ScreenScraperUser,
            Password = esSettings.ScreenScraperPassword,
            DevId = devId,
            DevPassword = devPassword,
            SoftName = softName,
            DeveloperCredentialSource = ResolveDeveloperCredentialSource(scrapingOptions)
        };
    }

    private static string ResolveDeveloperCredentialSource(ApiExposeOptions.ScrapingOptions options)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APIEXPOSE_SCREENSCRAPER_DEV_ID")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APIEXPOSE_SCREENSCRAPER_DEV_PASSWORD")))
        {
            return "environment";
        }

        if (!string.IsNullOrWhiteSpace(options.ScreenScraperDevId) ||
            !string.IsNullOrWhiteSpace(options.ScreenScraperDevPassword))
        {
            return "appsettings";
        }

        return options.UseBundledScreenScraperDeveloperCredentials
            ? "bundled"
            : "missing";
    }

    private static string NormalizeBaseUrl(string value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(trimmed)
            ? DefaultBaseUrl
            : trimmed;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}

public sealed class ScreenScraperConnectionInfo
{
    public string BaseUrl { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DevId { get; set; } = string.Empty;
    public string DevPassword { get; set; } = string.Empty;
    public string SoftName { get; set; } = string.Empty;
    public string DeveloperCredentialSource { get; set; } = string.Empty;

    public bool HasUserCredentials =>
        !string.IsNullOrWhiteSpace(User) &&
        !string.IsNullOrWhiteSpace(Password);

    public bool HasDeveloperCredentials =>
        !string.IsNullOrWhiteSpace(DevId) &&
        !string.IsNullOrWhiteSpace(DevPassword) &&
        !string.IsNullOrWhiteSpace(SoftName);
}
