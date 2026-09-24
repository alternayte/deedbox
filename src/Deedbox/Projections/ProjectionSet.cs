using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Deedbox;

internal sealed record ProjectionRegistration(
    string Name,
    Run Run,
    Type Type,
    Func<IServiceProvider, ProjectionBase> Create);

internal sealed record RegisteredProjection(string Name, Run Run, ProjectionBase Instance);

/// <summary>
/// The app's projections, created once from the root container. Handlers must keep no state between
/// events; per-event services come from <see cref="ProjectionContext.Services"/>.
/// </summary>
internal sealed class ProjectionSet
{
    public ProjectionSet(DeedboxRuntime runtime, IServiceProvider services)
    {
        All = runtime.Options.Projections.Select(p => new RegisteredProjection(p.Name, p.Run, p.Create(services))).ToList();
        foreach (var projection in All)
        {
            foreach (var type in projection.Instance.HandledTypes)
            {
                if (!runtime.Registry.IsRegistered(type))
                {
                    throw new DeedboxException(Errors.ProjectionHandlesUnregistered,
                        $"Projection '{projection.Name}' handles {type.Name}, which is not a registered event. Register it on its stream, or remove the handler.");
                }
            }
        }

        Inline = All.Where(p => p.Run == Run.Inline).ToList();
        Async = All.Where(p => p.Run == Run.Async).ToList();
    }

    public IReadOnlyList<RegisteredProjection> All { get; }

    public IReadOnlyList<RegisteredProjection> Inline { get; }

    public IReadOnlyList<RegisteredProjection> Async { get; }

    public static ProjectionRegistration Registration<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(string name, Run run)
        where T : ProjectionBase =>
        new(name, run, typeof(T), services => ActivatorUtilities.CreateInstance<T>(services));
}
