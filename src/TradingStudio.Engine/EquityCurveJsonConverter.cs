using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingStudio.Engine;

public class EquityCurveJsonConverter : JsonConverter<(DateTimeOffset Time, decimal Equity)>
{
    public override (DateTimeOffset Time, decimal Equity) Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject");
        DateTimeOffset time = default; decimal equity = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return (time, equity);
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected PropertyName");
            var prop = reader.GetString(); reader.Read();
            if (prop == "t") time = reader.GetDateTimeOffset();
            else if (prop == "e") equity = reader.GetDecimal();
            else reader.Skip();
        }
        throw new JsonException("Unterminated object");
    }

    public override void Write(Utf8JsonWriter writer, (DateTimeOffset Time, decimal Equity) value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("t", value.Time);
        writer.WriteNumber("e", value.Equity);
        writer.WriteEndObject();
    }
}