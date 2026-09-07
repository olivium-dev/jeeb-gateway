namespace JeebGateway.FormSubmissions;

public static class FormTemplateRegistry
{
    public const string Onboarding = "jeeb_jeeber_onboarding_v1";
    public static bool IsKnown(string templateName) => templateName == Onboarding;
}
