using System.Text.Json;
using JeebGateway.FormSubmissions;
using Xunit;

namespace JeebGateway.IntegrationTests.FormSubmissions;

public sealed class TemplateSchemaValidatorTests
{
    [Fact]
    public void Valid_answers_round_trip_objects_without_reinterpreting_strings()
    {
        var body = FormFixtures.Json("""{"state":"{}","country":"Lebanon","address":"Home","home_base":{"lat":0,"lng":0}}""");
        Assert.Null(TemplateSchemaValidator.Validate(FormFixtures.Schema, FormFixtures.Document, body));
        var answers = TemplateSchemaValidator.ToAnswers(body);
        Assert.IsType<string>(TemplateSchemaValidator.Restore(FormFixtures.Schema, FormFixtures.Document, answers)["state"]);
        Assert.IsType<JsonElement>(TemplateSchemaValidator.Restore(FormFixtures.Schema, FormFixtures.Document, answers)["home_base"]);
    }

    [Theory]
    [InlineData("""{"country":"x","address":"x","home_base":{"lat":0,"lng":0}}""", "state")]
    [InlineData("""{"state":"  ","country":"x","address":"x","home_base":{"lat":0,"lng":0}}""", "state")]
    [InlineData("""{"state":7,"country":"x","address":"x","home_base":{"lat":0,"lng":0}}""", "state")]
    [InlineData("""{"state":"x","country":"x","address":"x","home_base":"bad"}""", "home_base")]
    [InlineData("""{"state":"x","country":"x","address":"x","home_base":{"lat":95,"lng":0}}""", "home_base.lat")]
    [InlineData("""{"state":"x","country":"x","address":"x","home_base":{"lat":0,"lng":0},"vehicle_number":"x"}""", "vehicle_number")]
    [InlineData("""{"state":"x","state":"y","country":"x","address":"x","home_base":{"lat":0,"lng":0}}""", "state")]
    public void Invalid_field_is_identified(string body, string field) =>
        Assert.Equal(field, TemplateSchemaValidator.Validate(FormFixtures.Schema, FormFixtures.Document, FormFixtures.Json(body))?.Field);

    [Fact]
    public void Maximum_length_comes_from_template_not_a_retyped_field_rule()
    {
        var document = FormFixtures.Json(FormFixtures.Document.GetRawText().Replace("256", "3"));
        Assert.Equal("state", TemplateSchemaValidator.Validate(FormFixtures.Schema, document, FormFixtures.Body)?.Field);
        var body = JsonSerializer.SerializeToElement(new { state = new string('x', 257), country = "x", address = "x", home_base = new { lat = 0, lng = 0 } });
        Assert.Equal("state", TemplateSchemaValidator.Validate(FormFixtures.Schema, FormFixtures.Document, body)?.Field);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"components":[],"required_fields":[]}""")]
    [InlineData("""{"components":[{"componentID":"state","type":"unrecognized"}],"required_fields":[]}""")]
    public void Broken_schema_fails_closed(string schema) =>
        Assert.Throws<JsonException>(() => TemplateSchemaValidator.Validate(FormFixtures.Json(schema), FormFixtures.Document, FormFixtures.Body));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"state":"","country":"Lebanon","address":"Home","home_base":{"lat":0,"lng":0}}""")]
    [InlineData("""{"state":"Beirut","country":"Lebanon","address":"Home","home_base":{}}""")]
    [InlineData("""{"state":"Beirut","country":"Lebanon","address":"Home","home_base":{"lat":91,"lng":0}}""")]
    public void Incomplete_or_invalid_stored_answers_do_not_restore(string body) =>
        Assert.Throws<JsonException>(() => TemplateSchemaValidator.Restore(FormFixtures.Schema, FormFixtures.Document,
            TemplateSchemaValidator.ToAnswers(FormFixtures.Json(body))));

    [Fact]
    public void Stored_strings_obey_the_current_template_limit()
    {
        var body = JsonSerializer.SerializeToElement(new { state = new string('x', 257), country = "x", address = "x", home_base = new { lat = 0, lng = 0 } });
        Assert.Throws<JsonException>(() => TemplateSchemaValidator.Restore(FormFixtures.Schema, FormFixtures.Document,
            TemplateSchemaValidator.ToAnswers(body)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{}]")]
    public void Empty_or_incomplete_template_is_a_definition_failure(string document) =>
        Assert.Throws<JsonException>(() => TemplateSchemaValidator.Validate(FormFixtures.Schema, FormFixtures.Json(document), FormFixtures.Body));

    [Theory]
    [InlineData("output", "broken_output")]
    [InlineData("\"type\":\"object\"", "\"type\":\"string\"")]
    [InlineData("\"rule\":\"required\"", "\"rule\":\"optional\"")]
    [InlineData("\"componentID\":\"street\"", "\"componentID\":\"state\"")]
    public void Mismatched_template_is_a_definition_failure(string before, string after) =>
        Assert.Throws<JsonException>(() => TemplateSchemaValidator.Validate(FormFixtures.Schema,
            FormFixtures.Json(FormFixtures.Document.GetRawText().Replace(before, after)), FormFixtures.Body));
}

public sealed class HomeBaseRulesTests
{
    [Theory]
    [InlineData("""{"lat":-90,"lng":-180}""")]
    [InlineData("""{"lat":90,"lng":180,"label":""}""")]
    public void Geographic_edges_are_valid(string value) => Assert.Null(HomeBaseRules.Validate(FormFixtures.Json(value)));

    [Theory]
    [InlineData("""{"lng":0}""", "home_base.lat")]
    [InlineData("""{"lat":0,"lng":181}""", "home_base.lng")]
    [InlineData("""{"lat":"0","lng":0}""", "home_base.lat")]
    [InlineData("""{"lat":1e400,"lng":0}""", "home_base.lat")]
    [InlineData("""{"lat":0,"lng":0,"label":7}""", "home_base.label")]
    [InlineData("""{"lat":0,"lat":1,"lng":0}""", "home_base.lat")]
    [InlineData("""{"lat":0,"lng":0,"extra":{"large":"payload"}}""", "home_base.extra")]
    public void Invalid_coordinate_or_label_is_rejected(string value, string field) =>
        Assert.Equal(field, HomeBaseRules.Validate(FormFixtures.Json(value))?.Field);

    [Fact]
    public void Long_label_is_rejected() => Assert.Equal("home_base.label", HomeBaseRules.Validate(
        JsonSerializer.SerializeToElement(new { lat = 0, lng = 0, label = new string('x', 257) }))?.Field);
}
