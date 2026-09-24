using System.Reflection;
using System.Text.RegularExpressions;

namespace Deedbox;

/// <summary>One numbered, forward-only schema script. The script holds a schema placeholder.</summary>
internal sealed record Migration(int Version, string Name, string Script)
{
    public const string SchemaPlaceholder = "{{schema}}";

    public string ScriptFor(string schema) => Script.Replace(SchemaPlaceholder, schema, StringComparison.Ordinal);

    /// <summary>Reads every <c>NNNN_name.sql</c> resource under <paramref name="prefix"/>, in version order.</summary>
    public static IReadOnlyList<Migration> LoadEmbedded(Assembly assembly, string prefix)
    {
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal))
                continue;

            var file = resource[prefix.Length..^".sql".Length];
            var separator = file.IndexOf('_', StringComparison.Ordinal);
            var version = int.Parse(file[..separator], System.Globalization.CultureInfo.InvariantCulture);

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            migrations.Add(new Migration(version, file[(separator + 1)..], reader.ReadToEnd()));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
                throw new InvalidOperationException($"Migrations under {prefix} must be numbered 1..n without gaps.");
        }

        return migrations;
    }
}

internal static partial class SchemaName
{
    public const string Default = "deedbox";

    public static string Validate(string schema)
    {
        if (!Pattern().IsMatch(schema))
        {
            throw new DeedboxException(Errors.InvalidSchemaName,
                $"Schema name '{schema}' is not valid. Use lower-case letters, digits and underscores, starting with a letter or underscore, at most 63 characters.");
        }

        return schema;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Pattern();
}
