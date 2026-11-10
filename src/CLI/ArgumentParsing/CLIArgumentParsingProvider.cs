// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Core.Cli;

using System.Collections;
using Core.Abstractions;

public class CLIArgumentParsingProvider(PropertyParser propertyParser, ILoggingService loggingService) : IArgumentParsingProvider
{
    public IDictionary<string, (string, object)> ParseArguments(IDictionary<string, object> args, IDictionary<string, ParameterMetadata> parameters, IDictionary<string, string>? parameterAliases)
    {
        Dictionary<string, (string, object)> parsedArguments = new(StringComparer.OrdinalIgnoreCase);

        ParsePositionalArguments(args, parameters, parameterAliases, parsedArguments);

        // Positional tokens (empty-string key) are bound above; only named flags remain.
        foreach (var (invocationName, value) in args.Where(kvp => kvp.Key.Length > 0))
        {
            var parameter = ResolveParameter(invocationName, parameters, parameterAliases)
                ?? throw AzclipsErrorException.UsageError($"Unable to find parameter: {invocationName}");

            try
            {
                parsedArguments[parameter.Name] = (invocationName, ParseArgument(invocationName, value, parameter.ParameterType));
            }
            catch (Exception ex)
            {
                throw AzclipsErrorException.UsageError($"Failed to parse parameter '{invocationName}': {ex.Message}", innerException: ex);
            }
        }
        return parsedArguments;
    }

    // Binds positional tokens (the empty-string key) in order via indexed ParameterAliases ([""], "1", "2", ...), rejecting unrecognized extras and same-parameter flag conflicts. Binding is opt-in.
    private void ParsePositionalArguments(IDictionary<string, object> args, IDictionary<string, ParameterMetadata> parameters, IDictionary<string, string>? parameterAliases, Dictionary<string, (string, object)> parsedArguments)
    {
        if (!args.TryGetValue(string.Empty, out var positionalSource))
        {
            return;
        }

        var explicitlyNamed = GetExplicitlyNamedParameters(args, parameters, parameterAliases);
        var positionalValues = FlattenToStrings(positionalSource);
        for (int i = 0; i < positionalValues.Length; i++)
        {
            var positionalKey = i == 0 ? string.Empty : i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (parameterAliases == null || !parameterAliases.TryGetValue(positionalKey, out var canonicalKey) || !parameters.TryGetValue(canonicalKey, out var parameterMetadata))
            {
                throw AzclipsErrorException.UsageError($"Unrecognized positional argument(s): {string.Join(" ", positionalValues.Skip(i))}");
            }

            if (explicitlyNamed.Contains(parameterMetadata.Name))
            {
                throw AzclipsErrorException.UsageError($"Parameter '{parameterMetadata.InvocationName}' was specified both positionally and by flag ('--{parameterMetadata.InvocationName}').");
            }

            try
            {
                parsedArguments[parameterMetadata.Name] = (parameterMetadata.InvocationName, ParseArgument(parameterMetadata.InvocationName, positionalValues[i], parameterMetadata.ParameterType));
            }
            catch (Exception ex)
            {
                throw AzclipsErrorException.UsageError($"Failed to parse parameter '{parameterMetadata.InvocationName}': {ex.Message}", innerException: ex);
            }
        }
    }

    // Resolves named-flag args to canonical parameter names so positional binding can detect same-parameter conflicts deterministically.
    private HashSet<string> GetExplicitlyNamedParameters(IDictionary<string, object> args, IDictionary<string, ParameterMetadata> parameters, IDictionary<string, string>? parameterAliases)
    {
        var explicitlyNamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in args.Keys.Where(key => key.Length > 0))
        {
            if (ResolveParameter(key, parameters, parameterAliases) is { } metadata)
            {
                explicitlyNamed.Add(metadata.Name);
            }
        }
        return explicitlyNamed;
    }

    // Resolves a typed flag (canonical name or alias) to its parameter, or null if it is unknown. Digit-only keys are
    // reserved for indexed positional slots (ParameterAliases["1"], ...) and never resolve as named flags like `--1`.
    private ParameterMetadata? ResolveParameter(string invocationName, IDictionary<string, ParameterMetadata> parameters, IDictionary<string, string>? parameterAliases)
    {
        string name = invocationName;
        if (!parameters.ContainsKey(name) && !IsIndexedPositionalKey(name) && parameterAliases?.TryGetValue(name, out var canonical) == true)
        {
            loggingService.Verbose($"Resolving alias '{name}' to canonical parameter '{canonical}'");
            name = canonical;
        }

        if (!parameters.TryGetValue(name, out var parameter))
        {
            return null;
        }

        return parameter ?? throw new InvalidOperationException($"Parameter metadata is null for: {invocationName}");
    }

    // Lists (string[], List<string>, List<List<string>>, ...) take repeated values; everything else, including string and bool, takes one.
    private static bool AcceptsMultipleValues(Type type) => TryGetCollectionElementType(type, out _);

    // Digit-only keys are reserved for indexed positional slots (ParameterAliases["1"], ["2"], ...); a user-typed `--1` must fall through to "Unable to find parameter" rather than silently resolving as a named flag.
    private static bool IsIndexedPositionalKey(string key) => key.Length > 0 && key.All(char.IsAsciiDigit);

    private object ParseArgument(string invocationName, object source, Type targetType)
    {
        // Validate each CLI occurrence before flattening or passing nested collections through.
        if (source is List<List<string>> occurrences)
        {
            if (AcceptsMultipleValues(targetType))
            {
                if (occurrences.Count == 0 || occurrences.Any(values => values.Count == 0))
                {
                    throw AzclipsErrorException.UsageError($"Parameter '{invocationName}' requires a value.");
                }
            }
            else
            {
                var extra = occurrences.FirstOrDefault(values => values.Count > 1);
                if (extra is not null)
                {
                    throw AzclipsErrorException.UsageError($"Parameter '{invocationName}' accepts a single value but received {extra.Count}.");
                }

                // Like classic az (argparse), a repeated single-value flag keeps its last occurrence.
                if (occurrences.Count > 1)
                {
                    // Parse the earlier occurrences only to validate them; the last one continues below.
                    foreach (var occurrence in occurrences.Take(occurrences.Count - 1))
                    {
                        _ = ParseArgument(invocationName, occurrence, targetType);
                    }
                    source = occurrences[^1];
                }
            }
        }

        // Already the expected type
        if (targetType.IsInstanceOfType(source))
        {
            return source;
        }

        // Collection target (string[], List<string>, string[][], List<List<string>>, etc.)
        // CLI raw value is List<List<string>>; convert it into the target collection shape.
        if (TryGetCollectionElementType(targetType, out var elementType))
        {
            return ConvertToCollection(invocationName, source, targetType, elementType!);
        }

        // Scalar target: occurrences were validated above, so at most one value remains.
        var flattened = FlattenToStrings(source);
        if (flattened.Length == 0)
        {
            if (targetType == typeof(bool))
            {
                return true;
            }
            throw AzclipsErrorException.UsageError($"Parameter '{invocationName}' requires a value.");
        }
        return propertyParser.ConvertToType(invocationName, flattened[0], targetType);
    }

    // List<List<string>> -> string[] (flattened)
    private static string[] FlattenToStrings(object source) =>
        source switch
        {
            List<List<string>> listOfLists => listOfLists.SelectMany(l => l).ToArray(),
            List<string> list => list.ToArray(),
            string[] arr => arr,
            string s => [s],
            IEnumerable<string> e => e.ToArray(),
            _ => [source?.ToString() ?? string.Empty],
        };

    private static bool TryGetCollectionElementType(Type type, out Type? elementType)
    {
        if (type == typeof(string))
        {
            elementType = null;
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType is not null;
        }

        if (type.IsGenericType)
        {
            var ienum = new[] { type }
                .Concat(type.GetInterfaces())
                .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (ienum is not null)
            {
                elementType = ienum.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null;
        return false;
    }

    private object ConvertToCollection(string invocationName, object source, Type targetType, Type elementType)
    {
        // Determine if elementType itself is a collection (nested case: List<List<string>>, string[][])
        bool nested = TryGetCollectionElementType(elementType, out var nestedElementType);

        var items = new List<object?>();

        if (nested)
        {
            // Build outer list of inner collections
            // Source can be List<List<string>>, List<string> (treat as single inner), or scalar (single inner of one)
            IEnumerable<IEnumerable<string>> outer = source switch
            {
                List<List<string>> listOfLists => listOfLists,
                List<string> list => [list],
                string s => [new List<string> { s }],
                _ => [new List<string> { source?.ToString() ?? string.Empty }],
            };
            foreach (var inner in outer)
            {
                items.Add(BuildInnerCollection(inner, elementType, nestedElementType!));
            }
        }
        else
        {
            // Flat collection: flatten everything to strings, then convert each to elementType
            foreach (var s in FlattenToStrings(source))
            {
                items.Add(ConvertScalar(invocationName, s, elementType));
            }
        }

        return Materialize(items, targetType, elementType);
    }

    private object BuildInnerCollection(IEnumerable<string> source, Type targetType, Type elementType)
    {
        var items = new List<object?>();
        foreach (var s in source)
        {
            items.Add(ConvertScalar(string.Empty, s, elementType));
        }
        return Materialize(items, targetType, elementType);
    }

    private object ConvertScalar(string invocationName, string value, Type targetType)
    {
        if (targetType == typeof(string))
        {
            return value;
        }
        return propertyParser.ConvertToType(invocationName, value, targetType);
    }

    private static object Materialize(IList<object?> items, Type targetType, Type elementType)
    {
        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                array.SetValue(items[i], i);
            }
            return array;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var item in items)
        {
            list.Add(item);
        }

        if (targetType.IsAssignableFrom(listType))
        {
            return list;
        }

        try
        {
            return Activator.CreateInstance(targetType, list)!;
        }
        catch
        {
            return list;
        }
    }

    // PowerShell should have validated most of cases
    // This method is mainly for CLI required, allowed values validation
    public bool ValidateArguments(IDictionary<string, (string, object)> args, IDictionary<string, ParameterMetadata> parameters, out IList<string>? errors)
    {
        var errorList = new List<string>();

        // Check for unknown arguments and allowed values
        foreach (var kvp in args)
        {
            var (invocationName, value) = kvp.Value;
            if (!parameters.TryGetValue(kvp.Key, out var metadata))
            {
                errorList.Add($"Unknown parameter: '{invocationName}'");
                continue;
            }

            if (metadata.AllowedValues is { Length: > 0 }
                && value is string strValue
                && !metadata.AllowedValues.Contains(strValue, StringComparer.OrdinalIgnoreCase))
            {
                errorList.Add($"Parameter '{invocationName}' value '{strValue}' is not allowed. Allowed values: {string.Join(", ", metadata.AllowedValues)}");
            }
        }

        foreach (var parameter in parameters.Values)
        {
            if (parameter.IsRequired && !args.ContainsKey(parameter.Name))
            {
                errorList.Add($"Required parameter '--{parameter.InvocationName}' is missing.");
            }
        }

        errors = errorList;
        return errorList.Count == 0;
    }

    public string? ResolveParameterSet(IDictionary<string, (string, object)> parsedArgs, IDictionary<string, ParameterMetadata> parameters, IDictionary<string, ParameterSetMetadata>? parameterSets, Func<IDictionary<string, (string, object)>, IDictionary<string, ParameterMetadata>, IDictionary<string, ParameterSetMetadata>?, (string?, IList<string>?)> defaultResolve, out IList<string>? errors)
    {
        var (resolved, errorList) = defaultResolve(parsedArgs, parameters, parameterSets);
        errors = errorList;
        return resolved;
    }
}
