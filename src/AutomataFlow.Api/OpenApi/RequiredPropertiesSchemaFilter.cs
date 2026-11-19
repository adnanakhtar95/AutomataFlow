using System.Reflection;
using System.Text.Json;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AutomataFlow.Api.OpenApi;

/// <summary>
/// Marks every property whose C# type is not nullable as <c>required</c> in the OpenAPI document (ADR-076). The Api always
/// serializes every property, so a non-nullable property is always present; nullable ones stay optional. Without this
/// the generated TypeScript types would make every field optional and hide real contract changes.
/// </summary>
public sealed class RequiredPropertiesSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema concrete || concrete.Properties is null || context.Type.IsEnum || context.Type.IsPrimitive)
        {
            return;
        }

        foreach (var property in context.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (!concrete.Properties.ContainsKey(name) || IsNullable(property))
            {
                continue;
            }

            concrete.Required ??= new HashSet<string>(StringComparer.Ordinal);
            concrete.Required.Add(name);
        }
    }

    private static bool IsNullable(PropertyInfo property)
    {
        if (property.PropertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(property.PropertyType) is not null;
        }

        return new NullabilityInfoContext().Create(property).ReadState != NullabilityState.NotNull;
    }
}
