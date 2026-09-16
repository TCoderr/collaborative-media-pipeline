using System.Text;
using Microsoft.EntityFrameworkCore;

namespace MediaPipeline.Application.Persistence;

/// <summary>
/// Maps CLR names to PostgreSQL's conventional snake_case, so nothing in the schema needs
/// quoting when an operator opens psql during an incident.
/// </summary>
internal static class SnakeCaseNaming
{
    public static void ApplySnakeCaseNames(this ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var tableName = entity.GetTableName();
            if (tableName is not null)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            foreach (var property in entity.GetProperties())
            {
                // xmin is a PostgreSQL system column; renaming it would break the mapping.
                var current = property.GetColumnName();
                if (string.Equals(current, "xmin", StringComparison.Ordinal))
                {
                    continue;
                }

                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entity.GetKeys())
            {
                var name = key.GetName();
                if (name is not null)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                var name = fk.GetConstraintName();
                if (name is not null)
                {
                    fk.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                var name = index.GetDatabaseName();
                if (name is not null)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previousIsLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (i > 0 && (previousIsLower || nextIsLower) && builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
