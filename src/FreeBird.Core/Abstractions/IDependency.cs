namespace FreeBird.Core.Abstractions;

/// <summary>
/// Marker interface for Autofac assembly-scan registration.
/// Types implementing this are auto-registered as their implemented interfaces
/// with InstancePerLifetimeScope (Corsair convention).
/// </summary>
public interface IDependency
{
}
