using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox;

internal sealed record ProjectionRegistration(
    string Name,
    Run Run,
    Type Type,
    Func<IServiceProvider, ProjectionBase> Create);

internal sealed record RegisteredProjection(string Name, Run Run, ProjectionBase Instance);

internal sealed record SubscriptionRegistration(string Name, Type Type, Func<IServiceProvider, Subscription> Create);

internal sealed record RegisteredSubscription(string Name, Subscription Instance);

/// <summary>
/// The app's projections, created once from the root container. Handlers must keep no state between
/// events; per-event services come from <see cref="ProjectionContext.Services"/>.
/// </summary>
internal sealed class ProjectionSet
{
    public ProjectionSet(DeedboxRuntime runtime, IServiceProvider services)
    {
        All = runtime.Options.Projections.Select(p => new RegisteredProjection(p.Name, p.Run, p.Create(services))).ToList();
        Subscriptions = runtime.Options.Subscriptions.Select(s => new RegisteredSubscription(s.Name, s.Create(services))).ToList();

        foreach (var projection in All)
        {
            if (projection.Instance.IsBatch && projection.Run == Run.Inline)
            {
                throw new DeedboxException(Errors.InlineBatchProjection,
                    $"Batch projection '{projection.Name}' is registered inline. Batch projections run async; register it with Run.Async.");
            }

            CheckHandled(runtime, "Projection", projection.Name, projection.Instance.HandledTypes);
        }

        foreach (var subscription in Subscriptions)
            CheckHandled(runtime, "Subscription", subscription.Name, subscription.Instance.HandledTypes);

        Inline = All.Where(p => p.Run == Run.Inline).ToList();
        Async = All.Where(p => p.Run == Run.Async).ToList();
    }

    public IReadOnlyList<RegisteredSubscription> Subscriptions { get; }

    private static void CheckHandled(DeedboxRuntime runtime, string what, string name, IEnumerable<Type> types)
    {
        foreach (var type in types)
        {
            if (!runtime.Registry.IsRegistered(type))
            {
                throw new DeedboxException(Errors.ProjectionHandlesUnregistered,
                    $"{what} '{name}' handles {type.Name}, which is not a registered event. Register it on its stream, or remove the handler.");
            }
        }
    }

    public IReadOnlyList<RegisteredProjection> All { get; }

    public IReadOnlyList<RegisteredProjection> Inline { get; }

    public IReadOnlyList<RegisteredProjection> Async { get; }

    public static ProjectionRegistration Registration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(string name, Run run)
        where T : ProjectionBase =>
        new(name, run, typeof(T), services => ActivatorUtilities.CreateInstance<T>(services));
}
