// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Core.Abstractions;

namespace Core.ArgumentParsing
{
    public class ArgumentParsingService(IArgumentParsingProvider provider, IProfileService profileService) : IArgumentParsingService
    {
        public IProcessor BindArgumentsToProcessor(IProcessor processor, IDictionary<string, object> args, CommandMetadata command, Profile? profile)
        {
            IDictionary<string, ParameterMetadata> parameters = command.Parameters;
            IDictionary<string, ParameterMetadata> canonicalParameters = parameters.ToDictionary(kvp => kvp.Value.Name, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            IDictionary<string, string>? parameterAliases = command.ParameterAliases;
            IDictionary<string, ParameterSetMetadata>? parameterSets = command.ParameterSets;
            IDictionary<string, (string, object)> parsedArgs = provider.ParseArguments(args, parameters, parameterAliases);

            var argsWithDefaults = new Dictionary<string, (string, object)>(parsedArgs);
            var missingProfileDefaults = new List<string>();
            foreach (var key in profileService.GetProfileKeysForDefault())
            {
                object? value = argsWithDefaults.TryGetValue(key, out var parsedArg) ? parsedArg.Item2 : null;
                if (value is null && canonicalParameters.ContainsKey(key))
                {
                    string invocationName = canonicalParameters[key].InvocationName;
                    if (profileService.GetProfileValueByKey(profile, key) is not {} profileValue)
                    {
                        missingProfileDefaults.Add($"{invocationName} is not provided from input and not present in profile either");
                        continue;
                    }
                    argsWithDefaults[key] = (invocationName, profileValue);
                }
            }

            if (!provider.ValidateArguments(argsWithDefaults, canonicalParameters, out IList<string>? argumentValidationErrors) && argumentValidationErrors is not null && argumentValidationErrors.Count > 0)
            {
                var message = $"Invalid arguments: {string.Join(Environment.NewLine, argumentValidationErrors)}";
                if (canonicalParameters.Values.Any(parameter => parameter.IsRequired && !argsWithDefaults.ContainsKey(parameter.Name)))
                {
                    throw AzclipsErrorException.MissingRequiredParameters(message);
                }
                throw AzclipsErrorException.UsageError(message);
            }

            // Preserve explicit-input set resolution; profile defaults must not select or exclude a set.
            string? resolvedParameterSetName = provider.ResolveParameterSet(parsedArgs, canonicalParameters, parameterSets, ValidateParameterSets, out IList<string>? parameterSetValidationErrors);
            if (parameterSetValidationErrors is not null && parameterSetValidationErrors.Count > 0)
            {
                var message = $"Invalid parameter set: {string.Join(Environment.NewLine, parameterSetValidationErrors)}";
                throw AzclipsErrorException.UsageError(message);
            }

            if (missingProfileDefaults.Count > 0)
            {
                throw AzclipsErrorException.MissingRequiredParameters(string.Join(Environment.NewLine, missingProfileDefaults));
            }

            argsWithDefaults.ToList().ForEach(p => SetProperty(processor, p.Key, p.Value.Item2));
            return processor;
        }

        private (string?, IList<string>?) ValidateParameterSets(IDictionary<string, (string, object)> parsedArgs, IDictionary<string, ParameterMetadata> parameters, IDictionary<string, ParameterSetMetadata>? parameterSets)
        {
            if (parameterSets is null || parameterSets.Count == 0)
            {
                return (null, null);
            }

            List<string> errorList = new List<string>();
            HashSet<string> candidates = parameterSets.Select(kvp => kvp.Key).ToHashSet();
            HashSet<ParameterMetadata> argsMetadata = parsedArgs.Keys.Select(k => parameters[k]).ToHashSet();
            
            // Validate whether the coming arg matches any remaining candidates, return error if none
            foreach (var metadata in argsMetadata)
            {
                // Parameter set validation opt-in for now when not all syntax have paramter set defined
                // Once syntax are properly populated, "metadata.ParameterSets is null" should not happen
                // Validate it in build-time when generating syntax
                if (metadata.ParameterSets is null)
                {
                    continue;
                }
                candidates.IntersectWith(metadata.ParameterSets.Keys);
                if (candidates.Count == 0)
                {
                    errorList.Add($"Parameter: {metadata.InvocationName} cannot be resolved to any parameter set");
                    return (null, errorList);
                }
            }

            HashSet<ParameterSetMetadata> candidatesMetadata = candidates.Select(candidate => parameterSets[candidate]).ToHashSet();
            candidatesMetadata.RemoveWhere(candidate =>
            {                
                if (IsValidCandidate(argsMetadata, candidate))
                {
                    return false;
                }
                string[] members = candidate.Parameters!.Keys.Select(k => parameters[k].InvocationName).ToArray();
                errorList.Add($"Input: [{string.Join(", ", argsMetadata.Select(p => p.InvocationName))}] cannot be resolved for candidate: [{string.Join(", ", members)}]");
                return true;
            });

            if (candidatesMetadata.Count != 1)
            {
                if (errorList.Count == 0)
                {
                    foreach (var candidate in candidatesMetadata)
                    {
                        string[] members = candidate.Parameters!.Keys.Select(k => parameters[k].InvocationName).ToArray();
                        errorList.Add($"Cannot resolve ambiguous input, possible candidate: [{string.Join(", ", members)}]");
                    }
                }
                return (null, errorList);
            }
            return (candidatesMetadata.FirstOrDefault()!.Name, null);
        }

        private bool IsValidCandidate(HashSet<ParameterMetadata> args, ParameterSetMetadata candidate)
        {
            // filter 1. validate whether args have all required parameters for candidate
            // filter 2. validate whether args have any extra parameters not defined in the candidate. 
            // for example: ps1->[subid, rg, name]; ps2->[subid, rg]; input {subid, rg, name} survives filter 1, will be filtered in filter 2
            // Defensive, for "metadata.ParameterSets is null" escaped previous check
            // filter 3. validate whether there is only one candidate left
            foreach (ParameterReference pr in candidate.Parameters!.Values)
            {
                if (pr.IsRequired && !args.Any(a => a.Name == pr.Name))
                {
                    return false;
                }
            }

            foreach (var arg in args)
            {
                if (!candidate.Parameters!.ContainsKey(arg.Name))
                {
                    return false;
                }
            }
            
            return true;
        }

        private void SetProperty(IProcessor processor, string propertyName, object? value)
        {
            var propertyInfo = processor.GetType().GetProperty(propertyName);
            if (propertyInfo == null)
            {
                throw new ArgumentException($"The property '{propertyName}' does not exist on processor '{processor.GetType().FullName}'.");
            }
            try
            {
                if (value != null)
                {
                    // Values already assignable to the target type (e.g. a string[] for a collection
                    // parameter such as --scope/-Scope) must be assigned directly: Convert.ChangeType
                    // only handles IConvertible scalars and throws for arrays and other collections.
                    var convertedValue = propertyInfo.PropertyType.IsInstanceOfType(value)
                        ? value
                        : Convert.ChangeType(value, propertyInfo.PropertyType);
                    propertyInfo.SetValue(processor, convertedValue);
                }
                else
                {
                    propertyInfo.SetValue(processor, null);
                }
            }
            catch (Exception e)
            {
                throw new ArgumentException($"The property '{propertyName}' on processor '{processor.GetType().FullName}' could not be set to value '{value}'.", e);
            }
        }
    }
}