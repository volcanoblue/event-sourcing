using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace VolcanoBlue.EventSourcing.Serialization;

/// <summary>
/// Fast path for reading the common scalar types: reading them straight off the reader skips the per-value
/// setup of <see cref="JsonSerializer"/>. Anything else, or an unexpected token, goes through the serializer, so the
/// accepted JSON (and the errors) are the ones System.Text.Json defines.
/// </summary>
internal enum ScalarKind : byte { None, String, Int32, Int64, Boolean, Decimal, Double, Guid, DateTime, DateTimeOffset }

internal static class JsonScalars
{
    public static ScalarKind KindOf(Type type) =>
        type == typeof(string) ? ScalarKind.String :
        type == typeof(int) ? ScalarKind.Int32 :
        type == typeof(long) ? ScalarKind.Int64 :
        type == typeof(bool) ? ScalarKind.Boolean :
        type == typeof(decimal) ? ScalarKind.Decimal :
        type == typeof(double) ? ScalarKind.Double :
        type == typeof(Guid) ? ScalarKind.Guid :
        type == typeof(DateTime) ? ScalarKind.DateTime :
        type == typeof(DateTimeOffset) ? ScalarKind.DateTimeOffset :
        ScalarKind.None;

    /// <summary>Reads the value the reader is positioned on.</summary>
    public static object? Read(ref Utf8JsonReader reader, ScalarKind kind, JsonTypeInfo info)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                switch (kind)
                {
                    case ScalarKind.String: return reader.GetString();
                    case ScalarKind.Guid: return reader.GetGuid();
                    case ScalarKind.DateTime: return reader.GetDateTime();
                    case ScalarKind.DateTimeOffset: return reader.GetDateTimeOffset();
                }

                break;
            case JsonTokenType.Number:
                switch (kind)
                {
                    case ScalarKind.Int32: return reader.GetInt32();
                    case ScalarKind.Int64: return reader.GetInt64();
                    case ScalarKind.Decimal: return reader.GetDecimal();
                    case ScalarKind.Double: return reader.GetDouble();
                }

                break;
            case JsonTokenType.True:
            case JsonTokenType.False:
                if (kind == ScalarKind.Boolean)
                    return reader.GetBoolean();

                break;
            case JsonTokenType.Null:
                if (kind == ScalarKind.String)
                    return null;

                break;
        }

        return JsonSerializer.Deserialize(ref reader, info);
    }
}
