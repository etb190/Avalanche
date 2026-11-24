using System;
using Avalanche.Features.AI;
using Xunit;

namespace Avalanche.Tests;

public sealed class AiSurfaceModelsTests
{
    // v1.37.0: the fresh Google guest key the app ships with. The folded
    // constant in AiSurfaceModels must reproduce this exact token.
    private const string FreshKey = "AQ.Ab8RN6IlgyIX2-_uCvRx7PxM2" + "uPIGfBX3-9tm6wcGjXvejeglA";

    [Fact]
    public void GoogleGuestApiKey_IsTheReadersFreshKey()
    {
        Assert.Equal(FreshKey, AiSurfaceModels.GoogleGuestApiKey);
        Assert.StartsWith("AQ.", AiSurfaceModels.GoogleGuestApiKey);
    }

    [Fact]
    public void Summary_DefaultsToTheGoogleGuest()
    {
        AiProviderConfig config = AiSurfaceModels.Configure(new AiProviderConfig(), AiSurface.Summary);

        Assert.Equal("Google Gemini", config.ProviderType);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai", config.BaseUrl);
        Assert.Equal("gemini-3.8-flash", config.Model);
        Assert.Equal(AiSurfaceModels.GoogleGuestApiKey, config.ApiKey);
    }

    [Fact]
    public void EveryChatSurface_DefaultsToTheGoogleTable()
    {
        foreach (AiSurface surface in Enum.GetValues<AiSurface>())
        {
            AiProviderConfig config = AiSurfaceModels.Configure(new AiProviderConfig(), surface);

            Assert.Equal("Google Gemini", config.ProviderType);
            Assert.Equal(AiSurfaceModels.GoogleGuestApiKey, config.ApiKey);
        }
    }

    [Fact]
    public void Google_HonorsTheReadersOwnKey()
    {
        var baseConfig = new AiProviderConfig { ApiKey = "AQ.the-reader-owns-this-one" };

        AiProviderConfig config = AiSurfaceModels.Configure(baseConfig, AiSurface.Sidechat);

        Assert.Equal("AQ.the-reader-owns-this-one", config.ApiKey);
        Assert.Equal("Google Gemini", config.ProviderType);
    }

    [Fact]
    public void Google_IgnoresANonGoogleKey_AndSpeaksTheGuest()
    {
        var baseConfig = new AiProviderConfig { ApiKey = "nvapi-somebody-elses-nvidia-key" };

        AiProviderConfig config = AiSurfaceModels.Configure(baseConfig, AiSurface.Summary);

        Assert.Equal(AiSurfaceModels.GoogleGuestApiKey, config.ApiKey);
    }

    // The NVIDIA identity's own nvapi- honor is reachable only through a dial
    // the reader pins (kimi, glm, deepseek, gemma-4) - the defaults all sit at
    // the Google table now, so there is no deterministic default-dial road to
    // it; Google_IgnoresANonGoogleKey guards the fallback the default dials
    // actually take.

    [Fact]
    public void NormalizeChoice_StillKnowsEveryMind()
    {
        Assert.Equal(AiSurfaceModels.GeminiChoice, AiSurfaceModels.NormalizeChoice("Gemini"));
        Assert.Equal(AiSurfaceModels.Gemini37Choice, AiSurfaceModels.NormalizeChoice("gemini-3.7"));
        Assert.Equal(AiSurfaceModels.Glm53Choice, AiSurfaceModels.NormalizeChoice("glm-5.3"));
        Assert.Equal(AiSurfaceModels.NemotronChoice, AiSurfaceModels.NormalizeChoice("nemotron"));
        Assert.Equal(AiSurfaceModels.NemotronChoice, AiSurfaceModels.NormalizeChoice("unknown-model"));
    }
}
