using System.Net;
using CatAssistant.Backend.Services;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class AssistantCoordinatorTests
{
    [Fact]
    public void BoundClientRejectsAnotherCard()
    {
        var error = Assert.Throws<LinkException>(() => AssistantCoordinator.EnsureDeviceMatchesOwner(0x001122334455, 0x00AABBCCDDEE));

        Assert.Equal(LinkError.BoundDeviceMismatch, error.Kind);
    }

    [Fact]
    public void BoundClientAcceptsItsOwnerAndFirstPairing()
    {
        AssistantCoordinator.EnsureDeviceMatchesOwner(0x001122334455, 0x001122334455);
        AssistantCoordinator.EnsureDeviceMatchesOwner(null, 0x00AABBCCDDEE);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(9, 15)]
    public void ReconnectBackoffIsBounded(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AssistantCoordinator.ReconnectDelayForFailure(failures));
    }

    [Fact]
    public void NoSpeechUsesRecordingGuidanceInsteadOfServiceError()
    {
        var error = new CloudException(CloudError.NoSpeech, "语音识别未返回文字。");

        var message = AssistantCoordinator.ErrorMessageForCard(error);

        Assert.Equal("没有听到清晰语音，请按住 OK 说完后再松开", message);
    }

    [Theory]
    [InlineData(CloudError.ApiKeyMissing)]
    [InlineData(CloudError.ApiKeyInvalid)]
    public void ApiKeyErrorsKeepConfigurationGuidance(CloudError kind)
    {
        var message = AssistantCoordinator.ErrorMessageForCard(new CloudException(kind, "key error"));

        Assert.Equal("请在 Windows 客户端检查 API Key", message);
    }

    [Fact]
    public void ServiceErrorsKeepRetryGuidance()
    {
        var message = AssistantCoordinator.ErrorMessageForCard(new CloudException(CloudError.Service, "service error"));

        Assert.Equal("服务暂时不可用，请稍后重试", message);
    }

    [Fact]
    public void AsrBadRequestIsTreatedAsNoSpeech()
    {
        var error = DashScopeClient.ErrorForStatus(HttpStatusCode.BadRequest, isAsrRequest: true);

        Assert.Equal(CloudError.NoSpeech, error.Kind);
        Assert.Equal("DashScope 未检测到有效语音。", error.Message);
        Assert.Equal("没有听到清晰语音，请按住 OK 说完后再松开", AssistantCoordinator.ErrorMessageForCard(error));
    }

    [Fact]
    public void ChatBadRequestRemainsAServiceError()
    {
        var error = DashScopeClient.ErrorForStatus(HttpStatusCode.BadRequest, isAsrRequest: false);

        Assert.Equal(CloudError.Service, error.Kind);
        Assert.Equal("DashScope 服务返回 HTTP 400。", error.Message);
    }
}
