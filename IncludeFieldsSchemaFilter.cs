using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Coflnet.Sky.PlayerState;

/// <summary>
/// Many models here (ExtractedInfo, Composter, KatStatus, ForgeItem, ChestView, StateObject, ...)
/// declare public fields instead of properties for compact MessagePack serialization, and the API
/// serializes them fine at runtime via JsonSerializerOptions.IncludeFields. Swashbuckle's schema
/// generator does not respect that option and only reflects properties, so those fields would
/// otherwise be missing from swagger.json (and thus from the generated OpenAPI client).
/// </summary>
public class IncludeFieldsSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema openApiSchema)
            return;
        var fields = context.Type.GetFields(BindingFlags.Instance | BindingFlags.Public);
        if (fields.Length == 0)
            return;
        openApiSchema.Properties ??= new Dictionary<string, IOpenApiSchema>();
        foreach (var field in fields)
        {
            if (field.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                continue;
            var name = field.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(field.Name);
            if (openApiSchema.Properties.ContainsKey(name))
                continue;
            openApiSchema.Properties[name] = context.SchemaGenerator.GenerateSchema(field.FieldType, context.SchemaRepository);
        }
    }
}
