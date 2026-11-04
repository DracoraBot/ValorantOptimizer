namespace ValorantOptimizer.Core;

/// <summary>Static validation of a catalog, run at startup and in tests.</summary>
public static class OptimizationCatalogValidator
{
    public static IReadOnlyList<string> Validate(IEnumerable<IOptimization> optimizations)
    {
        ArgumentNullException.ThrowIfNull(optimizations);
        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (IOptimization optimization in optimizations)
        {
            OptimizationDescriptor descriptor = optimization.Descriptor;
            if (!IsValidId(descriptor.Id))
            {
                errors.Add($"'{descriptor.Id}' is not a valid optimization id.");
            }

            if (!seenIds.Add(descriptor.Id))
            {
                errors.Add($"Duplicate optimization id '{descriptor.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.Name))
            {
                errors.Add($"'{descriptor.Id}' has no name.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.Summary))
            {
                errors.Add($"'{descriptor.Id}' has no summary.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.WhatItChanges))
            {
                errors.Add($"'{descriptor.Id}' does not describe what it changes.");
            }

            if (string.IsNullOrWhiteSpace(descriptor.Why))
            {
                errors.Add($"'{descriptor.Id}' does not explain why it might help.");
            }

            foreach (RegistryScope scope in descriptor.ManagedScopes)
            {
                if (string.IsNullOrWhiteSpace(scope.SubKey))
                {
                    errors.Add($"'{descriptor.Id}' declares an empty registry scope.");
                }
            }
        }

        return errors;
    }

    private static bool IsValidId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length is < 3 or > 80)
        {
            return false;
        }

        if (!char.IsAsciiLetterLower(id[0]) && !char.IsAsciiDigit(id[0]))
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not '.' and not '-')
            {
                return false;
            }
        }

        return true;
    }
}
