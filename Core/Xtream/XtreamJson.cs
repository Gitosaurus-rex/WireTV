using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTv.Core.Xtream;

/// <summary>
/// Xtream Codes panels are wildly inconsistent about JSON types: the same field
/// comes back as a string on one server and a number on the next, and optional
/// fields arrive as null, "", 0 or false depending on the panel version.
///
/// These converters accept whatever a panel sends rather than throwing, because a
/// type mismatch in one field must not fail the whole channel import.
/// </summary>
internal sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            // Skip anything structured (some panels return [] for an empty field).
            _ => SkipAndReturnNull(ref reader)
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);

    private static string? SkipAndReturnNull(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }
}

/// <summary>Reads an int that a panel may have encoded as a string, or omitted.</summary>
internal sealed class FlexibleNullableIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) ? number : null;

            case JsonTokenType.String:
                var text = reader.GetString();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

            case JsonTokenType.True:
                return 1;

            case JsonTokenType.False:
            case JsonTokenType.Null:
                return null;

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteNumberValue(value.Value);
    }
}

/// <summary>Reads a long, used for the unix timestamps panels return as strings.</summary>
internal sealed class FlexibleNullableLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number) ? number : null;

            case JsonTokenType.String:
                var text = reader.GetString();
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

            case JsonTokenType.Null:
                return null;

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteNumberValue(value.Value);
    }
}
