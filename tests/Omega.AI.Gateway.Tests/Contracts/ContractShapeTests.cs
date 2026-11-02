using Omega.AI.Gateway.Contracts;
using Xunit;

namespace Omega.AI.Gateway.Tests.Contracts;

public sealed class ContractShapeTests
{
    [Fact]
    public void Public_contracts_do_not_reference_consumer_domain_types()
    {
        var contractTypes = new[]
        {
            typeof(AiProfile), typeof(AiMessage), typeof(AiRequest), typeof(AiResponse),
            typeof(AiModelInfo), typeof(AiStreamEvent), typeof(AiConnectionTest),
            typeof(IAiGateway), typeof(IAiProvider)
        };

        Assert.All(contractTypes, type =>
        {
            Assert.DoesNotContain(type.GetMembers(), member =>
                member.ToString()!.Contains("SVG", StringComparison.OrdinalIgnoreCase) ||
                member.ToString()!.Contains("OmegaIllustrator", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void Messages_keep_their_input_order_and_roles()
    {
        var messages = new[]
        {
            new AiMessage(AiMessageRole.System, "Use concise language."),
            new AiMessage(AiMessageRole.User, "Hello"),
            new AiMessage(AiMessageRole.Assistant, "Hi")
        };

        var request = new AiRequest("profile-1", messages);

        Assert.Equal(messages, request.Messages);
        Assert.Equal(
            new[] { AiMessageRole.System, AiMessageRole.User, AiMessageRole.Assistant },
            request.Messages.Select(message => message.Role));
    }

    [Fact]
    public void Model_override_is_optional()
    {
        var request = new AiRequest(
            "profile-1",
            new[] { new AiMessage(AiMessageRole.User, "Hello") });

        Assert.Null(request.ModelId);
    }
}
