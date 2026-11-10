// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Abstractions;
using Core.Abstractions.Runtime;
using Core.ArgumentParsing;
using Microsoft.Azure.Azclips.Cmdlets.Accounts;
using Moq;

namespace Cmdlets.Tests;

/// <summary>
/// Regression tests for <see cref="PSArgumentParsingProvider"/>. The parser bridges the
/// PowerShell cmdlet layer (whose <c>-ParameterName</c> matches the C# property on the cmdlet
/// class) and the processor layer (whose C# property can differ). The parser must route by
/// <see cref="ParameterMetadata.Name"/> so that the downstream reflection call
/// <c>processor.GetType().GetProperty(name)</c> targets the processor property, not the PS
/// parameter name.
/// </summary>
public class PSArgumentParsingProviderTests
{
    private static ParameterMetadata Meta(string processorProperty, string invocationName, Type type, string[]? aliases = null) =>
        new(processorProperty, invocationName, type, "test", IsRequired: false, aliases ?? [], AllowedValues: []);

    [Fact]
    public void BindArguments_ProfileDefaultDoesNotExcludeTheExplicitParameterSet()
    {
        var profile = new Profile { SubscriptionId = "profile-subscription" };
        var profileService = new Mock<IProfileService>();
        profileService.Setup(service => service.GetProfileKeysForDefault()).Returns(["SubscriptionId"]);
        profileService.Setup(service => service.GetProfileValueByKey(profile, "SubscriptionId"))
            .Returns(profile.SubscriptionId);
        var metadata = new CommandMetadata("Test", "Test-Command", [], typeof(TestProcessor).FullName!,
            typeof(TestProcessor).Assembly.GetName().Name!, typeof(TestProcessor),
            new Dictionary<string, ParameterMetadata>
            {
                ["Name"] = Meta("Name", "Name", typeof(string)),
                ["SubscriptionId"] = Meta("SubscriptionId", "SubscriptionId", typeof(string)),
            },
            new Dictionary<string, string>(),
            new Dictionary<string, ParameterSetMetadata>
            {
                ["ByName"] = new("ByName", Parameters: new Dictionary<string, ParameterReference>
                {
                    ["Name"] = new("Name", IsRequired: true),
                }),
            });
        var processor = new TestProcessor();

        new ArgumentParsingService(new PSArgumentParsingProvider(), profileService.Object)
            .BindArgumentsToProcessor(processor, new Dictionary<string, object> { ["Name"] = "example" },
                metadata, profile);

        Assert.Equal("example", processor.Name);
        Assert.Equal(profile.SubscriptionId, processor.SubscriptionId);
    }

    [Fact]
    public void ParseArguments_TranslatesKeyToProcessorPropertyName()
    {
        var provider = new PSArgumentParsingProvider();
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["PsParamName"] = Meta("ProcessorProperty", "PsParamName", typeof(string)),
        };
        var args = new Dictionary<string, object> { ["PsParamName"] = "value" };

        var result = provider.ParseArguments(args, parameters, parameterAliases: null);

        Assert.True(result.ContainsKey("ProcessorProperty"), "output key must be the processor property name");
        Assert.Equal("value", result["ProcessorProperty"].Item2);
        Assert.False(result.ContainsKey("PsParamName"), "must not leak the PS-side parameter name to reflection");
    }

    [Fact]
    public void ParseArguments_LeavesKeyUnchangedWhenNameMatches()
    {
        // Existing cmdlets whose PS parameter name equals the processor property must keep working.
        var provider = new PSArgumentParsingProvider();
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["Filter"] = Meta("Filter", "Filter", typeof(string)),
        };
        var args = new Dictionary<string, object> { ["Filter"] = "expression" };

        var result = provider.ParseArguments(args, parameters, parameterAliases: null);

        Assert.True(result.ContainsKey("Filter"));
        Assert.Equal("expression", result["Filter"].Item2);
    }

    [Fact]
    public void ParseArguments_PassesUnknownKeysThrough()
    {
        // Unknown keys are forwarded so downstream components can produce a clear error.
        var provider = new PSArgumentParsingProvider();
        var parameters = new Dictionary<string, ParameterMetadata>();
        var args = new Dictionary<string, object> { ["NotDeclared"] = "value" };

        var result = provider.ParseArguments(args, parameters, parameterAliases: null);

        Assert.True(result.ContainsKey("NotDeclared"));
        Assert.Equal("value", result["NotDeclared"].Item2);
    }

    [Fact]
    public void ParseArguments_TranslatesArrayParametersToProcessorPropertyName()
    {
        var provider = new PSArgumentParsingProvider();
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["Select"] = Meta("Select", "Select", typeof(string[])),
        };
        var args = new Dictionary<string, object> { ["Select"] = new[] { "id", "displayName" } };

        var result = provider.ParseArguments(args, parameters, parameterAliases: null);

        Assert.True(result.ContainsKey("Select"));
        var value = Assert.IsType<string[]>(result["Select"].Item2);
        Assert.Equal(new[] { "id", "displayName" }, value);
    }

    [Fact]
    public void ParseArguments_PreservesAssignableObjectsInCollection()
    {
        var provider = new PSArgumentParsingProvider();
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["Items"] = Meta("Items", "Items", typeof(List<object>)),
        };
        var item = new object();
        var args = new Dictionary<string, object> { ["Items"] = new[] { item } };

        var result = provider.ParseArguments(args, parameters, parameterAliases: null);

        var value = Assert.IsType<List<object>>(result["Items"].Item2);
        Assert.Same(item, Assert.Single(value));
    }

    private sealed class TestProcessor : IProcessor
    {
        public string Name { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;

        public Task ProcessAsync() => Task.CompletedTask;
        public IProcessor Init(ProcessorContext context) => this;
    }
}
