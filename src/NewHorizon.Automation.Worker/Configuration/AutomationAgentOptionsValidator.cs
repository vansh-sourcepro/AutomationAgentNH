using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Configuration;

namespace NewHorizon.Automation.Worker.Configuration;

/// <summary>
/// Validates the bootstrap options including the nested sections. The built-in
/// <c>ValidateDataAnnotations</c> does not recurse into complex properties, so the Range/Required
/// attributes on Database/ErpApi/Host/Defaults would otherwise never be evaluated.
/// </summary>
public sealed class AutomationAgentOptionsValidator : IValidateOptions<AutomationAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, AutomationAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        ValidateSection(options.Database, nameof(options.Database), failures);
        ValidateSection(options.ErpApi, nameof(options.ErpApi), failures);
        ValidateSection(options.Host, nameof(options.Host), failures);
        ValidateSection(options.Defaults, nameof(options.Defaults), failures);
        ValidateSection(options.Serilog, nameof(options.Serilog), failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSection(object section, string sectionName, List<string> failures)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(section);

        if (Validator.TryValidateObject(section, context, results, validateAllProperties: true))
        {
            return;
        }

        foreach (var result in results)
        {
            var members = result.MemberNames.Any()
                ? string.Join(", ", result.MemberNames)
                : "(section)";

            failures.Add(
                $"{AutomationAgentOptions.SectionName}:{sectionName}:{members} — {result.ErrorMessage}");
        }
    }
}
