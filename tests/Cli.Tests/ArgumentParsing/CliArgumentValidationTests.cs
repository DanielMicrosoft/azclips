// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Abstractions;
using Core.Runtime;

namespace Cli.Tests.ArgumentParsing;

public class CliArgumentValidationTests
{
    private static Core.Cli.CLIArgumentParsingProvider CreateProvider()
    {
        var loggingService = new ConsoleLoggingService();
        return new Core.Cli.CLIArgumentParsingProvider(new Core.Cli.PropertyParser(loggingService), loggingService);
    }

    [Fact]
    public void ValidateArguments_MissingRequired_ReportsCliFlagName()
    {
        var provider = CreateProvider();

        // Mirrors `az vm list`, whose required parameters are exposed as CLI flags.
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["SubscriptionId"] = new("SubscriptionId", "subscription-id", typeof(string), "Azure Subscription Id.", true, [], []),
            ["ResourceGroupName"] = new("ResourceGroupName", "resource-group", typeof(string), "The resource group name.", true, [], []),
        };

        var valid = provider.ValidateArguments(new Dictionary<string, (string, object)>(), parameters, out var errors);

        Assert.False(valid);
        Assert.NotNull(errors);
        Assert.Equal(2, errors.Count);
        Assert.Contains("Required parameter '--subscription-id' is missing.", errors);
        Assert.Contains("Required parameter '--resource-group' is missing.", errors);
        Assert.DoesNotContain(errors!, e => e.Contains("SubscriptionId"));
        Assert.DoesNotContain(errors!, e => e.Contains("ResourceGroupName"));
    }

    [Fact]
    public void ValidateArguments_RequiredFalseAndZeroValues_AreProvided()
    {
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["Force"] = new("Force", "force", typeof(bool), "Force.", true, [], []),
            ["Count"] = new("Count", "count", typeof(int), "Count.", true, [], []),
        };
        var args = new Dictionary<string, (string, object)>
        {
            ["Force"] = ("force", false),
            ["Count"] = ("count", 0),
        };

        Assert.True(CreateProvider().ValidateArguments(args, parameters, out var errors));
        Assert.Empty(errors!);
    }

    [Fact]
    public void ParseArguments_MissingScalarValue_ErrorUsesInvocationName()
    {
        // Guards that when a CLI flag name differs from its underlying processor property, the
        // error message references the flag the user typed, not the internal property name.
        var provider = CreateProvider();

        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["long-flag"] = new("LongFlagProperty", "long-flag", typeof(string), "desc", false, [], []),
        };
        var args = new Dictionary<string, object>
        {
            ["long-flag"] = new List<List<string>>(),
        };

        var ex = Assert.Throws<UsageErrorException>(() => provider.ParseArguments(args, parameters, parameterAliases: null));

        Assert.Contains("'long-flag'", ex.Message);
        Assert.DoesNotContain("LongFlagProperty", ex.Message);
    }

    [Fact]
    public void ParseArguments_ScalarConversionFailure_ErrorUsesInvocationName()
    {
        // Type-conversion failures inside PropertyParser must also surface the CLI flag name.
        var provider = CreateProvider();

        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["numeric-flag"] = new("NumericFlagProperty", "numeric-flag", typeof(int), "desc", false, [], []),
        };
        var args = new Dictionary<string, object>
        {
            ["numeric-flag"] = new List<List<string>> { new() { "not-an-int" } },
        };

        var ex = Assert.Throws<UsageErrorException>(() => provider.ParseArguments(args, parameters, parameterAliases: null));

        Assert.Contains("'numeric-flag'", ex.Message);
        Assert.DoesNotContain("NumericFlagProperty", ex.Message);
    }

    public static IEnumerable<object[]> EmptyCollectionOccurrences()
    {
        Type[] types = [typeof(List<string>), typeof(string[]), typeof(List<List<string>>), typeof(string[][])];
        string[][] tokenSets =
        [
            ["--items"],
            ["--items", "--switch"],
            ["--items", "first", "--items"],
            ["--items", "--items", "second"],
            ["--items", "first", "--items", "--items", "second"],
        ];

        foreach (Type type in types)
        {
            foreach (string[] tokens in tokenSets)
            {
                yield return [type, tokens];
            }
        }
    }

    [Theory]
    [MemberData(nameof(EmptyCollectionOccurrences))]
    public void ParseArguments_EmptyCollectionOccurrence_ThrowsBeforeBinding(Type type, string[] tokens)
    {
        var provider = CreateProvider();
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["items"] = new("ItemsProperty", "items", type, "Items.", false, [], []),
            ["switch"] = new("Switch", "switch", typeof(bool), "Switch.", false, [], []),
        };
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["example", .. tokens]);

        var ex = Assert.Throws<UsageErrorException>(() => provider.ParseArguments(args, parameters, null));

        Assert.Contains("Parameter 'items' requires a value.", ex.Message);
        Assert.DoesNotContain("ItemsProperty", ex.Message);
    }

    [Theory]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(string[]))]
    [InlineData(typeof(List<List<string>>))]
    [InlineData(typeof(string[][]))]
    public void ParseArguments_EmptyCollectionSource_Throws(Type type)
    {
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["items"] = new("Items", "items", type, "Items.", false, [], []),
        };
        var args = new Dictionary<string, object> { ["items"] = new List<List<string>>() };

        Assert.Throws<UsageErrorException>(() => CreateProvider().ParseArguments(args, parameters, null));
    }

    [Fact]
    public void ParseArguments_EmptyCollectionAlias_ErrorUsesTypedAlias()
    {
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["items"] = new("ItemsProperty", "items", typeof(List<string>), "Items.", false, [], []),
        };
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["example", "-i"]);

        var ex = Assert.Throws<UsageErrorException>(() => CreateProvider().ParseArguments(
            args, parameters, new Dictionary<string, string> { ["i"] = "items" }));

        Assert.Contains("Parameter 'i' requires a value.", ex.Message);
        Assert.DoesNotContain("ItemsProperty", ex.Message);
    }

    [Theory]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(string[]))]
    [InlineData(typeof(List<List<string>>))]
    [InlineData(typeof(string[][]))]
    public void ParseArguments_CollectionValues_PreservesOccurrencesAndEmptyString(Type type)
    {
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["items"] = new("Items", "items", type, "Items.", false, [], []),
        };
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(
            ["example", "--items", "first", "", "--items", "second"]);

        var parsed = CreateProvider().ParseArguments(args, parameters, null);

        Assert.IsType(type, parsed["Items"].Item2);
        if (parsed["Items"].Item2 is IEnumerable<IEnumerable<string>> nested)
        {
            Assert.Collection(nested,
                first => Assert.Equal(["first", ""], first),
                second => Assert.Equal(["second"], second));
        }
        else
        {
            Assert.Equal(["first", "", "second"], Assert.IsAssignableFrom<IEnumerable<string>>(parsed["Items"].Item2));
        }
    }

    [Fact]
    public void ParseArguments_OmittedCollectionAndBareBoolean_RemainSupported()
    {
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["items"] = new("Items", "items", typeof(List<string>), "Items.", false, [], []),
            ["switch"] = new("Switch", "switch", typeof(bool), "Switch.", false, [], []),
        };
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["example", "--switch"]);

        var parsed = CreateProvider().ParseArguments(args, parameters, null);

        Assert.DoesNotContain("Items", parsed.Keys);
        Assert.True(Assert.IsType<bool>(parsed["Switch"].Item2));
    }

    private static Dictionary<string, ParameterMetadata> SingleValueParameters() => new()
    {
        ["name"] = new("Name", "name", typeof(string), "Name.", false, ["n"], []),
        ["count"] = new("Count", "count", typeof(int), "Count.", false, [], []),
        ["force"] = new("Force", "force", typeof(bool), "Force.", false, [], []),
    };

    private static readonly Dictionary<string, string> s_singleValueAliases = new() { ["n"] = "name" };

    [Theory]
    [InlineData("b", new[] { "--name", "a", "--name", "b" })]
    [InlineData("b", new[] { "--name", "a", "-n", "b" })]
    [InlineData("b", new[] { "-n", "a", "--name", "b" })]
    [InlineData("b", new[] { "-n", "a", "-n", "b" })]
    public void ParseArguments_SingleValueParameterRepeated_LastValueWins(string expected, string[] tokens)
    {
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["example", .. tokens]);

        var parsed = CreateProvider().ParseArguments(args, SingleValueParameters(), s_singleValueAliases);

        Assert.Equal(expected, parsed["Name"].Item2);
    }

    [Fact]
    public void ParseArguments_TypedSingleValueParametersRepeated_LastValueWins()
    {
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["example", "--count", "1", "--count", "2", "--force", "false", "--force"]);

        var parsed = CreateProvider().ParseArguments(args, SingleValueParameters(), s_singleValueAliases);

        Assert.Equal(2, parsed["Count"].Item2);
        Assert.True(Assert.IsType<bool>(parsed["Force"].Item2));
    }

    [Theory]
    [InlineData("count", new[] { "--count", "not-an-int", "--count", "2" })]
    [InlineData("count", new[] { "--count", "2", "--count", "not-an-int" })]
    [InlineData("force", new[] { "--force", "notabool", "--force", "true" })]
    [InlineData("force", new[] { "--force", "notabool", "--force" })]
    public void ParseArguments_SingleValueParameterRepeated_ValidatesEveryOccurrence(string typed, string[] tokens)
    {
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["example", .. tokens]);

        var ex = Assert.Throws<UsageErrorException>(() => CreateProvider().ParseArguments(args, SingleValueParameters(), s_singleValueAliases));

        Assert.Contains($"'{typed}'", ex.Message);
    }

    [Fact]
    public void ParseArguments_SingleValueParameterRepeatedAcrossAliases_ValidatesEveryOccurrence()
    {
        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["count"] = new("Count", "count", typeof(int), "Count.", false, ["c"], []),
        };
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["example", "-c", "not-an-int", "--count", "2"]);

        Assert.Throws<UsageErrorException>(() => CreateProvider().ParseArguments(args, parameters, new Dictionary<string, string> { ["c"] = "count" }));
    }

    [Theory]
    [InlineData("name", new[] { "--name", "a", "b" })]
    [InlineData("name", new[] { "--name", "a", "b", "--name", "c" })]
    [InlineData("count", new[] { "--count", "1", "2" })]
    [InlineData("force", new[] { "--force", "true", "false" })]
    public void ParseArguments_SingleValueParameterWithMultipleTokens_Throws(string typed, string[] tokens)
    {
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["example", .. tokens]);

        var ex = Assert.Throws<UsageErrorException>(() => CreateProvider().ParseArguments(args, SingleValueParameters(), s_singleValueAliases));

        Assert.Contains($"Parameter '{typed}' accepts a single value but received 2", ex.Message);
    }

    [Fact]
    public void ParseArguments_SingleValueParameterWithMultipleTokens_DoesNotEchoValues()
    {
        // Values can be credentials (e.g. `az login --federated-token <token> extra`); errors go to stderr.
        const string secret = "eyJhbGciOiJSUzI1NiJ9.secret-token";
        var metadata = new Microsoft.Azure.Azclips.Extensions.Accounts.ExtensionIndex().GetCommandMetadata("login", ClientType.CLI);
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["login", "--federated-token", secret, "extra"]);

        var ex = Assert.Throws<UsageErrorException>(() => CreateProvider().ParseArguments(args, metadata.Parameters, metadata.ParameterAliases));

        Assert.Contains("Parameter 'federated-token' accepts a single value but received 2.", ex.Message);
        Assert.DoesNotContain(secret, ex.Message);
        Assert.DoesNotContain("extra", ex.Message);
    }

    [Fact]
    public void ParseArguments_SingleValueParameters_BindOnce()
    {
        var (_, args) = new Core.Cli.Parser.ExtensionParser().Parse(["example", "-n", "a", "--count", "3", "--force", "false"]);

        var parsed = CreateProvider().ParseArguments(args, SingleValueParameters(), s_singleValueAliases);

        Assert.Equal("a", parsed["Name"].Item2);
        Assert.Equal(3, parsed["Count"].Item2);
        Assert.False(Assert.IsType<bool>(parsed["Force"].Item2));
    }

    [Fact]
    public void ParseArguments_BindsPositionalArgs_ViaIndexedAliases()
    {
        // Mirrors `az config set core.output.color always`: the [""] alias binds position 0 to
        // Key (matching the existing single-positional convention, e.g. `profile show <name>`),
        // and the ["1"] alias binds position 1 to Value.
        var provider = CreateProvider();

        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["key"] = new("Key", "key", typeof(string), "Configuration key.", true, [], []),
            ["value"] = new("Value", "value", typeof(string), "Configuration value.", true, [], []),
            ["force"] = new("Force", "force", typeof(bool), "Allow unknown keys.", false, [], []),
        };
        var parameterAliases = new Dictionary<string, string> { [""] = "key", ["1"] = "value" };
        var args = new Dictionary<string, object>
        {
            [string.Empty] = new List<List<string>> { new() { "core.output.color" }, new() { "always" } },
        };

        var parsed = provider.ParseArguments(args, parameters, parameterAliases);

        Assert.Equal("core.output.color", parsed["Key"].Item2);
        Assert.Equal("always", parsed["Value"].Item2);
        Assert.DoesNotContain("Force", parsed.Keys);
    }

    [Fact]
    public void ParseArguments_PositionalArgs_CoexistWithExplicitNamedFlag()
    {
        // `az config set core.output.color --value always`: the key is positional (position 0)
        // while the value is supplied explicitly via --value.
        var provider = CreateProvider();

        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["key"] = new("Key", "key", typeof(string), "Configuration key.", true, [], []),
            ["value"] = new("Value", "value", typeof(string), "Configuration value.", true, [], []),
        };
        var parameterAliases = new Dictionary<string, string> { [""] = "key", ["1"] = "value" };
        var args = new Dictionary<string, object>
        {
            [string.Empty] = new List<List<string>> { new() { "core.output.color" } },
            ["value"] = new List<List<string>> { new() { "always" } },
        };

        var parsed = provider.ParseArguments(args, parameters, parameterAliases);

        Assert.Equal("core.output.color", parsed["Key"].Item2);
        Assert.Equal("always", parsed["Value"].Item2);
    }

    [Fact]
    public void ParseArguments_PositionalArgs_WithoutIndexedAlias_Throws()
    {
        // A second positional token with no corresponding ["1"] alias must be rejected rather than
        // silently dropped.
        var provider = CreateProvider();

        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["key"] = new("Key", "key", typeof(string), "Configuration key.", true, [], []),
        };
        var parameterAliases = new Dictionary<string, string> { [""] = "key" };
        var args = new Dictionary<string, object>
        {
            [string.Empty] = new List<List<string>> { new() { "core.output.color" }, new() { "always" } },
        };

        var ex = Assert.Throws<UsageErrorException>(() => provider.ParseArguments(args, parameters, parameterAliases));

        Assert.Contains("Unrecognized positional argument", ex.Message);
    }

    [Fact]
    public void ParseArguments_PositionalArg_ConflictingWithExplicitFlag_Throws()
    {
        // `az config set core.output.color --key foo --value bar`: the positional token and the
        // --key flag both target Key. This must be rejected deterministically rather than silently
        // depending on dictionary iteration order to decide which one "wins".
        var provider = CreateProvider();

        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["key"] = new("Key", "key", typeof(string), "Configuration key.", true, [], []),
            ["value"] = new("Value", "value", typeof(string), "Configuration value.", true, [], []),
        };
        var parameterAliases = new Dictionary<string, string> { [""] = "key", ["1"] = "value" };
        var args = new Dictionary<string, object>
        {
            ["key"] = new List<List<string>> { new() { "foo" } },
            ["value"] = new List<List<string>> { new() { "bar" } },
            [string.Empty] = new List<List<string>> { new() { "core.output.color" } },
        };

        var ex = Assert.Throws<UsageErrorException>(() => provider.ParseArguments(args, parameters, parameterAliases));

        Assert.Contains("positionally and by flag", ex.Message);
    }

    [Fact]
    public void ParseArguments_IndexedPositionalAliasKey_NotAcceptedAsNamedFlag()
    {
        // Indexed ParameterAliases keys ("1", "2", ...) exist only to bind positional slots; a
        // user typing `--1 always` must not silently resolve as `--value always`. It should
        // surface the real problem (unknown parameter "1"), not succeed or produce a misleading
        // positional/flag conflict error.
        var provider = CreateProvider();

        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["key"] = new("Key", "key", typeof(string), "Configuration key.", true, [], []),
            ["value"] = new("Value", "value", typeof(string), "Configuration value.", true, [], []),
        };
        var parameterAliases = new Dictionary<string, string> { [""] = "key", ["1"] = "value" };
        var args = new Dictionary<string, object>
        {
            ["1"] = new List<List<string>> { new() { "always" } },
        };

        var ex = Assert.Throws<UsageErrorException>(() => provider.ParseArguments(args, parameters, parameterAliases));

        Assert.Contains("Unable to find parameter: 1", ex.Message);
    }

    [Fact]
    public void ParseArguments_MissingScalarValue_ErrorUsesShortAliasWhenUserTypedIt()
    {
        // When the user types a short alias (e.g. "-l" for "--long-flag"), the error should
        // reflect the alias the user typed, not the canonical form the alias resolves to.
        var provider = CreateProvider();

        var parameters = new Dictionary<string, ParameterMetadata>
        {
            ["long-flag"] = new("LongFlagProperty", "long-flag", typeof(string), "desc", true, ["l"], []),
        };
        var parameterAliases = new Dictionary<string, string> { ["l"] = "long-flag" };
        var args = new Dictionary<string, object>
        {
            ["l"] = new List<List<string>>(),
        };

        var ex = Assert.Throws<UsageErrorException>(() => provider.ParseArguments(args, parameters, parameterAliases));

        Assert.Contains("'l'", ex.Message);
        Assert.DoesNotContain("'long-flag'", ex.Message);
        Assert.DoesNotContain("LongFlagProperty", ex.Message);
    }
}
