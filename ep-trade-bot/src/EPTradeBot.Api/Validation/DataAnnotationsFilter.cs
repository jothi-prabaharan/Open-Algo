using EPTradeBot.Core.Validation;

namespace EPTradeBot.Api.Validation;

/// <summary>
/// Validates every request body argument with Data Annotations (recursively, so list
/// items are checked) and answers 400 with the field errors before the handler runs.
/// </summary>
internal sealed class DataAnnotationsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument is null || !IsRequestDto(argument.GetType())) continue;
            var results = AnnotationValidator.Validate(argument);
            if (results.Count > 0)
            {
                return TypedResults.ValidationProblem(
                    AnnotationValidator.ToProblemErrors(results),
                    title: "Some fields in the request are missing or not valid.");
            }
        }

        return await next(context);
    }

    private static bool IsRequestDto(Type t) =>
        t.Namespace is { } ns && ns.StartsWith("EPTradeBot.Core.Contracts", StringComparison.Ordinal);
}

internal static class ValidationExtensions
{
    public static TBuilder WithDataAnnotations<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, DataAnnotationsFilter>();
}
