using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace EPTradeBot.Core.Validation;

/// <summary>
/// Runs Data Annotations validation over an object graph. The framework's
/// <see cref="Validator"/> checks only the top-level object, so a basket order's
/// items or an options order's legs would go unchecked; this walks into nested
/// objects and lists and prefixes member names with their path (<c>orders[2].quantity</c>).
/// </summary>
public static class AnnotationValidator
{
    public static IReadOnlyList<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Walk(instance, "", results, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return results;
    }

    public static Dictionary<string, string[]> ToProblemErrors(IEnumerable<ValidationResult> results) =>
        results
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [""]).Select(m => (Member: m, Message: r.ErrorMessage ?? "Invalid value.")))
            .GroupBy(x => x.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Message).Distinct().ToArray());

    private static void Walk(object instance, string prefix, List<ValidationResult> results, HashSet<object> seen)
    {
        if (!seen.Add(instance)) return;

        var local = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), local, validateAllProperties: true);
        foreach (var r in local)
        {
            var members = r.MemberNames.Select(m => Join(prefix, JsonName(instance.GetType(), m))).ToArray();
            results.Add(new ValidationResult(r.ErrorMessage, members.Length > 0 ? members : [prefix]));
        }

        foreach (var prop in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || IsSimple(prop.PropertyType)) continue;
            var value = prop.GetValue(instance);
            if (value is null) continue;
            var name = Join(prefix, JsonName(instance.GetType(), prop.Name));
            if (value is IDictionary) continue;
            if (value is IEnumerable items and not string)
            {
                var i = 0;
                foreach (var item in items)
                {
                    if (item is not null && !IsSimple(item.GetType())) Walk(item, $"{name}[{i}]", results, seen);
                    i++;
                }
            }
            else
            {
                Walk(value, name, results, seen);
            }
        }
    }

    private static string JsonName(Type type, string member)
    {
        var prop = type.GetProperty(member);
        var attr = prop?.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
        return attr?.Name ?? member;
    }

    private static string Join(string prefix, string name) =>
        string.IsNullOrEmpty(prefix) ? name : name.StartsWith(prefix, StringComparison.Ordinal) ? name : $"{prefix}.{name}";

    private static bool IsSimple(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)
            || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(Guid) || t == typeof(DateTimeOffset)
            || t == typeof(TimeSpan) || t == typeof(System.Text.Json.JsonElement);
    }
}
