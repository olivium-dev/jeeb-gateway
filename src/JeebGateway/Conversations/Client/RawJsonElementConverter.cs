using System;
using Newtonsoft.Json;
using Stj = System.Text.Json;

namespace JeebGateway.Conversations.Client;

/// <summary>
/// S08 (H4) — bridges a <see cref="System.Text.Json.JsonElement"/> across the
/// <b>Newtonsoft</b> serializer the chat-service typed client uses.
///
/// <para>
/// WHY THIS EXISTS — the H4 marshaling bug. The conversation BFF mixes two JSON
/// stacks: the ASP.NET model binder + response serializer are System.Text.Json
/// (<c>AddControllers()</c> with no <c>AddNewtonsoftJson()</c>), while the chat
/// client wire is Newtonsoft (the repo-wide chat serializer). The append message's
/// <c>audience</c> / <c>payload</c> are open shapes the gateway carries verbatim
/// (chat owns their meaning). When a free <c>object?</c> held an STJ-bound
/// <see cref="System.Text.Json.JsonElement"/> and Newtonsoft serialized it, the
/// wire became <c>{"audience":{"ValueKind":3}}</c> — Newtonsoft reflected over the
/// JsonElement struct instead of writing its value, so chat-service received a
/// MALFORMED audience/payload (the nulled-audience symptom). The reverse leg was
/// equally broken: Newtonsoft deserialized chat-service's body into a
/// <c>JObject</c>/<c>JValue</c>, and the STJ response serializer then emitted
/// <c>{"x":[]}</c> for a structured payload (STJ reflecting over JObject internals).
/// </para>
///
/// <para>
/// THE FIX — pin a single canonical representation: <see cref="System.Text.Json.JsonElement"/>
/// end-to-end. The STJ model binder yields it natively; the STJ response serializer
/// emits it natively; this converter teaches Newtonsoft to read/write it as the RAW
/// JSON token it represents (not its struct shape). So <c>"all"</c> stays
/// <c>"all"</c> and <c>{"amount":42}</c> stays <c>{"amount":42}</c> on BOTH legs —
/// the gateway round-trips the open shape faithfully and reshapes nothing
/// (thin-BFF: chat owns the audience/payload meaning).
/// </para>
/// </summary>
public sealed class RawJsonElementConverter : JsonConverter<Stj.JsonElement?>
{
    public override void WriteJson(JsonWriter writer, Stj.JsonElement? value, JsonSerializer serializer)
    {
        if (value is null || value.Value.ValueKind == Stj.JsonValueKind.Null
            || value.Value.ValueKind == Stj.JsonValueKind.Undefined)
        {
            writer.WriteNull();
            return;
        }

        // Emit the element's RAW JSON verbatim — never reflect over the struct.
        writer.WriteRawValue(value.Value.GetRawText());
    }

    public override Stj.JsonElement? ReadJson(
        JsonReader reader,
        Type objectType,
        Stj.JsonElement? existingValue,
        bool hasExistingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        // Materialise the Newtonsoft token, then re-parse its raw JSON as a
        // System.Text.Json element so the value (string "all", structured object,
        // array, …) is preserved exactly for the STJ response serializer.
        var token = Newtonsoft.Json.Linq.JToken.Load(reader);
        var raw = token.ToString(Formatting.None);
        using var doc = Stj.JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
