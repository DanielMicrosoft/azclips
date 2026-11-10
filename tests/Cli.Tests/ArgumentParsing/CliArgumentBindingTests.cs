// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Abstractions;
using Core.Abstractions.Runtime;
using Core.ArgumentParsing;
using Core.Cli;
using Core.Cli.Help;
using Core.Cli.Parser;
using Core.Runtime;
using Moq;

namespace Cli.Tests.ArgumentParsing;

public class CliArgumentBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingRequiredArguments_AreAggregatedAndRequestHelp(bool withParameterSet)
    {
        var processor = new TestProcessor();

        var error = Assert.Throws<MissingRequiredArgumentException>(() =>
            CreateBinder().BindArgumentsToProcessor(processor, new Dictionary<string, object>(),
                Metadata(withParameterSet), profile: null));

        Assert.Contains("Required parameter '--name' is missing.", error.Message);
        Assert.Contains("Required parameter '--resource-group' is missing.", error.Message);
        Assert.DoesNotContain("ResourceGroupName", error.Message);
        Assert.Equal(2, error.ExitCode);
        Assert.False(error.AgentFallbackEligible);
        var invocation = new CommandInvocation("Test", "test create", new Dictionary<string, object>());
        Assert.True(ErrorHelpPolicy.ShouldShowCommandHelp(error, invocation, errorStreamIsHumanReadable: true));
        Assert.False(ErrorHelpPolicy.ShouldShowCommandHelp(error, invocation, errorStreamIsHumanReadable: false));
        Assert.Equal(string.Empty, processor.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProfileDefault_SatisfiesCliValidationWithoutChangingSetResolution(bool withParameterSet)
    {
        var processor = new TestProcessor();

        var result = CreateBinder().BindArgumentsToProcessor(processor,
            Arguments("--name", "example", "--resource-group", "group"),
            Metadata(withParameterSet, requiredSubscription: !withParameterSet),
            new Profile { SubscriptionId = "profile-subscription" });

        Assert.Same(processor, result);
        Assert.Equal("example", processor.Name);
        Assert.Equal("group", processor.ResourceGroupName);
        Assert.Equal("profile-subscription", processor.SubscriptionId);
    }

    [Fact]
    public void ProfileDefault_DoesNotChangeExistingExplicitInputSetRequirements()
    {
        var error = Assert.Throws<UsageErrorException>(() =>
            CreateBinder().BindArgumentsToProcessor(new TestProcessor(),
                Arguments("--name", "example", "--resource-group", "group"),
                Metadata(withParameterSet: true, requiredSubscription: true),
                new Profile { SubscriptionId = "profile-subscription" }));

        Assert.Contains("Invalid parameter set", error.Message);
        Assert.False(error.HasMissingRequiredParameters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("profile-subscription")]
    public void ExplicitSubscriptionAndAlias_BindWithoutBeingOverwritten(string? profileSubscription)
    {
        var processor = new TestProcessor();
        var profile = profileSubscription is null ? null : new Profile { SubscriptionId = profileSubscription };

        CreateBinder().BindArgumentsToProcessor(processor,
            Arguments("-n", "example", "--resource-group", "group", "--subscription-id", "explicit-subscription"),
            Metadata(withParameterSet: true, requiredSubscription: true), profile);

        Assert.Equal("example", processor.Name);
        Assert.Equal("explicit-subscription", processor.SubscriptionId);
    }

    [Fact]
    public void MissingProfileDefault_IsAMissingInputError()
    {
        var error = Assert.Throws<MissingRequiredArgumentException>(() =>
            CreateBinder().BindArgumentsToProcessor(new TestProcessor(),
                Arguments("--name", "example", "--resource-group", "group"),
                Metadata(withParameterSet: false), profile: null));

        Assert.Contains("subscription-id is not provided from input and not present in profile either", error.Message);
        Assert.False(error.AgentFallbackEligible);
    }

    [Fact]
    public void InvalidAllowedValue_RemainsAnOrdinaryUsageError()
    {
        var error = Assert.Throws<UsageErrorException>(() =>
            CreateBinder().BindArgumentsToProcessor(new TestProcessor(),
                Arguments("--name", "example", "--resource-group", "group", "--mode", "invalid"),
                Metadata(withParameterSet: true), new Profile { SubscriptionId = "subscription" }));

        Assert.Contains("not allowed", error.Message);
        Assert.False(error.HasMissingRequiredParameters);
    }

    [Fact]
    public void ExistingParameterSetConflict_RemainsAnOrdinaryUsageError()
    {
        var metadata = Metadata(withParameterSet: true);
        metadata.ParameterSets!["Default"].Parameters!.Remove("Mode");

        var error = Assert.Throws<UsageErrorException>(() =>
            CreateBinder().BindArgumentsToProcessor(new TestProcessor(),
                Arguments("--name", "example", "--resource-group", "group", "--mode", "valid"),
                metadata, new Profile { SubscriptionId = "subscription" }));

        Assert.Contains("Invalid parameter set", error.Message);
        Assert.False(error.HasMissingRequiredParameters);
    }

    private static ArgumentParsingService CreateBinder()
    {
        var logging = new ConsoleLoggingService();
        var profileService = new Mock<IProfileService>();
        profileService.Setup(service => service.GetProfileKeysForDefault()).Returns(["SubscriptionId"]);
        profileService.Setup(service => service.GetProfileValueByKey(It.IsAny<Profile?>(), "SubscriptionId"))
            .Returns((Profile? profile, string _) => profile?.SubscriptionId);
        return new ArgumentParsingService(
            new CLIArgumentParsingProvider(new PropertyParser(logging), logging), profileService.Object);
    }

    private static CommandMetadata Metadata(bool withParameterSet, bool requiredSubscription = false)
    {
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["subscription-id"] = new("SubscriptionId", "subscription-id", typeof(string), "Subscription.", requiredSubscription, [], []),
            ["name"] = new("Name", "name", typeof(string), "Name.", true, ["n"], []),
            ["resource-group"] = new("ResourceGroupName", "resource-group", typeof(string), "Group.", true, [], []),
            ["mode"] = new("Mode", "mode", typeof(string), "Mode.", false, [], ["valid"]),
        };
        var sets = withParameterSet
            ? new Dictionary<string, ParameterSetMetadata>
            {
                ["Default"] = new("Default", IsDefault: true,
                    Parameters: parameters.Values.ToDictionary(
                        parameter => parameter.Name,
                        parameter => new ParameterReference(parameter.Name, parameter.IsRequired))),
            }
            : null;
        return new CommandMetadata("Test", "test create", [], typeof(TestProcessor).FullName!,
            typeof(TestProcessor).Assembly.GetName().Name!, typeof(TestProcessor), parameters,
            new Dictionary<string, string> { ["n"] = "name" }, sets);
    }

    private static IDictionary<string, object> Arguments(params string[] args) =>
        new ExtensionParser().Parse(["test", .. args]).Item2;

    private sealed class TestProcessor : IProcessor
    {
        public string SubscriptionId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ResourceGroupName { get; set; } = string.Empty;
        public string? Mode { get; set; }

        public Task ProcessAsync() => Task.CompletedTask;
        public IProcessor Init(ProcessorContext context) => this;
    }
}
